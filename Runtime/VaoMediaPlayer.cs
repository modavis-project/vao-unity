using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;

namespace Modavis.Vao
{
    public enum VaoMediaTransportState { Stopped, Playing, Paused }

    [Serializable] public sealed class VaoStringEvent : UnityEvent<string> { }
    [Serializable] public sealed class VaoFloatEvent : UnityEvent<float> { }
    [Serializable] public sealed class VaoIntegerEvent : UnityEvent<int> { }

    [Serializable]
    public sealed class VaoMediaEntry
    {
        public string LogicalAssetIdentifier;
        public string RealizationIdentifier;
        public string Label;
        public string[] Roles = Array.Empty<string>();
        public AudioClip Clip;
        public string RuntimeUri;
    }

    /// <summary>
    /// Selects and transports non-sample audio assets such as performances,
    /// recordings, spoken explanations, or mechanical demonstration programs.
    /// Animation relations driven by the selected logical asset are kept locked
    /// to the audio playhead.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VaoMediaPlayer : MonoBehaviour, IVaoPackageConsumer
    {
        [SerializeField] private VaoPackageAsset package;
        [SerializeField] private AudioSource audioSource;
        [SerializeField] private VaoLinkedAnimationPlayer linkedAnimations;
        [SerializeField] private bool playOnStart;
        [SerializeField] private bool loop;
        [SerializeField] private bool includeSampleAndAcousticAssets;
        [SerializeField, Min(0f)] private float animationResyncToleranceSeconds = 0.05f;
        [SerializeField] private VaoStringEvent selectionChanged = new();
        [SerializeField] private VaoIntegerEvent transportStateChanged = new();
        [SerializeField] private VaoFloatEvent timeChanged = new();

        private readonly List<VaoMediaEntry> entries = new();
        private readonly List<int> activeAnimationLinks = new();
        private int selectedIndex = -1;
        private VaoMediaTransportState state;
        private float lastPublishedTime = -1f;

        public VaoPackageAsset Package { get => package; set { package = value; RebuildCatalog(); } }
        public AudioSource Output { get => audioSource; set => audioSource = value; }
        public VaoLinkedAnimationPlayer LinkedAnimations { get => linkedAnimations; set => linkedAnimations = value; }
        public IReadOnlyList<VaoMediaEntry> Entries => entries;
        public int SelectedIndex => selectedIndex;
        public VaoMediaEntry SelectedEntry => selectedIndex >= 0 && selectedIndex < entries.Count ? entries[selectedIndex] : null;
        public VaoMediaTransportState State => state;
        public bool Loop { get => loop; set { loop = value; if (audioSource != null) audioSource.loop = value; } }
        public float TimeSeconds => audioSource != null ? audioSource.time : 0f;
        public float DurationSeconds => SelectedEntry?.Clip != null ? SelectedEntry.Clip.length : 0f;
        public float NormalizedTime => DurationSeconds > 0f ? Mathf.Clamp01(TimeSeconds / DurationSeconds) : 0f;
        public VaoStringEvent SelectionChanged => selectionChanged;
        public VaoIntegerEvent TransportStateChanged => transportStateChanged;
        public VaoFloatEvent TimeChanged => timeChanged;

        public void SetPackage(VaoPackageAsset value) => Package = value;

        private void Awake()
        {
            EnsureDependencies();
            RebuildCatalog();
        }

        private void Start()
        {
            if (playOnStart && entries.Count > 0) Play();
        }

        private void Update()
        {
            if (state != VaoMediaTransportState.Playing || audioSource == null) return;
            if (!audioSource.isPlaying && !loop)
            {
                SetState(VaoMediaTransportState.Stopped);
                StopLinkedAnimations(false);
                PublishTime(0f);
                return;
            }

            SynchronizeLinkedAnimations();
            PublishTime(audioSource.time);
        }

        public void RebuildCatalog()
        {
            var previous = SelectedEntry?.LogicalAssetIdentifier;
            entries.Clear();
            activeAnimationLinks.Clear();
            selectedIndex = -1;
            if (package == null) return;

            var reserved = new HashSet<string>(package.SampleBindings.Select(item => item.RealizationIdentifier).Where(item => !string.IsNullOrEmpty(item)), StringComparer.Ordinal);
            foreach (var scene in package.AcousticScenes)
                if (!string.IsNullOrEmpty(scene.ResponseRealizationIdentifier)) reserved.Add(scene.ResponseRealizationIdentifier);

            foreach (var realization in package.Realizations)
            {
                if (realization.ImportedObject is not AudioClip clip) continue;
                if (!includeSampleAndAcousticAssets && reserved.Contains(realization.Identifier)) continue;
                var logical = package.FindLogicalAsset(realization.LogicalAssetIdentifier);
                entries.Add(new VaoMediaEntry
                {
                    LogicalAssetIdentifier = realization.LogicalAssetIdentifier,
                    RealizationIdentifier = realization.Identifier,
                    Label = string.IsNullOrWhiteSpace(logical?.Label) ? clip.name : logical.Label,
                    Roles = logical?.Roles ?? realization.Roles ?? Array.Empty<string>(),
                    Clip = clip,
                    RuntimeUri = realization.RuntimeUri
                });
            }

            entries.Sort((left, right) => string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase));
            if (entries.Count == 0) return;
            var restored = string.IsNullOrEmpty(previous) ? -1 : entries.FindIndex(item => item.LogicalAssetIdentifier == previous);
            Select(restored >= 0 ? restored : 0);
        }

        public bool Select(int index)
        {
            if (index < 0 || index >= entries.Count) return false;
            if (state != VaoMediaTransportState.Stopped) Stop();
            selectedIndex = index;
            EnsureDependencies();
            audioSource.clip = entries[index].Clip;
            audioSource.loop = loop;
            RebuildAnimationLinks();
            selectionChanged.Invoke(entries[index].LogicalAssetIdentifier);
            PublishTime(0f, true);
            return true;
        }

        public bool SelectLogicalAsset(string identifier)
        {
            var index = entries.FindIndex(item => string.Equals(item.LogicalAssetIdentifier, identifier, StringComparison.Ordinal));
            return Select(index);
        }

        public bool SelectRealization(string identifier)
        {
            var index = entries.FindIndex(item => string.Equals(item.RealizationIdentifier, identifier, StringComparison.Ordinal));
            return Select(index);
        }

        public void SelectNext()
        {
            if (entries.Count > 0) Select((Mathf.Max(selectedIndex, 0) + 1) % entries.Count);
        }

        public void SelectPrevious()
        {
            if (entries.Count > 0) Select((selectedIndex <= 0 ? entries.Count : selectedIndex) - 1);
        }

        public void Play()
        {
            if (SelectedEntry?.Clip == null) return;
            EnsureDependencies();
            if (state == VaoMediaTransportState.Paused)
            {
                audioSource.UnPause();
                foreach (var index in activeAnimationLinks) linkedAnimations?.ResumeLinkedClip(index);
            }
            else
            {
                audioSource.clip = SelectedEntry.Clip;
                audioSource.loop = loop;
                audioSource.Play();
                foreach (var index in activeAnimationLinks) linkedAnimations?.PlayLinkedClip(index, 0f, loop);
            }
            SetState(VaoMediaTransportState.Playing);
            SynchronizeLinkedAnimations(true);
        }

        public void Pause()
        {
            if (state != VaoMediaTransportState.Playing || audioSource == null) return;
            audioSource.Pause();
            foreach (var index in activeAnimationLinks) linkedAnimations?.PauseLinkedClip(index);
            SetState(VaoMediaTransportState.Paused);
        }

        public void Resume() => Play();

        public void TogglePlayPause()
        {
            if (state == VaoMediaTransportState.Playing) Pause(); else Play();
        }

        public void Stop()
        {
            if (audioSource != null) audioSource.Stop();
            StopLinkedAnimations(true);
            SetState(VaoMediaTransportState.Stopped);
            PublishTime(0f, true);
        }

        public void SeekSeconds(float seconds)
        {
            if (audioSource?.clip == null) return;
            audioSource.time = Mathf.Clamp(seconds, 0f, Mathf.Max(0f, audioSource.clip.length - 0.001f));
            SynchronizeLinkedAnimations(true);
            PublishTime(audioSource.time, true);
        }

        public void SeekNormalized(float normalized) => SeekSeconds(Mathf.Clamp01(normalized) * DurationSeconds);

        private void EnsureDependencies()
        {
            if (audioSource == null)
            {
                audioSource = GetComponent<AudioSource>();
                if (audioSource == null) audioSource = gameObject.AddComponent<AudioSource>();
                audioSource.playOnAwake = false;
            }
            if (linkedAnimations == null) linkedAnimations = GetComponent<VaoLinkedAnimationPlayer>();
        }

        private void RebuildAnimationLinks()
        {
            activeAnimationLinks.Clear();
            if (package == null || SelectedEntry == null) return;
            for (var index = 0; index < package.AnimationLinks.Count; index++)
                if (string.Equals(package.AnimationLinks[index].SourceLogicalAssetIdentifier, SelectedEntry.LogicalAssetIdentifier, StringComparison.Ordinal)) activeAnimationLinks.Add(index);
        }

        private void SynchronizeLinkedAnimations(bool force = false)
        {
            if (linkedAnimations == null || SelectedEntry?.Clip == null) return;
            var normalized = NormalizedTime;
            foreach (var index in activeAnimationLinks)
            {
                var expected = linkedAnimations.GetLinkedClipLength(index) * normalized;
                if (TryGetSynchronizedAnimationTime(index, out var synchronized)) expected = Mathf.Clamp((float)synchronized, 0f, linkedAnimations.GetLinkedClipLength(index));
                if (force || Mathf.Abs(linkedAnimations.GetLinkedClipTime(index) - expected) > animationResyncToleranceSeconds)
                {
                    var length = linkedAnimations.GetLinkedClipLength(index);
                    linkedAnimations.SetLinkedClipNormalizedTime(index, length > 0f ? expected / length : 0f, state == VaoMediaTransportState.Playing);
                }
            }
        }

        private bool TryGetSynchronizedAnimationTime(int linkIndex, out double seconds)
        {
            seconds = 0d;
            if (package == null || SelectedEntry == null || linkIndex < 0 || linkIndex >= package.AnimationLinks.Count || package.SynchronizationMappings.Count == 0) return false;
            var sourceTrack = package.Tracks.FirstOrDefault(item => item.RealizationIdentifier == SelectedEntry.RealizationIdentifier);
            var link = package.AnimationLinks[linkIndex];
            var animationRealizationIds = package.FindRealizationsForLogicalAsset(link.AnimationLogicalAssetIdentifier).Select(item => item.Identifier).ToHashSet(StringComparer.Ordinal);
            var targetTrack = package.Tracks.FirstOrDefault(item => animationRealizationIds.Contains(item.RealizationIdentifier));
            return sourceTrack != null && targetTrack != null && VaoSynchronizationEngine.TryMapSeconds(package, sourceTrack.TimebaseIdentifier, targetTrack.TimebaseIdentifier, TimeSeconds, out seconds);
        }

        private void StopLinkedAnimations(bool rewind)
        {
            foreach (var index in activeAnimationLinks) linkedAnimations?.StopLinkedClip(index, rewind);
        }

        private void SetState(VaoMediaTransportState value)
        {
            if (state == value) return;
            state = value;
            transportStateChanged.Invoke((int)value);
        }

        private void PublishTime(float value, bool force = false)
        {
            if (!force && Mathf.Abs(lastPublishedTime - value) < 0.02f) return;
            lastPublishedTime = value;
            timeChanged.Invoke(value);
        }
    }
}
