using Modavis.Vao;
using UnityEngine;
using UnityEngine.Events;

public sealed class VaoDeterministicAcousticsDemo : MonoBehaviour
{
    [SerializeField] private VaoDeterministicExecutor executor;
    [SerializeField] private VaoAcousticEnvironment acoustics;
    [SerializeField] private UnityEvent<string> statusChanged;

    private void Awake()
    {
        executor ??= GetComponent<VaoDeterministicExecutor>();
        acoustics ??= GetComponent<VaoAcousticEnvironment>();
    }

    private void OnEnable()
    {
        if (executor != null)
        {
            executor.ProcessStarted += OnProcessStarted;
            executor.ProcessCompleted += OnProcessCompleted;
            executor.EventRouted += OnEventRouted;
        }
        if (acoustics != null)
        {
            acoustics.RendererChanged += OnRendererChanged;
            acoustics.SceneChanged += OnSceneChanged;
        }
    }

    private void OnDisable()
    {
        if (executor != null)
        {
            executor.ProcessStarted -= OnProcessStarted;
            executor.ProcessCompleted -= OnProcessCompleted;
            executor.EventRouted -= OnEventRouted;
        }
        if (acoustics != null)
        {
            acoustics.RendererChanged -= OnRendererChanged;
            acoustics.SceneChanged -= OnSceneChanged;
        }
    }

    public bool Trigger(string controlIdentifier, string eventTypeIdentifier, float value = 0f)
        => executor != null && executor.ExecuteControlNow(controlIdentifier, eventTypeIdentifier, VaoPrimitiveValue.FromNumber(value));

    public bool StartProcess(string processIdentifier) => executor != null && executor.StartProcess(processIdentifier);
    public bool StopProcess(string processIdentifier) => executor != null && executor.StopProcess(processIdentifier);
    public bool NextRenderer() => acoustics != null && acoustics.SelectNextRenderer();

    public bool NextAcousticScene()
    {
        if (acoustics?.Package == null || acoustics.Package.AcousticScenes.Count == 0) return false;
        return acoustics.SelectScene((acoustics.SceneIndex + 1) % acoustics.Package.AcousticScenes.Count);
    }

    private void OnProcessStarted(string identifier) => statusChanged?.Invoke("Started process: " + identifier);
    private void OnProcessCompleted(string identifier) => statusChanged?.Invoke("Completed process: " + identifier);
    private void OnEventRouted(VaoExecutionEvent value) => statusChanged?.Invoke($"Routed {value.Value.Number} to {value.TargetIdentifier}");
    private void OnRendererChanged(IVaoAcousticRenderer value) => statusChanged?.Invoke("Renderer: " + value.RendererName);
    private void OnSceneChanged(VaoAcousticSceneRecord value) => statusChanged?.Invoke("Acoustic scene: " + value.Identifier);
}
