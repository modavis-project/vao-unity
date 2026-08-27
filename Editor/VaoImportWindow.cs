using System;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Modavis.Vao.Editor
{
    public sealed class VaoImportWindow : EditorWindow
    {
        [SerializeField] private string archivePath;
        [SerializeField] private VaoImportOptions options = new();
        private VaoArchiveInspection inspection;
        private Vector2 scroll;

        [MenuItem("Tools/MODAVIS/Import VAO 0.4.0…")]
        public static void Open() => GetWindow<VaoImportWindow>(true, "Import VAO 0.4.0").minSize = new Vector2(620, 420);

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Virtual Acoustic Object 0.4.0", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("The archive is parsed as inert data. Nothing package-supplied is executed or fetched, and payload bytes are imported only after carrier and fixity validation.", MessageType.Info);
            using (new EditorGUILayout.HorizontalScope())
            {
                archivePath = EditorGUILayout.TextField("VAO archive", archivePath);
                if (GUILayout.Button("Browse…", GUILayout.Width(90)))
                {
                    var selected = EditorUtility.OpenFilePanel("Select VAO 0.4.0", string.Empty, "vao");
                    if (!string.IsNullOrEmpty(selected)) { archivePath = selected; inspection = null; }
                }
            }
            options.DestinationAssetPath = EditorGUILayout.TextField("Destination", options.DestinationAssetPath);
            options.VerifyPayloadDigests = EditorGUILayout.Toggle("Verify every payload", options.VerifyPayloadDigests);
            options.CreatePrefab = EditorGUILayout.Toggle("Create runtime prefab", options.CreatePrefab);
            using (new EditorGUI.DisabledScope(!options.CreatePrefab)) options.CreateRuntimeControlSurface = EditorGUILayout.Toggle("Add generated controls", options.CreateRuntimeControlSurface);
            options.GenerateMidiAnimationClips = EditorGUILayout.Toggle("Convert linked MIDI", options.GenerateMidiAnimationClips);
            options.CopyGlbToStreamingAssets = EditorGUILayout.Toggle("Stage GLB for runtime", options.CopyGlbToStreamingAssets);
            options.MaterializationMode = (VaoMaterializationMode)EditorGUILayout.EnumPopup("Materialization", options.MaterializationMode);
            options.MaximumMaterializedBytes = EditorGUILayout.LongField("Maximum bytes", options.MaximumMaterializedBytes);
            if (inspection?.IsValid == true && options.MaterializationMode == VaoMaterializationMode.SelectedAssetGroups)
            {
                options.SelectedAssetGroupIdentifiers ??= new List<string>();
                EditorGUILayout.LabelField("Asset groups", EditorStyles.boldLabel);
                foreach (var group in inspection.Manifest["assetGroups"]?.OfType<Newtonsoft.Json.Linq.JObject>() ?? Enumerable.Empty<Newtonsoft.Json.Linq.JObject>())
                {
                    var id = group.Value<string>("id");
                    var label = VaoJson.Localized(group["labels"]);
                    var bytes = group.Value<long?>("totalByteSize") ?? 0;
                    var selected = options.SelectedAssetGroupIdentifiers.Contains(id);
                    var next = EditorGUILayout.ToggleLeft($"{label} — {EditorUtility.FormatBytes(bytes)}", selected);
                    if (next && !selected) options.SelectedAssetGroupIdentifiers.Add(id);
                    else if (!next && selected) options.SelectedAssetGroupIdentifiers.Remove(id);
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(archivePath)))
                    if (GUILayout.Button("Validate")) inspection = VaoArchiveReader.Inspect(archivePath, new VaoValidationPolicy { VerifyPayloadDigests = options.VerifyPayloadDigests });
                using (new EditorGUI.DisabledScope(inspection?.IsValid != true))
                    if (GUILayout.Button("Import")) Import();
            }

            if (inspection == null) return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(inspection.IsValid ? "VALID" : "INVALID", inspection.IsValid ? EditorStyles.boldLabel : EditorStyles.whiteLargeLabel);
            EditorGUILayout.LabelField("Title", inspection.Title);
            EditorGUILayout.LabelField("Identifier", inspection.Identifier);
            EditorGUILayout.LabelField("Verified payload", EditorUtility.FormatBytes(inspection.VerifiedPayloadBytes));
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var error in inspection.Errors) EditorGUILayout.HelpBox(error, MessageType.Error);
            foreach (var warning in inspection.Warnings.Distinct()) EditorGUILayout.HelpBox(warning, MessageType.Warning);
            EditorGUILayout.EndScrollView();
        }

        private void Import()
        {
            try
            {
                var result = VaoImporter.Import(archivePath, options);
                EditorGUIUtility.PingObject(result.Package);
                EditorUtility.DisplayDialog("VAO import complete", $"Imported {result.Package.Title}\n\n{result.Package.Realizations.Count} described realizations\n{result.Package.Realizations.Count(item => item.IsMaterialized)} materialized ({EditorUtility.FormatBytes(result.MaterializedBytes)})\n{result.SkippedRealizationCount} metadata-only\n{result.Package.SampleBindings.Count} sample mappings\n{result.Package.AnimationLinks.Count} animation links\n{result.Package.AcousticScenes.Count} acoustic scenes", "OK");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("VAO import failed", exception.Message, "OK");
            }
        }
    }
}
