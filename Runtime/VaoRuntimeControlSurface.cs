using System;
using System.Linq;
using UnityEngine;

namespace Modavis.Vao
{
    /// <summary>Dependency-light generated control surface for evaluation, desktop, mobile, and host prototyping.</summary>
    [DisallowMultipleComponent]
    public sealed class VaoRuntimeControlSurface : MonoBehaviour, IVaoPackageConsumer
    {
        [SerializeField] private VaoPackageAsset package;
        [SerializeField] private VaoSamplePlayer samplePlayer;
        [SerializeField] private VaoMediaPlayer mediaPlayer;
        [SerializeField] private VaoPresentationSelector presentationSelector;
        [SerializeField] private VaoRuntimeMaterializer materializer;
        [SerializeField] private VaoAcousticEnvironment acousticEnvironment;
        [SerializeField] private bool visible = true;
        [SerializeField] private bool showControls = true;
        [SerializeField] private bool showMedia = true;
        [SerializeField] private bool showPresentation = true;
        [SerializeField] private bool showKeyboard = true;
        [SerializeField] private bool showAcoustics = true;
        [SerializeField] private bool showMaterialization = true;
        [SerializeField] private int keyboardStartNote = 60;
        [SerializeField, Range(1, 24)] private int visibleKeyCount = 13;
        [SerializeField] private Rect windowRect = new(20f, 20f, 440f, 640f);
        private Vector2 scroll;
        private int heldNote = -1;
        private int remoteOffset;
        private bool restrictedConfirmed;
        private string status;
        private VaoRuntimeMaterializer subscribedMaterializer;

        public VaoPackageAsset Package { get => package; set { package = value; Resolve(); } }
        public bool Visible { get => visible; set => visible = value; }
        public string Status => status;
        public void SetPackage(VaoPackageAsset value) => Package = value;
        public void ToggleVisible() => visible = !visible;

        private void Awake() => Resolve();
        private void OnEnable() { Resolve(); Subscribe(); }
        private void OnDisable() { ReleaseHeldNote(); Unsubscribe(); }

        private void Resolve()
        {
            if (samplePlayer == null) samplePlayer = GetComponent<VaoSamplePlayer>();
            if (mediaPlayer == null) mediaPlayer = GetComponent<VaoMediaPlayer>();
            if (presentationSelector == null) presentationSelector = GetComponent<VaoPresentationSelector>();
            if (materializer == null) materializer = GetComponent<VaoRuntimeMaterializer>();
            if (acousticEnvironment == null) acousticEnvironment = GetComponent<VaoAcousticEnvironment>();
            Subscribe();
            if (package == null) package = GetComponent<VaoRuntimeObject>()?.Package;
            if (package?.SampleBindings.Count > 0)
            {
                var minimum = package.SampleBindings.Min(item => item.MinimumKey);
                var maximum = package.SampleBindings.Max(item => item.MaximumKey);
                keyboardStartNote = Mathf.Clamp(keyboardStartNote, minimum, Mathf.Max(minimum, maximum - visibleKeyCount + 1));
            }
        }

        private void Subscribe()
        {
            if (subscribedMaterializer == materializer) return;
            Unsubscribe();
            subscribedMaterializer = materializer;
            if (subscribedMaterializer != null) subscribedMaterializer.Materialized += OnMaterialized;
        }

        private void Unsubscribe()
        {
            if (subscribedMaterializer != null) subscribedMaterializer.Materialized -= OnMaterialized;
            subscribedMaterializer = null;
        }

        private void OnMaterialized(VaoMaterializationResult result)
        {
            status = result.Succeeded ? $"Verified {Short(result.RealizationIdentifier)} ({(result.FromCache ? "cache" : "download")})." : result.Error ?? result.Status.ToString();
        }

        private void OnGUI()
        {
            if (!visible || package == null) return;
            windowRect = GUI.Window(GetHashCode(), windowRect, DrawWindow, string.IsNullOrWhiteSpace(package.Title) ? "VAO Controls" : package.Title);
        }

        private void DrawWindow(int id)
        {
            if (heldNote >= 0 && Event.current.rawType == EventType.MouseUp) ReleaseHeldNote();
            scroll = GUILayout.BeginScrollView(scroll);
            if (showControls) DrawControls();
            if (showMedia) DrawMedia();
            if (showPresentation) DrawPresentation();
            if (showAcoustics) DrawAcoustics();
            if (showKeyboard) DrawKeyboard();
            if (showMaterialization) DrawMaterialization();
            if (!string.IsNullOrWhiteSpace(status)) GUILayout.Label(status, GUI.skin.box);
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
        }

        private void DrawControls()
        {
            if (package.Controls.Count == 0 || samplePlayer == null) return;
            GUILayout.Label("Declared controls", GUI.skin.label);
            foreach (var control in package.Controls)
            {
                var active = !string.IsNullOrEmpty(control.StateVariableIdentifier) && samplePlayer.GetState(control.StateVariableIdentifier);
                var label = string.IsNullOrWhiteSpace(control.Label) ? Short(control.Identifier) : control.Label;
                if (GUILayout.Button((active ? "● " : "○ ") + label)) samplePlayer.ToggleControl(control.Identifier);
            }
            GUILayout.Space(6f);
        }

        private void DrawMedia()
        {
            if (mediaPlayer == null || mediaPlayer.Entries.Count == 0) return;
            GUILayout.Label("Media and programs", GUI.skin.label);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("◀", GUILayout.Width(44f))) mediaPlayer.SelectPrevious();
            GUILayout.Label(mediaPlayer.SelectedEntry?.Label ?? "—", GUILayout.ExpandWidth(true));
            if (GUILayout.Button("▶", GUILayout.Width(44f))) mediaPlayer.SelectNext();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(mediaPlayer.State == VaoMediaTransportState.Playing ? "Pause" : "Play")) mediaPlayer.TogglePlayPause();
            if (GUILayout.Button("Stop")) mediaPlayer.Stop();
            mediaPlayer.Loop = GUILayout.Toggle(mediaPlayer.Loop, "Loop", GUILayout.Width(64f));
            GUILayout.EndHorizontal();
            var next = GUILayout.HorizontalSlider(mediaPlayer.NormalizedTime, 0f, 1f);
            if (Mathf.Abs(next - mediaPlayer.NormalizedTime) > 0.005f) mediaPlayer.SeekNormalized(next);
            GUILayout.Space(6f);
        }

        private void DrawKeyboard()
        {
            if (samplePlayer == null || package.SampleBindings.Count == 0) return;
            GUILayout.Label("Playable keys", GUI.skin.label);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("−", GUILayout.Width(32f))) keyboardStartNote = Mathf.Max(0, keyboardStartNote - 12);
            GUILayout.Label($"MIDI {keyboardStartNote}–{keyboardStartNote + visibleKeyCount - 1}");
            if (GUILayout.Button("+", GUILayout.Width(32f))) keyboardStartNote = Mathf.Min(127 - visibleKeyCount + 1, keyboardStartNote + 12);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            for (var offset = 0; offset < visibleKeyCount; offset++) DrawKey(keyboardStartNote + offset);
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);
        }

        private void DrawAcoustics()
        {
            if (acousticEnvironment == null || !acousticEnvironment.HasResponse) return;
            GUILayout.Label("Acoustic rendering", GUI.skin.label);
            GUILayout.Label(acousticEnvironment.DescribeCapability(), GUI.skin.box);
            GUILayout.BeginHorizontal();
            if (package.AcousticScenes.Count > 1 && GUILayout.Button("Previous scene")) acousticEnvironment.SelectScene((acousticEnvironment.SceneIndex - 1 + package.AcousticScenes.Count) % package.AcousticScenes.Count);
            if (acousticEnvironment.AvailableRenderers.Count > 1 && GUILayout.Button("Switch renderer")) acousticEnvironment.SelectNextRenderer();
            if (package.AcousticScenes.Count > 1 && GUILayout.Button("Next scene")) acousticEnvironment.SelectScene((acousticEnvironment.SceneIndex + 1) % package.AcousticScenes.Count);
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);
        }

        private void DrawPresentation()
        {
            var bundle = presentationSelector?.Current;
            if (bundle == null || !bundle.Companions.Any()) return;
            GUILayout.Label("Presentation companions", GUI.skin.label);
            for (var index = 0; index < bundle.Items.Count; index++)
            {
                var item = bundle.Items[index];
                if (item.Role == VaoPresentationRole.Primary) continue;
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{item.Role}: {item.Label}", GUILayout.ExpandWidth(true));
                if (!item.IsMaterialized && !string.IsNullOrEmpty(item.RealizationIdentifier) && GUILayout.Button("Acquire", GUILayout.Width(72f)))
                    status = presentationSelector.RequestCompanion(index) ? "Review companion rights and approve acquisition below." : "This companion has no available authorized distribution.";
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(6f);
        }

        private void DrawKey(int note)
        {
            var rect = GUILayoutUtility.GetRect(24f, 52f, GUILayout.ExpandWidth(true));
            GUI.Box(rect, note.ToString());
            var current = Event.current;
            if (!rect.Contains(current.mousePosition)) return;
            if (current.type == EventType.MouseDown && current.button == 0)
            {
                ReleaseHeldNote(); heldNote = note; samplePlayer.NoteOn(note); current.Use();
            }
            else if (current.type == EventType.MouseUp && current.button == 0 && heldNote == note)
            {
                ReleaseHeldNote(); current.Use();
            }
        }

        private void DrawMaterialization()
        {
            if (materializer == null) return;
            var pending = materializer.PendingPlan;
            if (pending != null)
            {
                GUILayout.Label("Acquisition approval", GUI.skin.label);
                GUILayout.Label($"{Short(pending.RealizationIdentifier)}\n{pending.ByteSize:N0} bytes\n{pending.RightsStatement}", GUI.skin.box);
                if (!string.IsNullOrWhiteSpace(pending.Attribution)) GUILayout.Label("Attribution: " + pending.Attribution);
                if (pending.RequiresRestrictedAccessConfirmation) restrictedConfirmed = GUILayout.Toggle(restrictedConfirmed, "I confirm that this application has authorized access");
                GUILayout.BeginHorizontal();
                using (new GuiEnabledScope(!pending.RequiresRestrictedAccessConfirmation || restrictedConfirmed))
                    if (GUILayout.Button("Approve and verify")) { if (restrictedConfirmed) materializer.ApproveRestrictedPending(); else materializer.ApprovePending(); status = "Acquiring and verifying…"; }
                if (GUILayout.Button("Deny")) { materializer.DenyPending(); restrictedConfirmed = false; status = "Acquisition denied."; }
                GUILayout.EndHorizontal();
                return;
            }

            if (!materializer.EnableRemoteAcquisition) { GUILayout.Label("Remote acquisition is disabled by the host.", GUI.skin.box); return; }
            if (materializer.Resolver == null) { GUILayout.Label("Remote acquisition needs a host repository resolver.", GUI.skin.box); return; }

            var available = package.Realizations.Where(item => !item.IsMaterialized && package.FindDistributionsForRealization(item.Identifier).Any(distribution => distribution.Kind == "repository")).ToList();
            if (available.Count == 0) return;
            GUILayout.Label("Available remote realizations", GUI.skin.label);
            const int pageSize = 12;
            remoteOffset = Mathf.Clamp(remoteOffset, 0, Mathf.Max(0, available.Count - 1));
            if (available.Count > pageSize)
            {
                GUILayout.BeginHorizontal();
                using (new GuiEnabledScope(remoteOffset > 0)) if (GUILayout.Button("Previous")) remoteOffset = Mathf.Max(0, remoteOffset - pageSize);
                GUILayout.Label($"{remoteOffset + 1}–{Mathf.Min(remoteOffset + pageSize, available.Count)} of {available.Count}", GUILayout.ExpandWidth(true));
                using (new GuiEnabledScope(remoteOffset + pageSize < available.Count)) if (GUILayout.Button("Next")) remoteOffset += pageSize;
                GUILayout.EndHorizontal();
            }
            foreach (var realization in available.Skip(remoteOffset).Take(pageSize))
            {
                var logical = package.FindLogicalAsset(realization.LogicalAssetIdentifier);
                if (GUILayout.Button($"Acquire {logical?.Label ?? Short(realization.Identifier)} ({realization.ByteSize:N0} bytes)"))
                {
                    var plan = materializer.CreatePlan(realization.Identifier);
                    if (!plan.CanAcquire) status = plan.Error;
                    else { materializer.RequestAcquisition(realization.Identifier); restrictedConfirmed = false; status = null; }
                }
            }
        }

        private void ReleaseHeldNote()
        {
            if (heldNote < 0 || samplePlayer == null) return;
            samplePlayer.NoteOff(heldNote); heldNote = -1;
        }

        private static string Short(string value)
        {
            if (string.IsNullOrEmpty(value)) return "Unnamed";
            var split = Math.Max(value.LastIndexOf(':'), value.LastIndexOf('/'));
            return split >= 0 && split + 1 < value.Length ? value[(split + 1)..] : value;
        }

        private readonly struct GuiEnabledScope : IDisposable
        {
            private readonly bool previous;
            public GuiEnabledScope(bool enabled) { previous = GUI.enabled; GUI.enabled = enabled; }
            public void Dispose() => GUI.enabled = previous;
        }
    }
}
