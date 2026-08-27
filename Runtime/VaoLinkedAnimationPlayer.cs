using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Modavis.Vao
{
    public enum VaoAnimationBackend { Auto, PlayableGraph, LegacyAnimation }

    [Serializable]
    public sealed class VaoAnimationLayerConfiguration
    {
        public string LinkIdentifier;
        public int LayerOrder;
        public bool Additive;
        [Range(0f, 1f)] public float Weight = 1f;
        [Min(0f)] public float BlendSeconds = 0.08f;
        [Min(0f)] public float Speed = 1f;
        public AvatarMask Mask;
        public AnimationCurve SpeedCurve = AnimationCurve.Linear(0f, 1f, 1f, 1f);
    }

    [Serializable]
    public sealed class VaoAnimationSequenceStep
    {
        public int LinkIndex;
        [Range(0f, 1f)] public float StartNormalizedTime;
        [Range(0f, 1f)] public float EndNormalizedTime = 1f;
        [Min(0.0001f)] public float Speed = 1f;
        [Range(0f, 1f)] public float Weight = 1f;
        [Min(0f)] public float FadeSeconds = 0.08f;
        [Min(0f)] public float HoldSeconds;
        public bool RewindAfterStep;
    }

    [Serializable]
    public sealed class VaoAnimationSequence
    {
        public string Identifier;
        public bool Loop;
        public List<VaoAnimationSequenceStep> Steps = new();
    }

    /// <summary>
    /// Drives VAO-linked clips through an Animator-backed PlayableGraph and keeps
    /// a legacy Animation fallback for legacy clips. Each declared link is an
    /// independent mixer layer with optional masks, additive blending, weights,
    /// cross-fades, speed curves, and sequence playback.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VaoLinkedAnimationPlayer : MonoBehaviour, IVaoPackageConsumer
    {
        [SerializeField] private VaoPackageAsset package;
        [SerializeField] private Transform targetRoot;
        [SerializeField] private List<VaoAnimationTargetRoot> targetRoots = new();
        [SerializeField] private VaoAnimationBackend backend = VaoAnimationBackend.Auto;
        [SerializeField] private bool preserveAnimatorController = true;
        [SerializeField] private List<VaoAnimationLayerConfiguration> layerConfigurations = new();
        [SerializeField] private List<VaoAnimationSequence> sequences = new();
        [SerializeField, Min(0f)] private float transitionSeconds = 0.04f;

        private readonly Dictionary<(int Link, int Note), Quaternion> restRotations = new();
        private readonly Dictionary<(int Link, int Note), Transform> targets = new();
        private readonly Dictionary<(int Link, int Note), Coroutine> transitions = new();
        private readonly Dictionary<Transform, GraphContext> graphByRoot = new();
        private readonly Dictionary<int, PlayableLinkState> playableByLink = new();
        private readonly Dictionary<int, Coroutine> weightTransitions = new();
        private Coroutine activeSequence;

        public VaoPackageAsset Package { get => package; set { package = value; RebuildTargets(); } }
        public Transform TargetRoot { get => targetRoot != null ? targetRoot : transform; set { targetRoot = value; RebuildTargets(); } }
        public IReadOnlyList<VaoAnimationTargetRoot> TargetRoots => targetRoots;
        public IReadOnlyList<VaoAnimationLayerConfiguration> LayerConfigurations => layerConfigurations;
        public IReadOnlyList<VaoAnimationSequence> Sequences => sequences;
        public VaoAnimationBackend Backend { get => backend; set { if (backend == value) return; backend = value; DestroyGraphs(); } }
        public bool PreserveAnimatorController { get => preserveAnimatorController; set { if (preserveAnimatorController == value) return; preserveAnimatorController = value; DestroyGraphs(); } }
        public bool IsSequencePlaying => activeSequence != null;

        public void SetPackage(VaoPackageAsset value) => Package = value;

        public void SetTargetRoot(string logicalAssetIdentifier, Transform root)
        {
            var item = targetRoots.FirstOrDefault(value => value.LogicalAssetIdentifier == logicalAssetIdentifier);
            if (item == null) targetRoots.Add(new VaoAnimationTargetRoot { LogicalAssetIdentifier = logicalAssetIdentifier, Root = root });
            else item.Root = root;
            if (targetRoot == null) targetRoot = root;
            RebuildTargets();
        }

        public void SetLayerConfiguration(VaoAnimationLayerConfiguration configuration)
        {
            if (configuration == null || string.IsNullOrEmpty(configuration.LinkIdentifier)) throw new ArgumentException("A linked-animation layer configuration needs a link identifier.", nameof(configuration));
            var index = layerConfigurations.FindIndex(item => item.LinkIdentifier == configuration.LinkIdentifier);
            if (index < 0) layerConfigurations.Add(configuration); else layerConfigurations[index] = configuration;
            DestroyGraphs();
        }

        public void AddSequence(VaoAnimationSequence sequence)
        {
            if (sequence == null) throw new ArgumentNullException(nameof(sequence));
            var index = sequences.FindIndex(item => item.Identifier == sequence.Identifier && !string.IsNullOrEmpty(sequence.Identifier));
            if (index < 0) sequences.Add(sequence); else sequences[index] = sequence;
        }

        private void Awake() => RebuildTargets();

        private void Update()
        {
            foreach (var state in playableByLink.Values)
            {
                if (!state.Playing || !state.Playable.IsValid() || state.Length <= 0d) continue;
                var time = state.Playable.GetTime();
                if (state.Loop && (time >= state.Length || time < 0d))
                {
                    time = (time % state.Length + state.Length) % state.Length;
                    state.Playable.SetTime(time);
                }
                else if (!state.Loop && time >= state.Length)
                {
                    state.Playable.SetTime(state.Length);
                    state.Playable.SetSpeed(0d);
                    state.Playing = false;
                    continue;
                }
                else if (!state.Loop && time <= 0d && state.SpeedScale < 0f)
                {
                    state.Playable.SetTime(0d);
                    state.Playable.SetSpeed(0d);
                    state.Playing = false;
                    continue;
                }
                var normalized = Mathf.Clamp01((float)(time / state.Length));
                var curve = state.Configuration.SpeedCurve;
                var curveScale = curve == null || curve.length == 0 ? 1f : Mathf.Max(0f, curve.Evaluate(normalized));
                state.Playable.SetSpeed(state.SpeedScale * state.Configuration.Speed * curveScale);
            }
        }

        public void RebuildTargets()
        {
            DestroyGraphs();
            targets.Clear();
            restRotations.Clear();
            if (package == null) return;
            for (var linkIndex = 0; linkIndex < package.AnimationLinks.Count; linkIndex++)
            {
                var link = package.AnimationLinks[linkIndex];
                var root = RootFor(link);
                for (var note = Mathf.Clamp(link.MinimumMidiNote, 0, 127); note <= Mathf.Clamp(link.MaximumMidiNote, 0, 127); note++)
                {
                    var path = (link.TargetPathPattern ?? "{midiNote}").Replace("{midiNote}", note.ToString());
                    var target = FindByPathOrName(root, path);
                    if (target == null) continue;
                    var key = (linkIndex, note);
                    targets[key] = target;
                    restRotations[key] = target.localRotation;
                }
            }
        }

        public void NoteOn(int midiNote)
        {
            if (package == null) return;
            for (var linkIndex = 0; linkIndex < package.AnimationLinks.Count; linkIndex++)
            {
                var key = (linkIndex, midiNote);
                if (!targets.TryGetValue(key, out var target)) continue;
                var link = package.AnimationLinks[linkIndex];
                var pressed = restRotations[key] * Quaternion.AngleAxis(link.PressedAngleDegrees, link.RotationAxis.sqrMagnitude > 0f ? link.RotationAxis.normalized : Vector3.right);
                StartTransition(key, target, pressed);
            }
        }

        public void NoteOff(int midiNote)
        {
            foreach (var key in targets.Keys.Where(item => item.Note == midiNote).ToArray())
                if (targets.TryGetValue(key, out var target) && restRotations.TryGetValue(key, out var rest)) StartTransition(key, target, rest);
        }

        public void PlayLinkedClip(int index = 0) => PlayLinkedClip(index, 0f, false);

        public void PlayLinkedClip(int index, float timeSeconds, bool loop)
        {
            if (ShouldUsePlayable(index) && TryGetPlayable(index, out var state))
            {
                state.Loop = loop;
                state.Playing = true;
                state.SpeedScale = 1f;
                state.Playable.SetTime(Mathf.Clamp(timeSeconds, 0f, (float)state.Length));
                state.Playable.SetSpeed(EffectiveSpeed(state));
                BlendLinkedClipWeight(index, state.Configuration.Weight, state.Configuration.BlendSeconds);
                state.Context.Graph.Evaluate(0f);
                return;
            }
            var legacy = PrepareLegacyState(index, out var animation);
            if (legacy == null) return;
            legacy.wrapMode = loop ? WrapMode.Loop : WrapMode.Once;
            animation.Play(legacy.name);
            legacy.time = Mathf.Clamp(timeSeconds, 0f, legacy.length);
            legacy.speed = 1f;
        }

        public void PauseLinkedClip(int index)
        {
            if (playableByLink.TryGetValue(index, out var playable))
            {
                playable.Playing = false;
                playable.Playable.SetSpeed(0d);
                return;
            }
            var state = PrepareLegacyState(index, out _);
            if (state != null) state.speed = 0f;
        }

        public void ResumeLinkedClip(int index)
        {
            if (ShouldUsePlayable(index) && TryGetPlayable(index, out var playable))
            {
                playable.Playing = true;
                playable.Playable.SetSpeed(EffectiveSpeed(playable));
                if (playable.Context.Mixer.GetInputWeight(playable.MixerInput) <= 0f) playable.Context.Mixer.SetInputWeight(playable.MixerInput, playable.Configuration.Weight);
                return;
            }
            var state = PrepareLegacyState(index, out var animation);
            if (state == null) return;
            if (!animation.IsPlaying(state.name)) animation.Play(state.name);
            state.speed = 1f;
        }

        public void StopLinkedClip(int index, bool rewind = true)
        {
            if (playableByLink.TryGetValue(index, out var playable) || ShouldUsePlayable(index) && TryGetPlayable(index, out playable))
            {
                playable.Playing = false;
                playable.Playable.SetSpeed(0d);
                if (rewind) playable.Playable.SetTime(0d);
                BlendLinkedClipWeight(index, 0f, playable.Configuration.BlendSeconds);
                playable.Context.Graph.Evaluate(0f);
                return;
            }
            var state = PrepareLegacyState(index, out var animation);
            if (state == null) return;
            animation.Stop(state.name);
            if (!rewind) return;
            state.enabled = true;
            state.weight = 1f;
            state.time = 0f;
            animation.Sample();
            state.enabled = false;
        }

        public void SetLinkedClipNormalizedTime(int index, float normalizedTime, bool keepPlaying)
        {
            if (ShouldUsePlayable(index) && TryGetPlayable(index, out var playable))
            {
                playable.Playable.SetTime(Mathf.Clamp01(normalizedTime) * playable.Length);
                playable.Playing = keepPlaying;
                playable.Playable.SetSpeed(keepPlaying ? EffectiveSpeed(playable) : 0d);
                if (playable.Context.Mixer.GetInputWeight(playable.MixerInput) <= 0f) playable.Context.Mixer.SetInputWeight(playable.MixerInput, playable.Configuration.Weight);
                playable.Context.Graph.Evaluate(0f);
                return;
            }
            var state = PrepareLegacyState(index, out var animation);
            if (state == null) return;
            state.enabled = true;
            state.weight = 1f;
            state.time = Mathf.Clamp01(normalizedTime) * state.length;
            state.speed = keepPlaying ? 1f : 0f;
            animation.Sample();
        }

        public void SetLinkedClipWeight(int index, float weight)
        {
            if (TryGetPlayable(index, out var state)) state.Context.Mixer.SetInputWeight(state.MixerInput, Mathf.Clamp01(weight));
        }

        public float GetLinkedClipWeight(int index)
            => TryGetPlayable(index, out var state) ? state.Context.Mixer.GetInputWeight(state.MixerInput) : 0f;

        public void BlendLinkedClipWeight(int index, float weight, float seconds)
        {
            if (!TryGetPlayable(index, out _)) return;
            if (weightTransitions.TryGetValue(index, out var active) && active != null) StopCoroutine(active);
            weightTransitions[index] = StartCoroutine(BlendWeight(index, Mathf.Clamp01(weight), Mathf.Max(0f, seconds)));
        }

        public void CrossFadeLinkedClips(int fromIndex, int toIndex, float seconds, float toTimeSeconds = 0f, bool loop = false)
        {
            PlayLinkedClip(toIndex, toTimeSeconds, loop);
            BlendLinkedClipWeight(fromIndex, 0f, seconds);
            BlendLinkedClipWeight(toIndex, ConfigurationFor(toIndex).Weight, seconds);
        }

        public float GetLinkedClipTime(int index)
        {
            if (playableByLink.TryGetValue(index, out var playable)) return (float)playable.Playable.GetTime();
            if (ShouldUsePlayable(index) && TryGetPlayable(index, out playable)) return (float)playable.Playable.GetTime();
            var state = PrepareLegacyState(index, out _);
            return state?.time ?? 0f;
        }

        public float GetLinkedClipLength(int index)
        {
            if (playableByLink.TryGetValue(index, out var playable)) return (float)playable.Length;
            if (ShouldUsePlayable(index) && TryGetPlayable(index, out playable)) return (float)playable.Length;
            var state = PrepareLegacyState(index, out _);
            return state?.length ?? 0f;
        }

        public VaoAnimationBackend GetActiveBackend(int index)
        {
            if (!ValidLink(index)) return backend;
            return ShouldUsePlayable(index) ? VaoAnimationBackend.PlayableGraph : VaoAnimationBackend.LegacyAnimation;
        }

        public bool PlaySequence(string identifier) => PlaySequence(sequences.FirstOrDefault(item => item.Identifier == identifier));

        public bool PlaySequence(VaoAnimationSequence sequence)
        {
            if (sequence == null || sequence.Steps.Count == 0) return false;
            StopSequence();
            activeSequence = StartCoroutine(RunSequence(sequence));
            return true;
        }

        public void StopSequence()
        {
            if (activeSequence != null) StopCoroutine(activeSequence);
            activeSequence = null;
        }

        private IEnumerator RunSequence(VaoAnimationSequence sequence)
        {
            do
            {
                foreach (var step in sequence.Steps)
                {
                    if (!ValidLink(step.LinkIndex)) continue;
                    var length = GetLinkedClipLength(step.LinkIndex);
                    if (length <= 0f) continue;
                    var start = Mathf.Clamp01(step.StartNormalizedTime);
                    var end = Mathf.Clamp01(step.EndNormalizedTime);
                    PlayLinkedClip(step.LinkIndex, start * length, false);
                    if (playableByLink.TryGetValue(step.LinkIndex, out var state))
                    {
                        state.SpeedScale = Mathf.Max(0.0001f, step.Speed) * (end >= start ? 1f : -1f);
                        state.Playable.SetSpeed(EffectiveSpeed(state));
                    }
                    BlendLinkedClipWeight(step.LinkIndex, step.Weight, step.FadeSeconds);
                    while (true)
                    {
                        var normalized = Mathf.Clamp01(GetLinkedClipTime(step.LinkIndex) / length);
                        if (end >= start ? normalized >= end : normalized <= end) break;
                        yield return null;
                    }
                    SetLinkedClipNormalizedTime(step.LinkIndex, end, false);
                    if (step.HoldSeconds > 0f) yield return new WaitForSeconds(step.HoldSeconds);
                    if (step.RewindAfterStep) StopLinkedClip(step.LinkIndex, true);
                }
            } while (sequence.Loop);
            activeSequence = null;
        }

        private IEnumerator BlendWeight(int index, float destination, float seconds)
        {
            if (!playableByLink.TryGetValue(index, out var state)) { weightTransitions.Remove(index); yield break; }
            var start = state.Context.Mixer.GetInputWeight(state.MixerInput);
            if (seconds <= 0f)
            {
                state.Context.Mixer.SetInputWeight(state.MixerInput, destination);
                weightTransitions.Remove(index);
                yield break;
            }
            for (var elapsed = 0f; elapsed < seconds; elapsed += Time.unscaledDeltaTime)
            {
                if (!state.Context.Mixer.IsValid()) break;
                state.Context.Mixer.SetInputWeight(state.MixerInput, Mathf.Lerp(start, destination, elapsed / seconds));
                yield return null;
            }
            if (state.Context.Mixer.IsValid()) state.Context.Mixer.SetInputWeight(state.MixerInput, destination);
            weightTransitions.Remove(index);
        }

        private void StartTransition((int Link, int Note) key, Transform target, Quaternion destination)
        {
            if (transitions.TryGetValue(key, out var active) && active != null) StopCoroutine(active);
            transitions[key] = StartCoroutine(Rotate(key, target, destination));
        }

        private IEnumerator Rotate((int Link, int Note) key, Transform target, Quaternion destination)
        {
            if (target == null) { transitions.Remove(key); yield break; }
            var start = target.localRotation;
            if (transitionSeconds <= 0f) { target.localRotation = destination; transitions.Remove(key); yield break; }
            for (var elapsed = 0f; elapsed < transitionSeconds; elapsed += Time.unscaledDeltaTime)
            {
                if (target == null) { transitions.Remove(key); yield break; }
                target.localRotation = Quaternion.Slerp(start, destination, elapsed / transitionSeconds);
                yield return null;
            }
            if (target != null) target.localRotation = destination;
            transitions.Remove(key);
        }

        private Transform RootFor(VaoAnimationLink link) => targetRoots.FirstOrDefault(item => item.LogicalAssetIdentifier == link.TargetLogicalAssetIdentifier && item.Root != null)?.Root ?? TargetRoot;

        private bool ShouldUsePlayable(int index)
        {
            if (!ValidLink(index) || backend == VaoAnimationBackend.LegacyAnimation) return false;
            var clip = ClipFor(index);
            if (clip == null) return false;
            if (clip.legacy)
            {
                if (backend == VaoAnimationBackend.PlayableGraph) Debug.LogWarning($"VAO linked clip '{clip.name}' is a legacy Animation clip and cannot be evaluated by PlayableGraph; using the legacy fallback.", this);
                return false;
            }
            return true;
        }

        private bool TryGetPlayable(int index, out PlayableLinkState state)
        {
            if (playableByLink.TryGetValue(index, out state) && state.Playable.IsValid()) return true;
            state = null;
            if (!ShouldUsePlayable(index)) return false;
            var root = RootFor(package.AnimationLinks[index]);
            if (root == null) return false;
            BuildGraph(root);
            return playableByLink.TryGetValue(index, out state) && state.Playable.IsValid();
        }

        private void BuildGraph(Transform root)
        {
            if (root == null || graphByRoot.ContainsKey(root)) return;
            var indices = Enumerable.Range(0, package.AnimationLinks.Count)
                .Where(index => RootFor(package.AnimationLinks[index]) == root && ShouldUsePlayable(index))
                .OrderBy(index => ConfigurationFor(index).LayerOrder).ThenBy(index => index).ToArray();
            if (indices.Length == 0) return;

            var animator = root.GetComponent<Animator>();
            if (animator == null) animator = root.gameObject.AddComponent<Animator>();
            var graph = PlayableGraph.Create($"VAO Linked Animations — {root.name}");
            graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            var includeController = preserveAnimatorController && animator.runtimeAnimatorController != null;
            var mixer = AnimationLayerMixerPlayable.Create(graph, indices.Length + (includeController ? 1 : 0));
            var context = new GraphContext { Root = root, Animator = animator, Graph = graph, Mixer = mixer };
            var input = 0;
            if (includeController)
            {
                var controller = AnimatorControllerPlayable.Create(graph, animator.runtimeAnimatorController);
                graph.Connect(controller, 0, mixer, input);
                mixer.SetInputWeight(input, 1f);
                context.Controller = controller;
                input++;
            }
            foreach (var index in indices)
            {
                var clip = ClipFor(index);
                var playable = AnimationClipPlayable.Create(graph, clip);
                playable.SetApplyFootIK(false);
                playable.SetApplyPlayableIK(false);
                playable.SetSpeed(0d);
                playable.SetTime(0d);
                graph.Connect(playable, 0, mixer, input);
                mixer.SetInputWeight(input, 0f);
                var configuration = ConfigurationFor(index);
                mixer.SetLayerAdditive((uint)input, configuration.Additive);
                if (configuration.Mask != null) mixer.SetLayerMaskFromAvatarMask((uint)input, configuration.Mask);
                playableByLink[index] = new PlayableLinkState
                {
                    Context = context, Playable = playable, MixerInput = input,
                    Configuration = configuration, Length = Math.Max(clip.length, 0.0001f), SpeedScale = 1f
                };
                input++;
            }
            var output = AnimationPlayableOutput.Create(graph, "VAO Animation", animator);
            output.SetSourcePlayable(mixer);
            context.Output = output;
            graph.Play();
            graph.Evaluate(0f);
            graphByRoot[root] = context;
        }

        private VaoAnimationLayerConfiguration ConfigurationFor(int index)
        {
            var identifier = ValidLink(index) ? package.AnimationLinks[index].Identifier : null;
            var configured = layerConfigurations.FirstOrDefault(item => item.LinkIdentifier == identifier);
            if (configured != null) return configured;
            var link = package.AnimationLinks[index];
            return new VaoAnimationLayerConfiguration
            {
                LinkIdentifier = identifier, LayerOrder = link.LayerOrder, Additive = link.Additive,
                Weight = Mathf.Clamp01(link.Weight), BlendSeconds = Mathf.Max(0f, link.BlendSeconds), Speed = Mathf.Max(0f, link.PlaybackSpeed),
                Mask = link.Mask, SpeedCurve = link.SpeedCurve ?? AnimationCurve.Linear(0f, 1f, 1f, 1f)
            };
        }

        private AnimationState PrepareLegacyState(int index, out Animation animation)
        {
            animation = null;
            if (!ValidLink(index)) return null;
            var clip = ClipFor(index);
            var root = RootFor(package.AnimationLinks[index]);
            if (clip == null || root == null) return null;
            animation = root.GetComponent<Animation>();
            if (animation == null) animation = root.gameObject.AddComponent<Animation>();
            if (!clip.legacy)
            {
                Debug.LogWarning($"VAO linked clip '{clip.name}' is not legacy and requires the PlayableGraph backend.", this);
                return null;
            }
            if (animation.GetClip(clip.name) != clip)
            {
                if (animation.GetClip(clip.name) != null) animation.RemoveClip(clip.name);
                animation.AddClip(clip, clip.name);
            }
            return animation[clip.name];
        }

        private AnimationClip ClipFor(int index) => ValidLink(index) ? package.AnimationLinks[index].GeneratedMidiClip ?? package.AnimationLinks[index].SourceClip : null;
        private bool ValidLink(int index) => package != null && index >= 0 && index < package.AnimationLinks.Count;
        private static double EffectiveSpeed(PlayableLinkState state)
        {
            var curve = state.Configuration.SpeedCurve;
            var normalized = state.Length <= 0d ? 0f : Mathf.Clamp01((float)(state.Playable.GetTime() / state.Length));
            var curveScale = curve == null || curve.length == 0 ? 1f : Mathf.Max(0f, curve.Evaluate(normalized));
            return state.SpeedScale * state.Configuration.Speed * curveScale;
        }

        private void DestroyGraphs()
        {
            foreach (var coroutine in weightTransitions.Values.Where(item => item != null).ToArray()) StopCoroutine(coroutine);
            weightTransitions.Clear();
            playableByLink.Clear();
            foreach (var context in graphByRoot.Values) if (context.Graph.IsValid()) context.Graph.Destroy();
            graphByRoot.Clear();
        }

        private void OnDisable() { StopSequence(); DestroyGraphs(); }
        private void OnDestroy() => DestroyGraphs();

        private static Transform FindByPathOrName(Transform root, string path)
        {
            if (root == null) return null;
            var direct = root.Find(path);
            if (direct != null) return direct;
            var leaf = path.Contains("/") ? path[(path.LastIndexOf('/') + 1)..] : path;
            foreach (var item in root.GetComponentsInChildren<Transform>(true)) if (item.name == leaf || item.name == path) return item;
            return null;
        }

        private sealed class GraphContext
        {
            public Transform Root;
            public Animator Animator;
            public PlayableGraph Graph;
            public AnimationLayerMixerPlayable Mixer;
            public AnimatorControllerPlayable Controller;
            public AnimationPlayableOutput Output;
        }

        private sealed class PlayableLinkState
        {
            public GraphContext Context;
            public AnimationClipPlayable Playable;
            public int MixerInput;
            public VaoAnimationLayerConfiguration Configuration;
            public double Length;
            public float SpeedScale;
            public bool Playing;
            public bool Loop;
        }
    }
}
