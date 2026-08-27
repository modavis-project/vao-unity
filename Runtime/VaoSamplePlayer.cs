using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Modavis.Vao
{
    [DisallowMultipleComponent]
    public sealed class VaoSamplePlayer : MonoBehaviour, IVaoPackageConsumer
    {
        [SerializeField] private VaoPackageAsset package;
        [SerializeField, Min(1)] private int maximumVoices = 64;
        [SerializeField, Min(0f)] private float releaseSeconds = 0.3f;
        [SerializeField, Range(0f, 1f)] private float spatialBlend = 1f;
        [SerializeField] private Transform voiceRoot;

        private readonly Dictionary<string, VaoPrimitiveValue> states = new(StringComparer.Ordinal);
        private readonly Dictionary<int, List<AudioSource>> voicesByKey = new();
        private readonly Dictionary<string, AudioClip> runtimeClips = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> selectionCounters = new(StringComparer.Ordinal);
        private readonly Dictionary<AudioSource, VaoSampleBinding> bindingsByVoice = new();
        private readonly HashSet<int> heldNotes = new();
        private readonly List<AudioSource> voices = new();
        private int voiceAllocationCounter;

        public VaoPackageAsset Package { get => package; set { package = value; ResetState(); } }
        public event Action<int, int> NoteStarted;
        public event Action<int> NoteReleased;
        public event Action<string, VaoPrimitiveValue> StateChanged;
        public event Action<VaoDeclarativeActionRecord> ActionRequested;
        public int ActiveVoiceCount => voices.Count;
        public Transform VoiceRoot { get => voiceRoot != null ? voiceRoot : transform; set => voiceRoot = value; }

        public void SetPackage(VaoPackageAsset value) => Package = value;

        private void Awake() => ResetState();

        public void ResetState()
        {
            states.Clear();
            voiceAllocationCounter = 0;
            if (package == null) return;
            foreach (var state in package.StateVariables) states[state.Identifier] = state.DefaultValue;
            foreach (var control in package.Controls)
                if (!string.IsNullOrEmpty(control.StateVariableIdentifier) && !states.ContainsKey(control.StateVariableIdentifier)) states[control.StateVariableIdentifier] = VaoPrimitiveValue.FromBoolean(control.DefaultBoolean);
        }

        public bool GetState(string stateVariableIdentifier) => states.TryGetValue(stateVariableIdentifier, out var value) && value.Boolean;
        public VaoPrimitiveValue GetStateValue(string stateVariableIdentifier) => states.TryGetValue(stateVariableIdentifier, out var value) ? value : default;

        public void SetState(string stateVariableIdentifier, bool value)
        {
            SetStateValue(stateVariableIdentifier, VaoPrimitiveValue.FromBoolean(value));
        }

        public void SetStateValue(string stateVariableIdentifier, VaoPrimitiveValue value)
        {
            if (string.IsNullOrEmpty(stateVariableIdentifier)) return;
            states[stateVariableIdentifier] = value;
            StateChanged?.Invoke(stateVariableIdentifier, value);
        }

        public bool ToggleControl(string controlIdentifier)
        {
            var control = package != null ? package.FindControl(controlIdentifier) : null;
            if (control == null || string.IsNullOrEmpty(control.StateVariableIdentifier)) return false;
            var transition = package.Transitions.Find(item => item.ControlIdentifier == controlIdentifier);
            if (transition != null)
            {
                ActivateControl(controlIdentifier, transition.EventTypeIdentifier, default);
                return GetState(control.StateVariableIdentifier);
            }
            var next = !GetState(control.StateVariableIdentifier);
            SetState(control.StateVariableIdentifier, next);
            return next;
        }

        public bool ActivateControl(string controlIdentifier, string eventTypeIdentifier, VaoPrimitiveValue inputValue)
        {
            var executor = GetComponent<VaoDeterministicExecutor>();
            if (executor != null && executor.isActiveAndEnabled && executor.Package == package && !executor.IsDispatching)
                return executor.ExecuteControlNow(controlIdentifier, eventTypeIdentifier, inputValue);
            return ExecuteControlDirect(controlIdentifier, eventTypeIdentifier, inputValue);
        }

        internal bool ExecuteControlDirect(string controlIdentifier, string eventTypeIdentifier, VaoPrimitiveValue inputValue)
        {
            if (package == null) return false;
            var eligible = package.Transitions
                .Where(transition => string.IsNullOrEmpty(transition.ControlIdentifier) || transition.ControlIdentifier == controlIdentifier)
                .Where(transition => string.IsNullOrEmpty(eventTypeIdentifier) || transition.EventTypeIdentifier == eventTypeIdentifier)
                .Where(transition => ConditionsMatch(transition.Conditions))
                .OrderByDescending(transition => transition.Priority)
                .ThenBy(transition => transition.Identifier, StringComparer.Ordinal)
                .ToList();
            foreach (var transition in eligible)
                foreach (var action in transition.Actions) Execute(action, inputValue);
            return eligible.Count > 0;
        }

        internal bool ConditionsMatch(IEnumerable<VaoStateConditionRecord> conditions)
        {
            foreach (var condition in conditions)
            {
                var actual = GetStateValue(condition.StateVariableIdentifier);
                var comparison = Compare(actual, condition.Value);
                var matches = condition.Operator switch
                {
                    "not-equals" => comparison != 0,
                    "greater-than" => comparison > 0,
                    "greater-than-or-equal" => comparison >= 0,
                    "less-than" => comparison < 0,
                    "less-than-or-equal" => comparison <= 0,
                    _ => comparison == 0
                };
                if (!matches) return false;
            }
            return true;
        }

        private void Execute(VaoDeclarativeActionRecord action, VaoPrimitiveValue inputValue)
        {
            if (!ExecuteStateAction(action, inputValue)) ActionRequested?.Invoke(action);
        }

        internal bool ExecuteStateAction(VaoDeclarativeActionRecord action, VaoPrimitiveValue inputValue)
        {
            switch (action?.Operation)
            {
                case "set-state":
                    SetStateValue(action.TargetIdentifier, action.HasValue ? action.Value : inputValue);
                    return true;
                case "toggle-state":
                    SetState(action.TargetIdentifier, !GetState(action.TargetIdentifier));
                    return true;
                case "increment-state":
                    var current = GetStateValue(action.TargetIdentifier);
                    var increment = action.HasValue ? action.Value.Number : 1d;
                    var next = current.Number + increment;
                    var declaration = package.StateVariables.Find(item => item.Identifier == action.TargetIdentifier);
                    if (declaration != null)
                    {
                        if (declaration.HasMinimum) next = Math.Max(declaration.MinimumValue, next);
                        if (declaration.HasMaximum) next = Math.Min(declaration.MaximumValue, next);
                    }
                    SetStateValue(action.TargetIdentifier, VaoPrimitiveValue.FromNumber(next));
                    return true;
                default:
                    return false;
            }
        }

        internal void RequestHostAction(VaoDeclarativeActionRecord action) => ActionRequested?.Invoke(action);

        private static int Compare(VaoPrimitiveValue left, VaoPrimitiveValue right)
        {
            if (left.Type is "number" or "integer" || right.Type is "number" or "integer") return left.Number.CompareTo(right.Number);
            if (left.Type == "boolean" || right.Type == "boolean") return left.Boolean.CompareTo(right.Boolean);
            return string.CompareOrdinal(left.Text, right.Text);
        }

        public void NoteOn(int midiNote, int velocity = 127)
        {
            if (package == null || velocity <= 0) { NoteOff(midiNote); return; }
            heldNotes.Add(midiNote);
            if (DispatchDeclaredNoteOn(midiNote, velocity)) return;
            PlayNoteDirect(midiNote, velocity);
        }

        private bool DispatchDeclaredNoteOn(int midiNote, int velocity)
        {
            var executor = GetComponent<VaoDeterministicExecutor>();
            if (executor == null || !executor.isActiveAndEnabled || executor.Package != package || executor.IsDispatching) return false;
            var binding = package.ProtocolBindings.FirstOrDefault(item => item.Direction == "input" && item.MessageType == "note" && (item.Number < 0 || item.Number == midiNote)
                && package.EventTypes.Any(eventType => eventType.Identifier == item.EventTypeIdentifier && eventType.EventKind == "note-on"));
            var eventType = binding != null ? package.EventTypes.First(item => item.Identifier == binding.EventTypeIdentifier)
                : package.EventTypes.FirstOrDefault(item => item.EventKind == "note-on");
            if (eventType == null) return false;
            var controlIdentifier = binding?.ControlIdentifier ?? package.RoutingRules.FirstOrDefault(item => midiNote >= item.MinimumKey && midiNote <= item.MaximumKey)?.SourceControlIdentifier;
            if (string.IsNullOrEmpty(controlIdentifier)) return false;
            executor.ExecuteControlNow(controlIdentifier, eventType.Identifier, VaoPrimitiveValue.FromNumber(midiNote), velocity);
            return package.RenderBindings.Any(item => item.EventTypeIdentifier == eventType.Identifier);
        }

        private void PlayNoteDirect(int midiNote, int velocity)
        {
            var started = new List<AudioSource>();
            var candidates = package.SampleBindings.Where(binding =>
            {
                if (binding.Trigger == "note-off" || binding.Trigger == "control" || binding.Trigger == "continuous") return false;
                if (midiNote < binding.MinimumKey || midiNote > binding.MaximumKey || velocity < binding.MinimumVelocity || velocity > binding.MaximumVelocity) return false;
                return string.IsNullOrEmpty(binding.StateVariableIdentifier) || GetState(binding.StateVariableIdentifier);
            });
            foreach (var group in candidates.GroupBy(binding => binding.MappingIdentifier, StringComparer.Ordinal))
            {
                foreach (var binding in SelectVariants(group.ToList()))
                {
                    var clip = binding.Clip;
                    if (clip == null && !string.IsNullOrEmpty(binding.RuntimeUri)) StartCoroutine(LoadAndPlay(binding, midiNote, velocity, false));
                    else if (clip != null) started.Add(Play(binding, clip, midiNote, velocity));
                }
            }
            if (started.Count > 0) voicesByKey[midiNote] = started;
            NoteStarted?.Invoke(midiNote, velocity);
        }

        public void NoteOff(int midiNote)
        {
            heldNotes.Remove(midiNote);
            if (voicesByKey.TryGetValue(midiNote, out var noteVoices))
            {
                voicesByKey.Remove(midiNote);
                foreach (var source in noteVoices)
                {
                    if (source == null) continue;
                    bindingsByVoice.TryGetValue(source, out var binding);
                    if (binding?.NoteOffPolicy == "finish-cycle") StartCoroutine(WaitForCompletion(source));
                    else if (binding?.NoteOffPolicy == "stop" || releaseSeconds <= 0f) StopVoice(source);
                    else StartCoroutine(FadeAndStop(source, releaseSeconds));
                }
            }
            var releases = package?.SampleBindings.Where(binding => binding.Trigger == "note-off" && midiNote >= binding.MinimumKey && midiNote <= binding.MaximumKey && (string.IsNullOrEmpty(binding.StateVariableIdentifier) || GetState(binding.StateVariableIdentifier))) ?? Enumerable.Empty<VaoSampleBinding>();
            foreach (var group in releases.GroupBy(binding => binding.MappingIdentifier, StringComparer.Ordinal))
                foreach (var binding in SelectVariants(group.ToList()))
                    if (binding.Clip != null) StartCoroutine(WaitForCompletion(Play(binding, binding.Clip, midiNote, 127)));
                    else if (!string.IsNullOrEmpty(binding.RuntimeUri)) StartCoroutine(LoadAndPlay(binding, midiNote, 127, true));
            NoteReleased?.Invoke(midiNote);
        }

        public bool RenderBinding(string renderBindingIdentifier, int key, int velocity = 127)
        {
            if (package == null) return false;
            var render = package.FindRenderBinding(renderBindingIdentifier);
            if (render == null || !ConditionsMatch(render.Conditions)) return false;
            if (render.SelectionPolicy == "host-defined") return false;
            var mappingIds = new HashSet<string>(render.SampleMappingIdentifiers ?? Array.Empty<string>(), StringComparer.Ordinal);
            var variantIds = new HashSet<string>(render.SampleVariantIdentifiers ?? Array.Empty<string>(), StringComparer.Ordinal);
            var candidates = package.SampleBindings.Where(binding => (mappingIds.Contains(binding.MappingIdentifier) || variantIds.Contains(binding.VariantIdentifier))
                && key >= binding.MinimumKey && key <= binding.MaximumKey && velocity >= binding.MinimumVelocity && velocity <= binding.MaximumVelocity).ToList();
            var selected = candidates.GroupBy(binding => binding.MappingIdentifier, StringComparer.Ordinal).SelectMany(group => SelectVariants(group.ToList())).ToList();
            if (render.SelectionPolicy != "simultaneous" && selected.Count > 1)
            {
                var counterKey = "render:" + render.Identifier;
                selectionCounters.TryGetValue(counterKey, out var counter);
                selectionCounters[counterKey] = counter + 1;
                selected = new List<VaoSampleBinding> { render.SelectionPolicy == "ordered" ? selected[counter % selected.Count] : selected[0] };
            }
            var started = new List<AudioSource>();
            foreach (var binding in selected)
                if (binding.Clip != null) started.Add(Play(binding, binding.Clip, key, velocity));
                else if (!string.IsNullOrEmpty(binding.RuntimeUri)) StartCoroutine(LoadAndPlay(binding, key, velocity, false));
            if (started.Count > 0)
            {
                if (!voicesByKey.TryGetValue(key, out var noteVoices)) voicesByKey[key] = noteVoices = new List<AudioSource>();
                noteVoices.AddRange(started);
            }
            if (selected.Count > 0) NoteStarted?.Invoke(key, velocity);
            return selected.Count > 0;
        }

        public void AllNotesOff()
        {
            foreach (var source in voices.ToArray()) if (source != null) StopVoice(source);
            voicesByKey.Clear();
            heldNotes.Clear();
        }

        private AudioSource Play(VaoSampleBinding binding, AudioClip clip, int midiNote, int velocity)
        {
            var declaredMaximum = package?.ExecutionSemantics.MaximumVoices ?? 0L;
            var limit = Mathf.Max(1, declaredMaximum > 0 ? (int)Math.Min(int.MaxValue, declaredMaximum) : maximumVoices);
            var policy = package?.ExecutionSemantics.VoiceAllocation;
            if (policy == "monophonic-priority")
                foreach (var voice in voices.ToArray()) StopVoice(voice);
            while (voices.Count >= limit)
            {
                var index = policy == "round-robin" ? voiceAllocationCounter++ % voices.Count : 0;
                StopVoice(voices[index]);
            }
            var child = new GameObject($"VAO Voice {midiNote}");
            child.transform.SetParent(VoiceRoot, false);
            var source = child.AddComponent<AudioSource>();
            source.clip = clip;
            source.playOnAwake = false;
            source.spatialBlend = spatialBlend;
            source.volume = Mathf.Pow(10f, binding.GainDecibels / 20f) * Mathf.Clamp01(velocity / 127f);
            source.pitch = Mathf.Pow(2f, (midiNote - binding.SampleRootKey) / 12f + binding.PitchTuningCents / 1200f);
            GetComponent<VaoAcousticEnvironment>()?.AttachVoice(child, source);
            source.Play();
            voices.Add(source);
            bindingsByVoice[source] = binding;
            return source;
        }

        private IEnumerator LoadAndPlay(VaoSampleBinding binding, int midiNote, int velocity, bool release)
        {
            if (runtimeClips.TryGetValue(binding.RuntimeUri, out var cached))
            {
                if (release || heldNotes.Contains(midiNote)) TrackLoadedVoice(midiNote, Play(binding, cached, midiNote, velocity), release);
                yield break;
            }
            using var request = UnityWebRequestMultimedia.GetAudioClip(binding.RuntimeUri, AudioType.WAV);
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"VAO audio load failed: {request.error} ({binding.RuntimeUri})", this);
                yield break;
            }
            var clip = DownloadHandlerAudioClip.GetContent(request);
            runtimeClips[binding.RuntimeUri] = clip;
            if (release || heldNotes.Contains(midiNote)) TrackLoadedVoice(midiNote, Play(binding, clip, midiNote, velocity), release);
        }

        private void TrackLoadedVoice(int midiNote, AudioSource source, bool release)
        {
            if (release) { StartCoroutine(WaitForCompletion(source)); return; }
            if (!voicesByKey.TryGetValue(midiNote, out var noteVoices)) voicesByKey[midiNote] = noteVoices = new List<AudioSource>();
            noteVoices.Add(source);
        }

        private List<VaoSampleBinding> SelectVariants(List<VaoSampleBinding> candidates)
        {
            if (candidates.Count <= 1 || candidates[0].SelectionPolicy == "simultaneous") return candidates;
            var key = candidates[0].MappingIdentifier ?? string.Empty;
            selectionCounters.TryGetValue(key, out var counter);
            selectionCounters[key] = counter + 1;
            if (candidates[0].SelectionPolicy == "round-robin") return new List<VaoSampleBinding> { candidates.OrderBy(item => item.RoundRobinIndex).ElementAt(counter % candidates.Count) };
            if (candidates[0].SelectionPolicy == "random-weighted")
            {
                var total = candidates.Sum(item => Math.Max(0.0001f, item.SelectionWeight));
                var point = (((uint)counter * 2654435761u) & 0x00ffffffu) / 16777216f * total;
                foreach (var candidate in candidates)
                {
                    point -= Math.Max(0.0001f, candidate.SelectionWeight);
                    if (point <= 0f) return new List<VaoSampleBinding> { candidate };
                }
            }
            return new List<VaoSampleBinding> { candidates.FirstOrDefault(item => item.Clip != null || !string.IsNullOrEmpty(item.RuntimeUri)) ?? candidates[0] };
        }

        private IEnumerator FadeAndStop(AudioSource source, float duration)
        {
            var start = source.volume;
            for (var elapsed = 0f; elapsed < duration && source != null; elapsed += Time.unscaledDeltaTime)
            {
                source.volume = Mathf.Lerp(start, 0f, elapsed / duration);
                yield return null;
            }
            if (source != null) StopVoice(source);
        }

        private IEnumerator WaitForCompletion(AudioSource source)
        {
            while (source != null && source.isPlaying) yield return null;
            if (source != null) StopVoice(source);
        }

        private void StopVoice(AudioSource source)
        {
            voices.Remove(source);
            if (source == null) return;
            bindingsByVoice.Remove(source);
            source.Stop();
            if (Application.isPlaying) Destroy(source.gameObject); else DestroyImmediate(source.gameObject);
        }

        private void OnDisable() => AllNotesOff();
    }
}
