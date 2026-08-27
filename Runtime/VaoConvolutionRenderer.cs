using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace Modavis.Vao
{
    public interface IVaoAcousticRenderer
    {
        string RendererName { get; }
        bool IsReady { get; }
        string LastError { get; }
        bool Prepare(VaoAcousticSceneRecord scene);
        void AttachVoice(GameObject voice, AudioSource source);
    }

    public interface IVaoAcousticRendererCapabilities
    {
        int RendererPriority { get; }
        bool CanRender(VaoAcousticSceneRecord scene);
        void SetSpatialContext(Transform emitter, Transform receiver);
    }

    public interface IVaoSwitchableAcousticRenderer
    {
        void DetachVoice(GameObject voice, AudioSource source);
    }

    [DisallowMultipleComponent]
    public sealed class VaoConvolutionRenderer : MonoBehaviour, IVaoAcousticRenderer, IVaoAcousticRendererCapabilities, IVaoSwitchableAcousticRenderer
    {
        [SerializeField, Range(0f, 1f)] private float wet = 1f;
        [SerializeField, Range(0f, 1f)] private float dry;
        [SerializeField, Min(0f)] private float maximumImpulseResponseSeconds;
        [SerializeField, Range(1f, 60f)] private float spatialUpdateRate = 20f;
        [SerializeField, Range(1, 8)] private int maximumInterpolatedResponses = 4;
        private VaoConvolutionKernel kernel;
        private VaoAcousticSceneRecord scene;
        private Transform emitterAnchor;
        private Transform receiverAnchor;
        private float nextSpatialUpdate;
        private string selectionSignature;
        private readonly Dictionary<string, VaoConvolutionKernel> kernelCache = new(StringComparer.Ordinal);
        private readonly List<VaoConvolutionFilter> filters = new();

        public string RendererName => "VAO position-aware RIR/SOFA convolution";
        public int RendererPriority => 100;
        public bool IsReady => kernel != null;
        public string LastError { get; private set; }

        public bool Prepare(VaoAcousticSceneRecord scene)
        {
            kernel = null;
            this.scene = scene;
            selectionSignature = null;
            kernelCache.Clear();
            LastError = null;
            if (!CanRender(scene)) { LastError = "No materialized PCM or decoded AES69-SOFA impulse response is available."; return false; }
            try
            {
                UpdateSpatialKernel(true);
                return true;
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                Debug.LogError($"VAO convolution preparation failed: {exception.Message}", this);
                return false;
            }
        }

        public void AttachVoice(GameObject voice, AudioSource source)
        {
            if (kernel == null || voice == null || source == null) return;
            var filter = voice.GetComponent<VaoConvolutionFilter>() ?? voice.AddComponent<VaoConvolutionFilter>();
            filter.Initialize(kernel, Mathf.Max(1, source.clip != null ? source.clip.channels : 1), wet, dry, scene?.TransitionSeconds ?? 0f);
            if (!filters.Contains(filter)) filters.Add(filter);
        }

        public void DetachVoice(GameObject voice, AudioSource source)
        {
            if (voice == null) return;
            var filter = voice.GetComponent<VaoConvolutionFilter>();
            if (filter == null) return;
            filters.Remove(filter);
            filter.enabled = false;
            if (Application.isPlaying) Destroy(filter); else DestroyImmediate(filter);
        }

        public bool CanRender(VaoAcousticSceneRecord candidate)
            => candidate != null
               && candidate.RuntimeFeatures.All(item => item == null || item.Mode is "disabled" or "metadata" or "response-field")
               && (candidate.Sofa != null || candidate.ImpulseResponse != null || candidate.ResponsePoints.Any(item => item?.Sofa != null || item?.ImpulseResponse != null));

        public void SetSpatialContext(Transform emitter, Transform receiver)
        {
            emitterAnchor = emitter;
            receiverAnchor = receiver;
            if (scene != null && CanRender(scene)) UpdateSpatialKernel(true);
        }

        private void Update()
        {
            if (scene == null || Time.unscaledTime < nextSpatialUpdate) return;
            nextSpatialUpdate = Time.unscaledTime + 1f / Mathf.Max(1f, spatialUpdateRate);
            if (scene.Sofa != null || scene.ResponsePoints.Count > 1) UpdateSpatialKernel(false);
            filters.RemoveAll(item => item == null);
        }

        private void UpdateSpatialKernel(bool force)
        {
            AudioSettings.GetDSPBufferSize(out var blockSize, out _);
            blockSize = Mathf.Max(32, blockSize);
            var selectedKernels = new List<VaoConvolutionKernel>();
            var weights = new List<float>();
            string signature;
            if (scene.Sofa != null)
            {
                var direction = DirectionInListenerSpace();
                var selection = scene.Sofa.Select(direction, scene.InterpolationMethod, maximumInterpolatedResponses);
                signature = Signature(selection.Indices, selection.Weights);
                for (var item = 0; item < selection.Count; item++)
                {
                    selectedKernels.Add(SofaKernel(scene.Sofa, selection.Indices[item], blockSize));
                    weights.Add(selection.Weights[item]);
                }
            }
            else if (scene.ResponsePoints.Count > 0)
            {
                var selection = SelectResponsePoints();
                signature = Signature(selection.Indices, selection.Weights);
                for (var item = 0; item < selection.Count; item++)
                {
                    var point = scene.ResponsePoints[selection.Indices[item]];
                    var selected = point.Sofa != null ? SofaKernel(point.Sofa, Mathf.Max(0, point.SofaDataIndex), blockSize, point.DelaySamples)
                        : ClipKernel(point.ImpulseResponse, point.ChannelIndices, blockSize, point.DelaySamples);
                    if (selected == null) continue;
                    selectedKernels.Add(selected);
                    weights.Add(selection.Weights[item]);
                }
            }
            else
            {
                signature = "static";
                var selected = ClipKernel(scene.ImpulseResponse, null, blockSize, 0f);
                if (selected != null) { selectedKernels.Add(selected); weights.Add(1f); }
            }
            if (!force && signature == selectionSignature) return;
            if (selectedKernels.Count == 0) { kernel = null; LastError = "No acoustic response matches the current listener/source position."; return; }
            selectionSignature = signature;
            kernel = selectedKernels.Count == 1 ? selectedKernels[0] : VaoConvolutionKernel.Blend(selectedKernels, weights);
            foreach (var filter in filters.ToArray()) if (filter != null) filter.SetKernel(kernel, scene.TransitionSeconds);
        }

        private VaoAcousticSelection SelectResponsePoints()
        {
            var source = emitterAnchor != null ? transform.InverseTransformPoint(emitterAnchor.position) : Vector3.zero;
            var receiver = receiverAnchor != null ? transform.InverseTransformPoint(receiverAnchor.position) : Vector3.zero;
            var candidates = scene.ResponsePoints.Select((point, index) => (point, index))
                .Where(item => item.point?.Sofa != null || item.point?.ImpulseResponse != null)
                .Select(item => (item.index, distance: Vector3.Distance(source, item.point.SourcePosition) + Vector3.Distance(receiver, item.point.ReceiverPosition)))
                .OrderBy(item => item.distance).ThenBy(item => item.index).ToList();
            if (candidates.Count == 0) return VaoAcousticSelection.Empty;
            var interpolate = scene.InterpolationMethod is not null and not "none" and not "nearest";
            var count = interpolate ? Mathf.Min(maximumInterpolatedResponses, candidates.Count) : 1;
            var indices = new int[count];
            var weights = new float[count];
            if (candidates[0].distance <= 0.000001f) { indices[0] = candidates[0].index; weights[0] = 1f; return new VaoAcousticSelection(indices, weights); }
            var total = 0f;
            for (var item = 0; item < count; item++) { indices[item] = candidates[item].index; weights[item] = 1f / Mathf.Max(0.000001f, candidates[item].distance); total += weights[item]; }
            for (var item = 0; item < count; item++) weights[item] /= total;
            return new VaoAcousticSelection(indices, weights);
        }

        private Vector3 DirectionInListenerSpace()
        {
            if (emitterAnchor == null || receiverAnchor == null) return Vector3.forward;
            var direction = emitterAnchor.position - receiverAnchor.position;
            return direction.sqrMagnitude < 1e-8f ? Vector3.forward : receiverAnchor.InverseTransformDirection(direction.normalized);
        }

        private VaoConvolutionKernel SofaKernel(VaoSofaAsset sofa, int index, int blockSize, float additionalDelaySamples = 0f)
        {
            var key = $"sofa:{sofa.GetEntityId()}:{index}:{additionalDelaySamples:R}:{blockSize}:{AudioSettings.outputSampleRate}:{maximumImpulseResponseSeconds}";
            if (!kernelCache.TryGetValue(key, out var value))
            {
                value = VaoConvolutionKernel.FromInterleaved(sofa.GetInterleavedResponse(index), sofa.FilterLength, sofa.ReceiverCount, sofa.SampleRate, blockSize, AudioSettings.outputSampleRate, maximumImpulseResponseSeconds, additionalDelaySamples);
                kernelCache[key] = value;
            }
            return value;
        }

        private VaoConvolutionKernel ClipKernel(AudioClip clip, int[] channels, int blockSize, float delaySamples)
        {
            if (clip == null) return null;
            var channelKey = channels == null ? "all" : string.Join(",", channels);
            var key = $"clip:{clip.GetEntityId()}:{channelKey}:{delaySamples:R}:{blockSize}:{AudioSettings.outputSampleRate}:{maximumImpulseResponseSeconds}";
            if (!kernelCache.TryGetValue(key, out var value))
            {
                value = VaoConvolutionKernel.FromAudioClip(clip, blockSize, AudioSettings.outputSampleRate, maximumImpulseResponseSeconds, channels, delaySamples);
                kernelCache[key] = value;
            }
            return value;
        }

        private static string Signature(IReadOnlyList<int> indices, IReadOnlyList<float> weights)
        {
            var parts = new string[indices.Count];
            for (var item = 0; item < indices.Count; item++) parts[item] = indices[item] + ":" + Mathf.RoundToInt(weights[item] * 32f);
            return string.Join("|", parts);
        }
    }

    [DisallowMultipleComponent]
    public sealed class VaoConvolutionFilter : MonoBehaviour
    {
        private VaoConvolutionState state;
        private VaoConvolutionState previousState;
        private VaoConvolutionState pendingState;
        private VaoConvolutionKernel currentKernel;
        private VaoConvolutionKernel pendingKernel;
        private float pendingTransitionSeconds;
        private float[] previousOutput;
        private int crossfadeFrames;
        private int crossfadePosition;
        private int channels;
        private float wet;
        private float dry;

        internal void Initialize(VaoConvolutionKernel kernel, int channels, float wetMix, float dryMix, float transitionSeconds = 0f)
        {
            state = new VaoConvolutionState(kernel, channels);
            currentKernel = kernel;
            this.channels = channels;
            wet = wetMix;
            dry = dryMix;
            pendingTransitionSeconds = transitionSeconds;
        }

        internal void SetKernel(VaoConvolutionKernel kernel, float transitionSeconds)
        {
            if (kernel == null) return;
            pendingKernel = kernel;
            Interlocked.Exchange(ref pendingState, new VaoConvolutionState(kernel, Mathf.Max(1, channels)));
            pendingTransitionSeconds = Mathf.Max(0f, transitionSeconds);
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (data == null || channels <= 0) return;
            this.channels = channels;
            var pending = Interlocked.Exchange(ref pendingState, null);
            if (pending != null)
            {
                previousState = state;
                state = pending;
                currentKernel = pendingKernel;
                crossfadeFrames = Mathf.RoundToInt(pendingTransitionSeconds * AudioSettings.outputSampleRate);
                crossfadePosition = 0;
            }
            if (state == null) return;
            if (state.InputChannels != channels)
            {
                previousState = null;
                state = new VaoConvolutionState(currentKernel, channels);
            }
            if (previousState == null || crossfadeFrames <= 0)
            {
                previousState = null;
                state.ProcessInterleaved(data, channels, wet, dry);
                return;
            }
            if (previousOutput == null || previousOutput.Length != data.Length) previousOutput = new float[data.Length];
            Array.Copy(data, previousOutput, data.Length);
            var currentOk = state.ProcessInterleaved(data, channels, wet, dry);
            var previousOk = previousState.ProcessInterleaved(previousOutput, channels, wet, dry);
            if (!currentOk || !previousOk) { previousState = null; return; }
            var frames = data.Length / channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var blend = Mathf.Clamp01((crossfadePosition + frame) / (float)Mathf.Max(1, crossfadeFrames));
                for (var channel = 0; channel < channels; channel++)
                {
                    var offset = frame * channels + channel;
                    data[offset] = Mathf.Lerp(previousOutput[offset], data[offset], blend);
                }
            }
            crossfadePosition += frames;
            if (crossfadePosition >= crossfadeFrames) previousState = null;
        }
    }

    internal sealed class VaoConvolutionKernel
    {
        public int BlockSize { get; private set; }
        public int TransformSize { get; private set; }
        public int PartitionCount { get; private set; }
        public int Channels { get; private set; }
        public float[][][] Real { get; private set; }
        public float[][][] Imaginary { get; private set; }

        public static VaoConvolutionKernel FromAudioClip(AudioClip clip, int blockSize, int outputSampleRate, float maximumSeconds, int[] selectedChannels = null, float delaySamples = 0f)
        {
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            var source = new float[clip.samples * clip.channels];
            if (!clip.GetData(source, 0)) throw new InvalidOperationException($"Impulse response {clip.name} could not be decoded by Unity.");
            var frames = clip.samples;
            var channels = clip.channels;
            if (selectedChannels is { Length: > 0 })
            {
                var selected = new float[frames * selectedChannels.Length];
                for (var frame = 0; frame < frames; frame++)
                    for (var channel = 0; channel < selectedChannels.Length; channel++) selected[frame * selectedChannels.Length + channel] = source[frame * clip.channels + Mathf.Clamp(selectedChannels[channel], 0, clip.channels - 1)];
                source = selected;
                channels = selectedChannels.Length;
            }
            return FromInterleaved(source, frames, channels, clip.frequency, blockSize, outputSampleRate, maximumSeconds, delaySamples);
        }

        public static VaoConvolutionKernel FromInterleaved(float[] source, int frames, int channels, int sourceSampleRate, int blockSize, int outputSampleRate, float maximumSeconds, float delaySamples = 0f)
        {
            if (delaySamples > 0f) source = Delay(source, frames, channels, delaySamples);
            if (sourceSampleRate != outputSampleRate)
            {
                var targetFrames = Mathf.Max(1, Mathf.RoundToInt(frames * outputSampleRate / (float)sourceSampleRate));
                source = Resample(source, frames, channels, targetFrames);
                frames = targetFrames;
            }
            if (maximumSeconds > 0f) frames = Math.Min(frames, Mathf.CeilToInt(maximumSeconds * outputSampleRate));
            return Build(source, frames, channels, blockSize);
        }

        private static float[] Delay(float[] source, int frames, int channels, float delaySamples)
        {
            var result = new float[source.Length];
            var integerDelay = Mathf.FloorToInt(delaySamples);
            var fraction = delaySamples - integerDelay;
            for (var frame = 0; frame < frames; frame++)
                for (var channel = 0; channel < channels; channel++)
                {
                    var target = frame + integerDelay;
                    var value = source[frame * channels + channel];
                    if (target < frames) result[target * channels + channel] += value * (1f - fraction);
                    if (fraction > 0f && target + 1 < frames) result[(target + 1) * channels + channel] += value * fraction;
                }
            return result;
        }

        internal static VaoConvolutionKernel Blend(IReadOnlyList<VaoConvolutionKernel> kernels, IReadOnlyList<float> weights)
        {
            if (kernels == null || kernels.Count == 0 || weights == null || weights.Count != kernels.Count) throw new ArgumentException("Acoustic kernel blend requires matching kernels and weights.");
            var first = kernels[0];
            var channels = kernels.Max(item => item.Channels);
            var partitions = kernels.Max(item => item.PartitionCount);
            if (kernels.Any(item => item.BlockSize != first.BlockSize || item.TransformSize != first.TransformSize)) throw new ArgumentException("Acoustic kernels use incompatible partition sizes.");
            var result = new VaoConvolutionKernel
            {
                BlockSize = first.BlockSize, TransformSize = first.TransformSize, PartitionCount = partitions, Channels = channels,
                Real = new float[channels][][], Imaginary = new float[channels][][]
            };
            for (var channel = 0; channel < channels; channel++)
            {
                result.Real[channel] = new float[partitions][];
                result.Imaginary[channel] = new float[partitions][];
                for (var partition = 0; partition < partitions; partition++)
                {
                    var real = result.Real[channel][partition] = new float[first.TransformSize];
                    var imaginary = result.Imaginary[channel][partition] = new float[first.TransformSize];
                    for (var item = 0; item < kernels.Count; item++)
                    {
                        var source = kernels[item];
                        if (partition >= source.PartitionCount) continue;
                        var sourceChannel = Math.Min(channel, source.Channels - 1);
                        for (var bin = 0; bin < first.TransformSize; bin++)
                        {
                            real[bin] += source.Real[sourceChannel][partition][bin] * weights[item];
                            imaginary[bin] += source.Imaginary[sourceChannel][partition][bin] * weights[item];
                        }
                    }
                }
            }
            return result;
        }

        internal static VaoConvolutionKernel Build(float[] interleaved, int frames, int channels, int blockSize)
        {
            if (interleaved == null || frames <= 0 || channels <= 0 || interleaved.Length < frames * channels) throw new ArgumentException("Invalid impulse-response data.");
            var size = 1;
            while (size < blockSize * 2) size <<= 1;
            blockSize = size / 2;
            var partitions = Mathf.CeilToInt(frames / (float)blockSize);
            var kernel = new VaoConvolutionKernel
            {
                BlockSize = blockSize, TransformSize = size, PartitionCount = partitions, Channels = channels,
                Real = new float[channels][][], Imaginary = new float[channels][][]
            };
            for (var channel = 0; channel < channels; channel++)
            {
                kernel.Real[channel] = new float[partitions][];
                kernel.Imaginary[channel] = new float[partitions][];
                for (var partition = 0; partition < partitions; partition++)
                {
                    var real = new float[size];
                    var imaginary = new float[size];
                    var offset = partition * blockSize;
                    for (var index = 0; index < blockSize && offset + index < frames; index++) real[index] = interleaved[(offset + index) * channels + channel];
                    VaoFastFourierTransform.Transform(real, imaginary, false);
                    kernel.Real[channel][partition] = real;
                    kernel.Imaginary[channel][partition] = imaginary;
                }
            }
            return kernel;
        }

        private static float[] Resample(float[] source, int frames, int channels, int targetFrames)
        {
            var result = new float[targetFrames * channels];
            var ratio = frames / (double)targetFrames;
            for (var frame = 0; frame < targetFrames; frame++)
            {
                var position = Math.Min(frames - 1d, frame * ratio);
                var left = (int)position;
                var right = Math.Min(frames - 1, left + 1);
                var blend = (float)(position - left);
                for (var channel = 0; channel < channels; channel++) result[frame * channels + channel] = Mathf.Lerp(source[left * channels + channel], source[right * channels + channel], blend);
            }
            return result;
        }
    }

    internal sealed class VaoConvolutionState
    {
        private readonly VaoConvolutionKernel kernel;
        private readonly int channels;
        private readonly float[][][] inputReal;
        private readonly float[][][] inputImaginary;
        private readonly float[][] overlap;
        private readonly float[][] outputReal;
        private readonly float[][] outputImaginary;
        private int ringIndex;
        public int InputChannels => channels;

        public VaoConvolutionState(VaoConvolutionKernel kernel, int channels)
        {
            this.kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
            this.channels = channels;
            inputReal = new float[channels][][];
            inputImaginary = new float[channels][][];
            overlap = new float[channels][];
            outputReal = new float[channels][];
            outputImaginary = new float[channels][];
            for (var channel = 0; channel < channels; channel++)
            {
                inputReal[channel] = new float[kernel.PartitionCount][];
                inputImaginary[channel] = new float[kernel.PartitionCount][];
                for (var partition = 0; partition < kernel.PartitionCount; partition++)
                {
                    inputReal[channel][partition] = new float[kernel.TransformSize];
                    inputImaginary[channel][partition] = new float[kernel.TransformSize];
                }
                overlap[channel] = new float[kernel.BlockSize];
                outputReal[channel] = new float[kernel.TransformSize];
                outputImaginary[channel] = new float[kernel.TransformSize];
            }
        }

        public bool ProcessInterleaved(float[] data, int dataChannels, float wet, float dry)
        {
            if (data == null || dataChannels != channels || data.Length / dataChannels != kernel.BlockSize) return false;
            for (var channel = 0; channel < channels; channel++)
            {
                var currentReal = inputReal[channel][ringIndex];
                var currentImaginary = inputImaginary[channel][ringIndex];
                Array.Clear(currentReal, 0, currentReal.Length);
                Array.Clear(currentImaginary, 0, currentImaginary.Length);
                for (var frame = 0; frame < kernel.BlockSize; frame++) currentReal[frame] = data[frame * channels + channel];
                VaoFastFourierTransform.Transform(currentReal, currentImaginary, false);

                var sumReal = outputReal[channel];
                var sumImaginary = outputImaginary[channel];
                Array.Clear(sumReal, 0, sumReal.Length);
                Array.Clear(sumImaginary, 0, sumImaginary.Length);
                var responseChannel = Math.Min(channel, kernel.Channels - 1);
                for (var partition = 0; partition < kernel.PartitionCount; partition++)
                {
                    var inputIndex = ringIndex - partition;
                    if (inputIndex < 0) inputIndex += kernel.PartitionCount;
                    var xr = inputReal[channel][inputIndex];
                    var xi = inputImaginary[channel][inputIndex];
                    var hr = kernel.Real[responseChannel][partition];
                    var hi = kernel.Imaginary[responseChannel][partition];
                    for (var bin = 0; bin < kernel.TransformSize; bin++)
                    {
                        sumReal[bin] += xr[bin] * hr[bin] - xi[bin] * hi[bin];
                        sumImaginary[bin] += xr[bin] * hi[bin] + xi[bin] * hr[bin];
                    }
                }
                VaoFastFourierTransform.Transform(sumReal, sumImaginary, true);
                for (var frame = 0; frame < kernel.BlockSize; frame++)
                {
                    var offset = frame * channels + channel;
                    var convolved = sumReal[frame] + overlap[channel][frame];
                    data[offset] = data[offset] * dry + convolved * wet;
                    overlap[channel][frame] = sumReal[frame + kernel.BlockSize];
                }
            }
            ringIndex = (ringIndex + 1) % kernel.PartitionCount;
            return true;
        }
    }

    internal static class VaoFastFourierTransform
    {
        public static void Transform(float[] real, float[] imaginary, bool inverse)
        {
            var length = real.Length;
            for (int index = 1, reverse = 0; index < length; index++)
            {
                var bit = length >> 1;
                for (; (reverse & bit) != 0; bit >>= 1) reverse ^= bit;
                reverse ^= bit;
                if (index >= reverse) continue;
                (real[index], real[reverse]) = (real[reverse], real[index]);
                (imaginary[index], imaginary[reverse]) = (imaginary[reverse], imaginary[index]);
            }
            for (var size = 2; size <= length; size <<= 1)
            {
                var angle = (inverse ? 2d : -2d) * Math.PI / size;
                var stepReal = (float)Math.Cos(angle);
                var stepImaginary = (float)Math.Sin(angle);
                for (var start = 0; start < length; start += size)
                {
                    var wr = 1f; var wi = 0f;
                    for (var offset = 0; offset < size / 2; offset++)
                    {
                        var even = start + offset; var odd = even + size / 2;
                        var tr = wr * real[odd] - wi * imaginary[odd];
                        var ti = wr * imaginary[odd] + wi * real[odd];
                        real[odd] = real[even] - tr; imaginary[odd] = imaginary[even] - ti;
                        real[even] += tr; imaginary[even] += ti;
                        var next = wr * stepReal - wi * stepImaginary;
                        wi = wr * stepImaginary + wi * stepReal; wr = next;
                    }
                }
            }
            if (!inverse) return;
            for (var index = 0; index < length; index++) { real[index] /= length; imaginary[index] /= length; }
        }
    }
}
