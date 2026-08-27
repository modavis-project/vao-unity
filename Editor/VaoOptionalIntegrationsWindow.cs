using System;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace Modavis.Vao.Editor
{
    public sealed class VaoOptionalIntegrationsWindow : EditorWindow
    {
        private static readonly Integration[] Integrations =
        {
            new("glTFast", "GLTFast.GltfImport", "com.unity.cloud.gltfast", "Runtime GLB/glTF scene instantiation and refresh after materialization."),
            new("AR Foundation", "UnityEngine.XR.ARFoundation.ARTrackedImage", "com.unity.xr.arfoundation", "Tracked-image/object/anchor placement through VaoTrackingSdkAdapter."),
            new("XR Interaction Toolkit", "UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable", "com.unity.xr.interaction.toolkit", "Grab/select/hover setup through VaoXrInteractionAdapter."),
            new("Vuforia", "Vuforia.ObserverBehaviour", null, "Observer status and anchor placement through VaoTrackingSdkAdapter."),
            new("Minis", "Minis.MidiDevice", null, "Automatic declared-note and control discovery through VaoMidiDeviceAdapter."),
            new("MidiJack", "MidiJack.MidiMaster", null, "Automatic declared-note and control discovery through VaoMidiDeviceAdapter.")
        };

        private AddRequest addRequest;
        private string operation;
        private Vector2 scroll;

        [MenuItem("Tools/MODAVIS/Optional Integrations…", priority = 110)]
        public static void Open() => GetWindow<VaoOptionalIntegrationsWindow>(true, "VAO Optional Integrations");

        private void OnGUI()
        {
            EditorGUILayout.HelpBox("The VAO core remains dependency-neutral. These adapters discover installed SDKs at runtime; official Unity packages can be added here only when you explicitly request it.", MessageType.Info);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var integration in Integrations) DrawIntegration(integration);
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Selected VAO objects", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Add the dependency-neutral adapters to selected GameObjects. Missing SDKs remain inert and do not cause compilation errors.", MessageType.None);
            using (new EditorGUI.DisabledScope(Selection.gameObjects.Length == 0))
                if (GUILayout.Button("Add VAO optional adapters to selection")) AddAdapters();
            if (!string.IsNullOrEmpty(operation)) EditorGUILayout.HelpBox(operation, addRequest?.Status == StatusCode.Failure ? MessageType.Error : MessageType.Info);
            EditorGUILayout.EndScrollView();
        }

        private void Update()
        {
            if (addRequest == null || !addRequest.IsCompleted) return;
            operation = addRequest.Status == StatusCode.Success ? $"Installed {addRequest.Result.displayName} {addRequest.Result.version}." : addRequest.Error?.message ?? "Package installation failed.";
            addRequest = null;
            Repaint();
        }

        private void DrawIntegration(Integration integration)
        {
            var detected = FindType(integration.TypeName) != null || integration.Name == "XR Interaction Toolkit" && FindType("UnityEngine.XR.Interaction.Toolkit.XRGrabInteractable") != null;
            var package = string.IsNullOrEmpty(integration.PackageName) ? null : UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().FirstOrDefault(item => item.name == integration.PackageName);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(integration.Name, EditorStyles.boldLabel);
            GUILayout.Label(detected ? "Ready" : package != null ? $"Installed {package.version}; scripts not loaded" : "Not detected", detected ? EditorStyles.miniBoldLabel : EditorStyles.miniLabel, GUILayout.Width(170f));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(integration.Description, EditorStyles.wordWrappedMiniLabel);
            if (!detected && package == null && !string.IsNullOrEmpty(integration.PackageName))
                using (new EditorGUI.DisabledScope(addRequest != null))
                    if (GUILayout.Button("Install " + integration.PackageName))
                    {
                        operation = "Requesting " + integration.PackageName + "…";
                        addRequest = Client.Add(integration.PackageName);
                    }
            EditorGUILayout.EndVertical();
        }

        private static void AddAdapters()
        {
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            foreach (var selected in Selection.gameObjects)
            {
                if (selected.GetComponent<VaoRuntimeObject>() == null && selected.GetComponent<VaoTrackedPlacement>() == null) continue;
                if (selected.GetComponent<VaoMidiDeviceAdapter>() == null) Undo.AddComponent<VaoMidiDeviceAdapter>(selected);
                if (selected.GetComponent<VaoTrackingSdkAdapter>() == null) Undo.AddComponent<VaoTrackingSdkAdapter>(selected);
                if (selected.GetComponent<VaoXrInteractionAdapter>() == null) Undo.AddComponent<VaoXrInteractionAdapter>(selected);
            }
            Undo.CollapseUndoOperations(group);
        }

        private static Type FindType(string fullName) => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(fullName, false)).FirstOrDefault(item => item != null);
        private sealed class Integration
        {
            public readonly string Name;
            public readonly string TypeName;
            public readonly string PackageName;
            public readonly string Description;
            public Integration(string name, string typeName, string packageName, string description)
            {
                Name = name; TypeName = typeName; PackageName = packageName; Description = description;
            }
        }
    }
}
