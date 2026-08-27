# XR host integration

`VaoTrackedPlacement` is the stable boundary between an imported VAO prefab and a tracking SDK. The optional `VaoTrackingSdkAdapter` discovers AR Foundation trackables or Vuforia observer behaviours at runtime and normalizes their tracking state without adding a compile-time dependency.

For an image-target workflow, attach the imported prefab to the target transform and forward tracker state:

```csharp
public void OnTargetFound(Transform target)
{
    placement.AttachToAnchor(target);
    placement.OnTrackingFound();
}

public void OnTargetLost()
{
    placement.OnTrackingLost();
}
```

For a world-tracked workflow, pass the pose supplied by the host:

```csharp
placement.SetTrackedWorldPose(anchorPose.position, anchorPose.rotation);
placement.SetTrackingActive(isTracked);
```

Unity UI sliders can call `SetNormalizedScale`. Reset buttons can call `ResetPlacement`. After dynamically adding renderers, colliders, canvases, or audio sources below the content root, call `RefreshContentState` once so found/lost restoration preserves their intended enabled or mute state.

Instrument keys can call `VaoSamplePlayer.NoteOn` and `NoteOff`; declared stops or controls can call `ToggleControl`. Program browsers can call `VaoMediaPlayer.SelectNext`, `SelectPrevious`, `TogglePlayPause`, and `SeekNormalized`. The same methods can be driven by XR Interaction Toolkit events, Vuforia callbacks, hand tracking, MIDI, or custom input.

For the common path, add `VaoTrackingSdkAdapter` beside `VaoTrackedPlacement`, parent the VAO instance below the tracker component, and enable discovery. Limited AR tracking is rejected by default and can be accepted explicitly. `VaoXrInteractionAdapter` can discover or install an XRI grab interactable after XRI is installed; hover and selection changes are exposed as Unity events. Open **Tools > MODAVIS > Optional Integrations…** for detection and setup.
