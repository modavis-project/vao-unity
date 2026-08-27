# Optional integrations

The core package has no dependency on a hardware-MIDI, model-loading, AR, tracking, or XR-interaction SDK. Open **Tools > MODAVIS > Optional Integrations…** to inspect availability. Installation buttons are explicit and are offered only for Unity registry packages: glTFast, AR Foundation, and XR Interaction Toolkit. Vuforia, Minis, and MidiJack remain host-managed.

`VaoMidiDeviceAdapter` discovers Minis or MidiJack through reflection. It polls only note and control numbers declared by the VAO (or the playable sample range when no note binding exists), emits MIDI 1.0 messages through `VaoMidiRouter`, and exposes connect/disconnect/provider events. `ForwardMidi1` is available for another host MIDI source. The adapter does not select a device-specific UI or persistence policy.

`VaoTrackingSdkAdapter` discovers AR Foundation components or Vuforia observer behaviours in the VAO object's parents. It normalizes found/lost state into `VaoTrackedPlacement`, optionally attaches to the tracking transform, and can forward world poses. Limited tracking is opt-in. Components with compatible public tracking properties can be bound as a custom source.

`VaoXrInteractionAdapter` detects supported XR Interaction Toolkit grab-interactable type names. It can explicitly install the component on a chosen root, add a kinematic rigidbody, and expose hover/selection state as Unity events. Interaction layers, input actions, hands/controllers, and locomotion remain host configuration.

`VaoGltfRuntimeLoader` discovers glTFast, supports local/StreamingAssets/runtime-materialized URIs, replaces prior instances safely, ignores stale asynchronous loads, reports failure, and rebuilds linked-animation targets after model instantiation.

Import the **Optional Integrations** sample from Package Manager for a minimal bootstrap that connects MIDI, discovers tracking, and optionally installs XRI grab support. For IL2CPP builds, `Runtime/link.xml` preserves the reflected SDK entry points when their assemblies are present.
