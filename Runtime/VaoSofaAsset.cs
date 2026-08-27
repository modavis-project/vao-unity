using System;
using System.Collections.Generic;
using UnityEngine;

namespace Modavis.Vao
{
    /// <summary>Managed, player-safe FIR data decoded from an AES69-SOFA realization during import.</summary>
    public sealed class VaoSofaAsset : ScriptableObject
    {
        [SerializeField] private string convention;
        [SerializeField] private string conventionVersion;
        [SerializeField] private string dataType;
        [SerializeField] private int measurementCount;
        [SerializeField] private int receiverCount;
        [SerializeField] private int filterLength;
        [SerializeField] private int sampleRate;
        [SerializeField] private Vector3[] sourcePositions = Array.Empty<Vector3>();
        [SerializeField] private float[] impulseResponses = Array.Empty<float>();
        [SerializeField] private float[] delaySamples = Array.Empty<float>();

        public string Convention => convention;
        public string ConventionVersion => conventionVersion;
        public string DataType => dataType;
        public int MeasurementCount => measurementCount;
        public int ReceiverCount => receiverCount;
        public int FilterLength => filterLength;
        public int SampleRate => sampleRate;
        public IReadOnlyList<Vector3> SourcePositions => sourcePositions;
        public IReadOnlyList<float> DelaySamples => delaySamples;

        internal void Initialize(string sofaConvention, string version, string type, int measurements, int receivers, int samples, int rate, Vector3[] positions, float[] responses, float[] delays)
        {
            if (measurements <= 0 || receivers <= 0 || samples <= 0 || rate <= 0) throw new ArgumentOutOfRangeException(nameof(measurements), "SOFA dimensions and sample rate must be positive.");
            if (responses == null || responses.Length != measurements * receivers * samples) throw new ArgumentException("SOFA FIR data does not match M x R x N dimensions.", nameof(responses));
            convention = sofaConvention;
            conventionVersion = version;
            dataType = type;
            measurementCount = measurements;
            receiverCount = receivers;
            filterLength = samples;
            sampleRate = rate;
            if (positions is { Length: > 0 } && positions.Length < measurements) throw new ArgumentException("SOFA source positions do not cover every measurement.", nameof(positions));
            sourcePositions = positions is { Length: > 0 } ? positions : new Vector3[measurements];
            impulseResponses = responses;
            delaySamples = delays ?? Array.Empty<float>();
        }

        public Vector3 GetSourcePosition(int measurementIndex) => sourcePositions[Mathf.Clamp(measurementIndex, 0, sourcePositions.Length - 1)];

        public float[] GetInterleavedResponse(int measurementIndex)
        {
            if (measurementIndex < 0 || measurementIndex >= measurementCount) throw new ArgumentOutOfRangeException(nameof(measurementIndex));
            var result = new float[filterLength * receiverCount];
            for (var receiver = 0; receiver < receiverCount; receiver++)
            {
                var sourceOffset = (measurementIndex * receiverCount + receiver) * filterLength;
                var delay = delaySamples.Length == measurementCount * receiverCount ? delaySamples[measurementIndex * receiverCount + receiver]
                    : delaySamples.Length == receiverCount ? delaySamples[receiver] : 0f;
                delay = Mathf.Max(0f, delay);
                var integerDelay = Mathf.FloorToInt(delay);
                var fraction = delay - integerDelay;
                for (var sample = 0; sample < filterLength; sample++)
                {
                    var target = sample + integerDelay;
                    var value = impulseResponses[sourceOffset + sample];
                    if (target < filterLength) result[target * receiverCount + receiver] += value * (1f - fraction);
                    if (fraction > 0f && target + 1 < filterLength) result[(target + 1) * receiverCount + receiver] += value * fraction;
                }
            }
            return result;
        }

        public VaoAcousticSelection Select(Vector3 direction, string interpolationMethod, int maximumResponses = 4)
        {
            if (measurementCount == 0) return VaoAcousticSelection.Empty;
            direction = direction.sqrMagnitude > 1e-8f ? direction.normalized : Vector3.forward;
            var distances = new List<(int index, float distance)>(measurementCount);
            for (var index = 0; index < measurementCount; index++)
            {
                var candidate = sourcePositions[index].sqrMagnitude > 1e-8f ? sourcePositions[index].normalized : Vector3.forward;
                distances.Add((index, Mathf.Max(0.000001f, 1f - Vector3.Dot(direction, candidate))));
            }
            distances.Sort((left, right) => left.distance != right.distance ? left.distance.CompareTo(right.distance) : left.index.CompareTo(right.index));
            var count = interpolationMethod is null or "none" or "nearest" ? 1 : Mathf.Clamp(maximumResponses, 1, distances.Count);
            var indices = new int[count];
            var weights = new float[count];
            if (distances[0].distance <= 0.000002f)
            {
                indices[0] = distances[0].index;
                weights[0] = 1f;
                return new VaoAcousticSelection(indices, weights);
            }
            var total = 0f;
            for (var item = 0; item < count; item++)
            {
                indices[item] = distances[item].index;
                weights[item] = 1f / distances[item].distance;
                total += weights[item];
            }
            for (var item = 0; item < count; item++) weights[item] /= total;
            return new VaoAcousticSelection(indices, weights);
        }
    }

    public readonly struct VaoAcousticSelection
    {
        public static readonly VaoAcousticSelection Empty = new(Array.Empty<int>(), Array.Empty<float>());
        public readonly int[] Indices;
        public readonly float[] Weights;
        public int Count => Indices?.Length ?? 0;

        public VaoAcousticSelection(int[] indices, float[] weights)
        {
            Indices = indices ?? Array.Empty<int>();
            Weights = weights ?? Array.Empty<float>();
        }
    }
}
