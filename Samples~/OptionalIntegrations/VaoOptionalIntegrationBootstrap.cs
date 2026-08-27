using Modavis.Vao;
using UnityEngine;

/// <summary>
/// Drop this on an imported VAO root to enable dependency-neutral discovery of
/// installed MIDI, tracking, and XR Interaction Toolkit integrations.
/// </summary>
[DisallowMultipleComponent]
public sealed class VaoOptionalIntegrationBootstrap : MonoBehaviour
{
    [SerializeField] private bool connectMidi = true;
    [SerializeField] private bool discoverTracking = true;
    [SerializeField] private bool installXrGrabInteractable;

    private void Awake()
    {
        if (connectMidi)
        {
            var midi = GetComponent<VaoMidiDeviceAdapter>() ?? gameObject.AddComponent<VaoMidiDeviceAdapter>();
            midi.Connect();
        }
        if (discoverTracking)
        {
            var tracking = GetComponent<VaoTrackingSdkAdapter>() ?? gameObject.AddComponent<VaoTrackingSdkAdapter>();
            tracking.Discover();
        }
        if (installXrGrabInteractable)
        {
            var interaction = GetComponent<VaoXrInteractionAdapter>() ?? gameObject.AddComponent<VaoXrInteractionAdapter>();
            interaction.InstallGrabInteractable();
        }
    }
}
