using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Modavis.Vao
{
    public enum VaoMidiDeviceProvider { Auto, MidiJack, Minis }
    public enum VaoTrackingProvider { Auto, ArFoundation, Vuforia, Custom }

    /// <summary>
    /// Optional hardware bridge for MidiJack or Minis. The adapter discovers an
    /// installed provider at runtime and forwards only declared VAO notes and CCs
    /// to VaoMidiRouter; the core package has no compile-time MIDI dependency.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed partial class VaoMidiDeviceAdapter : MonoBehaviour
    {
        [SerializeField] private VaoMidiRouter router;
        [SerializeField] private VaoMidiDeviceProvider provider = VaoMidiDeviceProvider.Auto;
        [SerializeField, Range(0, 15)] private int midiChannel;
        [SerializeField, Range(0f, 1f)] private float noteThreshold = 0.001f;
        [SerializeField] private bool autoConnect = true;
        [SerializeField] private VaoStringEvent providerChanged = new();
        private readonly Dictionary<int, float> noteValues = new();
        private readonly Dictionary<int, int> controlValues = new();
        private ProviderBridge bridge;
        private float nextDiscovery;

        public VaoMidiDeviceProvider Provider { get => provider; set { provider = value; Disconnect(); } }
        public string ActiveProvider => bridge?.Name;
        public bool IsConnected => bridge != null;
        public VaoStringEvent ProviderChanged => providerChanged;

        private void Awake() { if (router == null) router = GetComponent<VaoMidiRouter>(); }
        private void OnEnable() { if (autoConnect) Connect(); }
        private void OnDisable() => Disconnect();

        private void Update()
        {
            if (!autoConnect) return;
            if (bridge == null)
            {
                if (Time.unscaledTime >= nextDiscovery) { Connect(); nextDiscovery = Time.unscaledTime + 1f; }
                return;
            }
            if (!bridge.Available) { Disconnect(); return; }
            PollNotes();
            PollControls();
        }

        public bool Connect()
        {
            if (router == null) router = GetComponent<VaoMidiRouter>();
            if (router == null) return false;
            Disconnect(false);
            if (provider is VaoMidiDeviceProvider.Auto or VaoMidiDeviceProvider.Minis) bridge = MinisBridge.TryCreate();
            if (bridge == null && provider is VaoMidiDeviceProvider.Auto or VaoMidiDeviceProvider.MidiJack) bridge = MidiJackBridge.TryCreate();
            if (bridge == null) return false;
            noteValues.Clear();
            controlValues.Clear();
            providerChanged.Invoke(bridge.Name);
            return true;
        }

        public void Disconnect() => Disconnect(true);

        public void ForwardMidi1(byte status, byte data1, byte data2 = 0) => router?.ProcessMidi1(status, data1, data2);

        private void Disconnect(bool notify)
        {
            if (bridge == null) return;
            bridge.Dispose();
            bridge = null;
            noteValues.Clear();
            controlValues.Clear();
            if (notify) providerChanged.Invoke(string.Empty);
        }

        private void PollNotes()
        {
            foreach (var note in DeclaredNotes())
            {
                var value = Mathf.Clamp01(bridge.ReadNote(note));
                noteValues.TryGetValue(note, out var previous);
                if (previous <= noteThreshold && value > noteThreshold)
                    router.ProcessMidi1((byte)(0x90 | midiChannel), (byte)note, (byte)Mathf.Clamp(Mathf.RoundToInt(value * 127f), 1, 127));
                else if (previous > noteThreshold && value <= noteThreshold)
                    router.ProcessMidi1((byte)(0x80 | midiChannel), (byte)note, 0);
                noteValues[note] = value;
            }
        }

        private void PollControls()
        {
            foreach (var number in DeclaredControls())
            {
                var value = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(bridge.ReadControl(number)) * 127f), 0, 127);
                if (!controlValues.TryGetValue(number, out var previous)) { controlValues[number] = value; continue; }
                if (value == previous) continue;
                controlValues[number] = value;
                router.ProcessMidi1((byte)(0xb0 | midiChannel), (byte)number, (byte)value);
            }
        }

        private IEnumerable<int> DeclaredNotes()
        {
            var package = router?.Package;
            if (package == null) return Enumerable.Range(0, 128);
            var explicitNotes = package.ProtocolBindings.Where(item => item.Direction == "input" && item.Protocol == "MIDI-1.0" && item.MessageType == "note" && item.Number >= 0).Select(item => item.Number).Distinct().ToArray();
            if (explicitNotes.Length > 0) return explicitNotes;
            if (package.SampleBindings.Count == 0) return Enumerable.Range(0, 128);
            var minimum = Mathf.Clamp(package.SampleBindings.Min(item => item.MinimumKey), 0, 127);
            var maximum = Mathf.Clamp(package.SampleBindings.Max(item => item.MaximumKey), minimum, 127);
            return Enumerable.Range(minimum, maximum - minimum + 1);
        }

        private IEnumerable<int> DeclaredControls()
            => router?.Package?.ProtocolBindings.Where(item => item.Direction == "input" && item.Protocol == "MIDI-1.0" && item.MessageType == "control-change" && item.Number >= 0).Select(item => item.Number).Distinct() ?? Enumerable.Empty<int>();

        private abstract class ProviderBridge : IDisposable
        {
            public abstract string Name { get; }
            public abstract bool Available { get; }
            public abstract float ReadNote(int note);
            public abstract float ReadControl(int number);
            public virtual void Dispose() { }
        }

        private sealed class MidiJackBridge : ProviderBridge
        {
            private readonly Type type;
            private readonly MethodInfo getKey;
            private readonly MethodInfo getKnob;
            private MidiJackBridge(Type value, MethodInfo key, MethodInfo knob) { type = value; getKey = key; getKnob = knob; }
            public override string Name => "MidiJack";
            public override bool Available => type != null && FindType("MidiJack.MidiMaster") != null;
            public override float ReadNote(int note) => InvokeFloat(getKey, null, note);
            public override float ReadControl(int number) => getKnob == null ? 0f : InvokeFloat(getKnob, null, number, 0f);

            public static MidiJackBridge TryCreate()
            {
                var type = FindType("MidiJack.MidiMaster");
                if (type == null) return null;
                var key = type.GetMethod("GetKey", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null);
                var knob = type.GetMethod("GetKnob", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int), typeof(float) }, null);
                return key == null ? null : new MidiJackBridge(type, key, knob);
            }
        }

        private sealed class MinisBridge : ProviderBridge
        {
            private readonly Type type;
            private readonly PropertyInfo current;
            private readonly MethodInfo getNote;
            private readonly MethodInfo getControl;
            private MinisBridge(Type value, PropertyInfo currentProperty, MethodInfo note, MethodInfo control) { type = value; current = currentProperty; getNote = note; getControl = control; }
            public override string Name => "Minis";
            public override bool Available => type != null && current?.GetValue(null) != null;
            public override float ReadNote(int note) => ReadValue(getNote?.Invoke(current.GetValue(null), new object[] { note }));
            public override float ReadControl(int number) => ReadValue(getControl?.Invoke(current.GetValue(null), new object[] { number }));

            public static MinisBridge TryCreate()
            {
                var type = FindType("Minis.MidiDevice");
                if (type == null) return null;
                var current = type.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                var note = type.GetMethod("GetNote", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
                var control = type.GetMethod("GetControl", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
                return current == null || note == null || current.GetValue(null) == null ? null : new MinisBridge(type, current, note, control);
            }

            private static float ReadValue(object control)
            {
                if (control == null) return 0f;
                var method = control.GetType().GetMethod("ReadValue", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                return method == null ? 0f : Convert.ToSingle(method.Invoke(control, null));
            }
        }

        private static float InvokeFloat(MethodInfo method, object target, params object[] arguments)
        {
            try { return method == null ? 0f : Convert.ToSingle(method.Invoke(target, arguments)); }
            catch (TargetInvocationException) { return 0f; }
        }

        internal static Type FindType(string fullName) => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(fullName, false)).FirstOrDefault(item => item != null);
    }

    /// <summary>Polling adapter for AR Foundation trackables and Vuforia ObserverBehaviour.</summary>
    [DisallowMultipleComponent]
    public sealed partial class VaoTrackingSdkAdapter : MonoBehaviour
    {
        [SerializeField] private VaoTrackedPlacement placement;
        [SerializeField] private Component trackingSource;
        [SerializeField] private VaoTrackingProvider provider = VaoTrackingProvider.Auto;
        [SerializeField] private bool discoverInParents = true;
        [SerializeField] private bool attachToTrackingTransform = true;
        [SerializeField] private bool acceptLimitedTracking;
        [SerializeField] private VaoBooleanEvent trackingChanged = new();
        private bool? lastState;
        private Transform attachedTo;

        public Component TrackingSource => trackingSource;
        public string ActiveProvider => ProviderFor(trackingSource);
        public VaoBooleanEvent TrackingChanged => trackingChanged;

        private void Awake() { if (placement == null) placement = GetComponent<VaoTrackedPlacement>(); }
        private void OnEnable() => Discover();
        private void Update()
        {
            if (trackingSource == null && discoverInParents) Discover();
            if (trackingSource == null || placement == null) return;
            if (attachToTrackingTransform && placement.PlacementRoot != trackingSource.transform && attachedTo != trackingSource.transform)
            {
                placement.AttachToAnchor(trackingSource.transform);
                attachedTo = trackingSource.transform;
            }
            else if (!attachToTrackingTransform)
                placement.SetTrackedWorldPose(trackingSource.transform.position, trackingSource.transform.rotation);
            var active = EvaluateTrackingState(trackingSource, acceptLimitedTracking);
            if (lastState == active) return;
            lastState = active;
            placement.SetTrackingActive(active);
            trackingChanged.Invoke(active);
        }

        public void Bind(Component source)
        {
            trackingSource = source;
            attachedTo = null;
            lastState = null;
        }

        public bool Discover()
        {
            var components = discoverInParents ? GetComponentsInParent<Component>(true) : GetComponents<Component>();
            trackingSource = components.FirstOrDefault(IsAcceptedProvider);
            attachedTo = null;
            lastState = null;
            return trackingSource != null;
        }

        private bool IsAcceptedProvider(Component component)
        {
            if (component == null || component == this || component is VaoTrackedPlacement) return false;
            var detected = ProviderFor(component);
            return provider switch
            {
                VaoTrackingProvider.ArFoundation => detected == "AR Foundation",
                VaoTrackingProvider.Vuforia => detected == "Vuforia",
                VaoTrackingProvider.Custom => detected == "Custom",
                _ => detected is "AR Foundation" or "Vuforia"
            };
        }

        internal static bool EvaluateTrackingState(object source, bool acceptLimited)
        {
            if (source == null) return false;
            var type = source.GetType();
            var direct = Property(type, source, "isTracked") ?? Property(type, source, "isTracking");
            if (direct is bool boolean) return boolean;
            var trackingState = Property(type, source, "trackingState")?.ToString();
            if (!string.IsNullOrEmpty(trackingState)) return string.Equals(trackingState, "Tracking", StringComparison.OrdinalIgnoreCase) || acceptLimited && string.Equals(trackingState, "Limited", StringComparison.OrdinalIgnoreCase);
            var targetStatus = Property(type, source, "TargetStatus");
            var status = targetStatus == null ? null : Property(targetStatus.GetType(), targetStatus, "Status")?.ToString();
            if (!string.IsNullOrEmpty(status)) return status.Contains("TRACKED", StringComparison.OrdinalIgnoreCase) || acceptLimited && status.Contains("LIMITED", StringComparison.OrdinalIgnoreCase);
            return source is Behaviour behaviour && behaviour.isActiveAndEnabled;
        }

        private static object Property(Type type, object target, string name)
        {
            try { return type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target); }
            catch (TargetInvocationException) { return null; }
        }

        private static string ProviderFor(Component component)
        {
            var name = component?.GetType().FullName ?? string.Empty;
            if (name.StartsWith("UnityEngine.XR.ARFoundation.", StringComparison.Ordinal)) return "AR Foundation";
            if (name.StartsWith("Vuforia.", StringComparison.Ordinal)) return "Vuforia";
            if (component != null)
            {
                var type = component.GetType();
                if (type.GetProperty("trackingState") != null || type.GetProperty("isTracked") != null || type.GetProperty("isTracking") != null || type.GetProperty("TargetStatus") != null) return "Custom";
            }
            return string.Empty;
        }
    }

    /// <summary>Optional XR Interaction Toolkit installer/state bridge, implemented without a hard XRI reference.</summary>
    [DisallowMultipleComponent]
    public sealed partial class VaoXrInteractionAdapter : MonoBehaviour
    {
        private static readonly string[] InteractableTypes =
        {
            "UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable",
            "UnityEngine.XR.Interaction.Toolkit.XRGrabInteractable"
        };

        [SerializeField] private Transform interactionRoot;
        [SerializeField] private Component interactable;
        [SerializeField] private bool installGrabInteractableOnStart;
        [SerializeField] private bool addKinematicRigidbody = true;
        [SerializeField] private VaoBooleanEvent selectionChanged = new();
        [SerializeField] private VaoBooleanEvent hoverChanged = new();
        private bool? selected;
        private bool? hovered;

        public bool IsAvailable => FindInteractableType() != null;
        public Component Interactable => interactable;
        public VaoBooleanEvent SelectionChanged => selectionChanged;
        public VaoBooleanEvent HoverChanged => hoverChanged;

        private void Awake()
        {
            if (interactionRoot == null) interactionRoot = transform;
            if (interactable == null) interactable = FindExisting();
            if (interactable == null && installGrabInteractableOnStart) InstallGrabInteractable();
        }

        private void Update()
        {
            if (interactable == null) return;
            var nextSelected = ReadBool(interactable, "isSelected");
            var nextHovered = ReadBool(interactable, "isHovered");
            if (nextSelected.HasValue && selected != nextSelected) { selected = nextSelected; selectionChanged.Invoke(nextSelected.Value); }
            if (nextHovered.HasValue && hovered != nextHovered) { hovered = nextHovered; hoverChanged.Invoke(nextHovered.Value); }
        }

        public bool InstallGrabInteractable()
        {
            if (interactionRoot == null) interactionRoot = transform;
            var type = FindInteractableType();
            if (type == null) return false;
            interactable = interactionRoot.GetComponent(type) ?? interactionRoot.gameObject.AddComponent(type);
            if (addKinematicRigidbody)
            {
                var body = interactionRoot.GetComponent<Rigidbody>() ?? interactionRoot.gameObject.AddComponent<Rigidbody>();
                body.isKinematic = true;
                body.useGravity = false;
            }
            return true;
        }

        private Component FindExisting()
        {
            var type = FindInteractableType();
            return type == null ? null : interactionRoot.GetComponent(type);
        }

        private static Type FindInteractableType() => InteractableTypes.Select(VaoMidiDeviceAdapter.FindType).FirstOrDefault(item => item != null);
        private static bool? ReadBool(object target, string name)
        {
            try { return target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target) is bool value ? value : null; }
            catch (TargetInvocationException) { return null; }
        }
    }
}
