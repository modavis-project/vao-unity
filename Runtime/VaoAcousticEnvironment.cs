using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Modavis.Vao
{
    [DisallowMultipleComponent]
    public sealed class VaoAcousticEnvironment : MonoBehaviour, IVaoPackageConsumer
    {
        [SerializeField] private VaoPackageAsset package;
        [SerializeField] private int sceneIndex;
        [SerializeField] private bool createExplicitFallbackZone;
        [SerializeField] private AudioReverbPreset fallbackPreset = AudioReverbPreset.Auditorium;
        [SerializeField] private AudioReverbZone fallbackZone;
        [SerializeField] private Transform emitterAnchor;
        [SerializeField] private Transform receiverAnchor;
        [SerializeField] private MonoBehaviour rendererBehaviour;
        private readonly List<(GameObject voice, AudioSource source)> attachedVoices = new();

        public VaoPackageAsset Package { get => package; set { package = value; Apply(); } }
        public bool HasResponse => package != null && package.AcousticScenes.Count > 0;
        public AudioClip ImpulseResponse => HasResponse ? package.AcousticScenes[Mathf.Clamp(sceneIndex, 0, package.AcousticScenes.Count - 1)].ImpulseResponse : null;
        public IVaoAcousticRenderer Renderer => rendererBehaviour as IVaoAcousticRenderer;
        public IReadOnlyList<IVaoAcousticRenderer> AvailableRenderers => GetComponents<MonoBehaviour>().OfType<IVaoAcousticRenderer>().ToArray();
        public bool ConvolutionAvailable => Renderer?.IsReady == true;
        public bool NativeConvolutionAvailable => ConvolutionAvailable;
        public Transform EmitterAnchor { get => emitterAnchor; set => emitterAnchor = value; }
        public Transform ReceiverAnchor { get => receiverAnchor; set => receiverAnchor = value; }
        public int SceneIndex => sceneIndex;
        public event Action<IVaoAcousticRenderer> RendererChanged;
        public event Action<VaoAcousticSceneRecord> SceneChanged;
        public void SetPackage(VaoPackageAsset value) => Package = value;

        private void Awake() => Apply();

        public void Apply()
        {
            if (HasResponse)
            {
                var scene = package.AcousticScenes[Mathf.Clamp(sceneIndex, 0, package.AcousticScenes.Count - 1)];
                if (rendererBehaviour is not IVaoAcousticRenderer || rendererBehaviour is IVaoAcousticRendererCapabilities capabilities && !capabilities.CanRender(scene))
                    rendererBehaviour = BestRenderer(scene) as MonoBehaviour;
                if (Renderer is IVaoAcousticRendererCapabilities spatial) spatial.SetSpatialContext(emitterAnchor, receiverAnchor);
                Renderer?.Prepare(scene);
            }
            if (!createExplicitFallbackZone || !HasResponse) return;
            fallbackZone ??= GetComponent<AudioReverbZone>() ?? gameObject.AddComponent<AudioReverbZone>();
            fallbackZone.reverbPreset = fallbackPreset;
            fallbackZone.minDistance = 0f;
            fallbackZone.maxDistance = 10000f;
        }

        public void AttachVoice(GameObject voice, AudioSource source)
        {
            if (voice == null || source == null) return;
            attachedVoices.RemoveAll(item => item.voice == null || item.source == null);
            if (!attachedVoices.Any(item => item.voice == voice && item.source == source)) attachedVoices.Add((voice, source));
            if (HasResponse && Renderer?.IsReady == true) Renderer.AttachVoice(voice, source);
        }

        public bool SelectScene(int index)
        {
            if (package == null || index < 0 || index >= package.AcousticScenes.Count) return false;
            sceneIndex = index;
            Apply();
            SceneChanged?.Invoke(package.AcousticScenes[index]);
            ReattachVoices();
            return true;
        }

        public bool SelectRenderer(string rendererName)
        {
            if (!HasResponse || string.IsNullOrWhiteSpace(rendererName)) return false;
            var candidate = GetComponents<MonoBehaviour>().OfType<IVaoAcousticRenderer>()
                .FirstOrDefault(item => string.Equals(item.RendererName, rendererName, StringComparison.Ordinal));
            return SelectRenderer(candidate);
        }

        public bool SelectRenderer(IVaoAcousticRenderer candidate)
        {
            if (!HasResponse || candidate == null || candidate is not MonoBehaviour behaviour) return false;
            var scene = package.AcousticScenes[Mathf.Clamp(sceneIndex, 0, package.AcousticScenes.Count - 1)];
            if (candidate is IVaoAcousticRendererCapabilities capabilities && !capabilities.CanRender(scene)) return false;
            var previousBehaviour = rendererBehaviour;
            if (Renderer is IVaoSwitchableAcousticRenderer previous)
                foreach (var item in attachedVoices) if (item.voice != null && item.source != null) previous.DetachVoice(item.voice, item.source);
            rendererBehaviour = behaviour;
            if (candidate is IVaoAcousticRendererCapabilities spatial) spatial.SetSpatialContext(emitterAnchor, receiverAnchor);
            if (!candidate.Prepare(scene))
            {
                rendererBehaviour = previousBehaviour;
                if (Renderer is IVaoAcousticRendererCapabilities restoredSpatial) restoredSpatial.SetSpatialContext(emitterAnchor, receiverAnchor);
                Renderer?.Prepare(scene);
                ReattachVoices();
                return false;
            }
            ReattachVoices();
            RendererChanged?.Invoke(candidate);
            return true;
        }

        public bool SelectNextRenderer()
        {
            if (!HasResponse) return false;
            var compatible = CompatibleRenderers(package.AcousticScenes[Mathf.Clamp(sceneIndex, 0, package.AcousticScenes.Count - 1)]).ToList();
            if (compatible.Count == 0) return false;
            var current = compatible.FindIndex(item => ReferenceEquals(item, Renderer));
            return SelectRenderer(compatible[(current + 1 + compatible.Count) % compatible.Count]);
        }

        private IVaoAcousticRenderer BestRenderer(VaoAcousticSceneRecord scene) => CompatibleRenderers(scene)
            .OrderByDescending(item => item is IVaoAcousticRendererCapabilities capabilities ? capabilities.RendererPriority : 0)
            .ThenBy(item => item.RendererName, StringComparer.Ordinal).FirstOrDefault();

        private IEnumerable<IVaoAcousticRenderer> CompatibleRenderers(VaoAcousticSceneRecord scene) => GetComponents<MonoBehaviour>().OfType<IVaoAcousticRenderer>()
            .Where(item => item is not IVaoAcousticRendererCapabilities capabilities || capabilities.CanRender(scene));

        private void ReattachVoices()
        {
            attachedVoices.RemoveAll(item => item.voice == null || item.source == null);
            if (Renderer?.IsReady != true) return;
            foreach (var item in attachedVoices) Renderer.AttachVoice(item.voice, item.source);
        }

        public string DescribeCapability()
        {
            if (!HasResponse) return "No VAO acoustic response is declared.";
            var scene = package.AcousticScenes[Mathf.Clamp(sceneIndex, 0, package.AcousticScenes.Count - 1)];
            return ConvolutionAvailable
                ? $"Rendering {scene.RenderStrategy} with {scene.ResponseSetIdentifier} through {Renderer.RendererName}."
                : $"Response {scene.ResponseSetIdentifier} is imported and preserved; no compatible acoustic renderer is ready, so playback remains dry.";
        }
    }
}
