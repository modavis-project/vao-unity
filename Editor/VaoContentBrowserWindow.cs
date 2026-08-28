using System;
using System.IO;
using System.Linq;
using Modavis.Vao;
using UnityEditor;
using UnityEngine;

namespace Modavis.Vao.Editor
{
    public enum VaoSourceState { UpToDate, Changed, Missing, External }

    public static class VaoSourceDependency
    {
        public static VaoSourceState State(VaoPackageAsset package, out string source)
        {
            source = VaoReimport.ResolveSourcePath(package);
            if (string.IsNullOrWhiteSpace(source)) return VaoSourceState.Missing;
            if (!source.StartsWith("Assets", StringComparison.Ordinal)) return File.Exists(source) ? VaoSourceState.External : VaoSourceState.Missing;
            var archive = AssetDatabase.LoadAssetAtPath<VaoArchiveAsset>(source);
            if (archive == null) return VaoSourceState.Missing;
            return archive.ArchiveSha256 == package.SourceArchiveSha256 ? VaoSourceState.UpToDate : VaoSourceState.Changed;
        }
    }

    public sealed class VaoContentBrowserWindow : EditorWindow
    {
        private static readonly string[] Tabs = { "Summary", "Assets", "Presentations", "Entities", "Controls", "Relations", "Execution", "Acoustics", "Science", "Signals", "Rights", "Capabilities" };
        [SerializeField] private VaoPackageAsset package;
        [SerializeField] private int tab;
        [SerializeField] private string search;
        private Vector2 scroll;

        [MenuItem("Tools/MODAVIS/VAO Content Browser")]
        public static void Open() => GetWindow<VaoContentBrowserWindow>("VAO Content Browser");

        public static void Open(VaoPackageAsset value)
        {
            var window = GetWindow<VaoContentBrowserWindow>("VAO Content Browser");
            window.package = value; window.Show(); window.Focus();
        }

        private void OnSelectionChange()
        {
            if (Selection.activeObject is VaoPackageAsset selected) { package = selected; Repaint(); }
        }

        private void OnGUI()
        {
            package = (VaoPackageAsset)EditorGUILayout.ObjectField("VAO package", package, typeof(VaoPackageAsset), false);
            if (package == null) { EditorGUILayout.HelpBox("Select an imported VaoPackageAsset.", MessageType.Info); return; }
            tab = GUILayout.Toolbar(tab, Tabs);
            search = EditorGUILayout.TextField("Filter", search);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            switch (tab)
            {
                case 0: DrawSummary(); break;
                case 1: DrawAssets(); break;
                case 2: DrawPresentations(); break;
                case 3: DrawEntities(); break;
                case 4: DrawControls(); break;
                case 5: DrawRelations(); break;
                case 6: DrawExecution(); break;
                case 7: DrawAcoustics(); break;
                case 8: DrawScience(); break;
                case 9: DrawSignals(); break;
                case 10: DrawRights(); break;
                case 11: DrawCapabilities(); break;
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawSummary()
        {
            EditorGUILayout.LabelField(package.Title, EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Identifier", package.Identifier);
            EditorGUILayout.LabelField("Release", package.ReleaseIdentifier);
            EditorGUILayout.LabelField("Format", package.FormatVersion);
            EditorGUILayout.LabelField("Imported", package.ImportedAtUtc);
            var state = VaoSourceDependency.State(package, out var source);
            EditorGUILayout.LabelField("Source", source ?? "Missing");
            EditorGUILayout.HelpBox(state switch { VaoSourceState.UpToDate => "The tracked source archive matches this import.", VaoSourceState.Changed => "The tracked source archive changed. Preview the reimport before applying it.", VaoSourceState.External => "The source is external to Assets; it can be reimported but Unity cannot automatically track moves.", _ => "The tracked source archive is missing." }, state == VaoSourceState.Changed ? MessageType.Warning : state == VaoSourceState.Missing ? MessageType.Error : MessageType.Info);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Entities", package.Entities.Count.ToString());
            EditorGUILayout.LabelField("Logical assets", package.LogicalAssets.Count.ToString());
            EditorGUILayout.LabelField("Realizations", $"{package.Realizations.Count} ({package.Realizations.Count(item => item.IsMaterialized)} materialized)");
            EditorGUILayout.LabelField("Controls", package.Controls.Count.ToString());
            EditorGUILayout.LabelField("Relations", package.Relations.Count.ToString());
            EditorGUILayout.LabelField("Acoustic scenes", package.AcousticScenes.Count.ToString());
            EditorGUILayout.LabelField("Process models", package.ProcessModels.Count.ToString());
            EditorGUILayout.LabelField("Synchronization mappings", package.SynchronizationMappings.Count.ToString());
            EditorGUILayout.LabelField("Scientific observations", package.ScientificObservations.Count.ToString());
            EditorGUILayout.LabelField("Protocol bindings", package.ProtocolBindings.Count.ToString());
            EditorGUILayout.LabelField("Transfer functions", package.TransferFunctions.Count.ToString());
            EditorGUILayout.LabelField("Physical components", package.PhysicalComponents.Count.ToString());
            EditorGUILayout.LabelField("Required capabilities", package.Capabilities.Count.ToString());
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(state == VaoSourceState.Missing)) if (GUILayout.Button("Preview source update…")) VaoReimportWindow.Open(package);
            using (new EditorGUI.DisabledScope(package.Prefab == null)) if (GUILayout.Button("Add or enable generated runtime controls")) AddControlSurface(package);
            if (package.Prefab != null && GUILayout.Button("Ping runtime prefab")) EditorGUIUtility.PingObject(package.Prefab);
        }

        private void DrawAssets()
        {
            foreach (var logical in package.LogicalAssets.Where(item => Matches(item.Identifier, item.Label, string.Join(" ", item.Roles))))
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(string.IsNullOrWhiteSpace(logical.Label) ? Short(logical.Identifier) : logical.Label, EditorStyles.boldLabel);
                EditorGUILayout.SelectableLabel(logical.Identifier, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                EditorGUILayout.LabelField("Roles", string.Join(", ", logical.Roles));
                foreach (var realization in package.FindRealizationsForLogicalAsset(logical.Identifier))
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label(realization.IsMaterialized ? "●" : "○", GUILayout.Width(18f));
                    GUILayout.Label($"{realization.MediaType} · {EditorUtility.FormatBytes(realization.ByteSize)} · {realization.QualityTier}");
                    if (realization.ImportedObject != null && GUILayout.Button("Ping", GUILayout.Width(48f))) EditorGUIUtility.PingObject(realization.ImportedObject);
                    EditorGUILayout.EndHorizontal();
                    if (!realization.IsMaterialized)
                    {
                        var distributions = package.FindDistributionsForRealization(realization.Identifier);
                        EditorGUILayout.LabelField("Remote", distributions.Count == 0 ? "not declared" : string.Join(", ", distributions.Select(item => item.Access + ":" + Short(item.Identifier))));
                    }
                }
                EditorGUILayout.EndVertical();
            }
        }

        private void DrawEntities()
        {
            foreach (var entity in package.Entities.Where(item => Matches(item.Identifier, item.Label, item.Kind, string.Join(" ", item.Types))))
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(string.IsNullOrWhiteSpace(entity.Label) ? Short(entity.Identifier) : entity.Label, EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Kind", entity.Kind);
                EditorGUILayout.SelectableLabel(entity.Identifier, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                EditorGUILayout.EndVertical();
            }
        }

        private void DrawPresentations()
        {
            var found = false;
            foreach (var logical in package.LogicalAssets.Where(item => Matches(item.Identifier, item.Label, string.Join(" ", item.Roles))))
            {
                var bundle = package.ResolvePresentation(logical.Identifier);
                if (bundle == null || !bundle.Companions.Any()) continue;
                found = true;
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(bundle.Label, EditorStyles.boldLabel);
                EditorGUILayout.SelectableLabel(bundle.PrimaryLogicalAssetIdentifier, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                foreach (var item in bundle.Companions)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label(item.IsMaterialized ? "●" : "○", GUILayout.Width(18f));
                    GUILayout.Label($"{item.Role} · {item.Label}", GUILayout.ExpandWidth(true));
                    if (item.ImportedObject != null && GUILayout.Button("Ping", GUILayout.Width(48f))) EditorGUIUtility.PingObject(item.ImportedObject);
                    EditorGUILayout.EndHorizontal();
                    if (!string.IsNullOrWhiteSpace(item.Attribution)) EditorGUILayout.LabelField("Attribution", item.Attribution);
                }
                EditorGUILayout.EndVertical();
            }
            if (!found) EditorGUILayout.HelpBox("No presentation companion bundles are declared for the current filter.", MessageType.Info);
        }

        private void DrawControls()
        {
            foreach (var control in package.Controls.Where(item => Matches(item.Identifier, item.Label, item.Behavior)))
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(string.IsNullOrWhiteSpace(control.Label) ? Short(control.Identifier) : control.Label, EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Behavior", control.Behavior);
                EditorGUILayout.LabelField("State", control.StateVariableIdentifier);
                EditorGUILayout.LabelField("MIDI", control.MidiNumber >= 0 ? $"{control.MidiMessageType} channel {control.MidiChannel}, number {control.MidiNumber}" : "not bound");
                EditorGUILayout.EndVertical();
            }
        }

        private void DrawRelations()
        {
            foreach (var relation in package.Relations.Where(item => Matches(item.Identifier, item.SubjectIdentifier, item.Predicate, item.ObjectIdentifier)))
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(Short(relation.Predicate), EditorStyles.boldLabel);
                EditorGUILayout.SelectableLabel(relation.SubjectIdentifier + "\n→ " + relation.ObjectIdentifier, GUILayout.Height(EditorGUIUtility.singleLineHeight * 2f));
                EditorGUILayout.EndVertical();
            }
        }

        private void DrawAcoustics()
        {
            foreach (var scene in package.AcousticScenes.Where(item => Matches(item.Identifier, item.SceneEntityIdentifier, item.RepresentationType, item.RenderStrategy)))
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(Short(scene.Identifier), EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Representation", scene.RepresentationType);
                EditorGUILayout.LabelField("Strategy", scene.RenderStrategy);
                EditorGUILayout.LabelField("Response", scene.ResponseSetIdentifier);
                EditorGUILayout.LabelField("Kind / encoding", $"{scene.ResponseKind ?? "—"} / {scene.ResponseEncoding ?? "—"}");
                EditorGUILayout.LabelField("Interpolation", $"{scene.InterpolationMethod ?? "nearest"} · outside: {scene.OutsideDomainPolicy ?? "—"}");
                EditorGUILayout.LabelField("Listener", $"{scene.ListenerMode ?? "—"} · {Short(scene.ReceiverIdentifier)}");
                if (scene.RuntimeFeatures.Count > 0) EditorGUILayout.LabelField("Runtime features", string.Join(", ", scene.RuntimeFeatures.Select(item => $"{item.Feature}:{item.Mode}")));
                EditorGUILayout.LabelField("Response points", scene.ResponsePoints.Count.ToString());
                EditorGUILayout.LabelField("Materialized", (scene.ImpulseResponse != null || scene.Sofa != null || scene.ResponsePoints.Any(point => point.ImpulseResponse != null || point.Sofa != null)).ToString());
                if (scene.Sofa != null)
                {
                    EditorGUILayout.LabelField("SOFA", $"{scene.Sofa.Convention} · {scene.Sofa.MeasurementCount} measurements · {scene.Sofa.ReceiverCount} receivers · {scene.Sofa.SampleRate} Hz");
                    if (GUILayout.Button("Ping decoded SOFA", GUILayout.Width(140f))) EditorGUIUtility.PingObject(scene.Sofa);
                }
                EditorGUILayout.EndVertical();
            }
        }

        private void DrawExecution()
        {
            EditorGUILayout.LabelField("Deterministic semantics", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Event order", package.ExecutionSemantics.SimultaneousEventOrder);
            EditorGUILayout.LabelField("Actions", package.ExecutionSemantics.ActionExecution);
            EditorGUILayout.LabelField("Late / reentrant", $"{package.ExecutionSemantics.LateEventPolicy} / {package.ExecutionSemantics.ReentrancyPolicy}");
            EditorGUILayout.LabelField("Maximum microsteps", package.ExecutionSemantics.MaximumMicrosteps.ToString());
            EditorGUILayout.LabelField("Voice allocation", $"{package.ExecutionSemantics.VoiceAllocation ?? "host default"} / {(package.ExecutionSemantics.MaximumVoices > 0 ? package.ExecutionSemantics.MaximumVoices.ToString() : "host maximum")}");
            EditorGUILayout.Space();
            foreach (var process in package.ProcessModels.Where(item => Matches(item.Identifier, item.ProcessKind, item.Ordering, item.TerminationPolicy)))
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(Short(process.Identifier), EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Kind / ordering", $"{process.ProcessKind} / {process.Ordering}");
                EditorGUILayout.LabelField("Termination", process.TerminationPolicy);
                EditorGUILayout.LabelField("Actions / children", $"{process.Actions.Count} / {process.ChildProcessIdentifiers.Length}");
                EditorGUILayout.EndVertical();
            }
            foreach (var rule in package.RoutingRules.Where(item => Matches(item.Identifier, item.SourceEntityIdentifier, item.TargetEntityIdentifier, item.RoutingBehavior, item.KeyTransform)))
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(Short(rule.Identifier), EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Route", $"{Short(rule.SourceEntityIdentifier)} → {Short(rule.TargetEntityIdentifier)}");
                EditorGUILayout.LabelField("Behavior / keys", $"{rule.RoutingBehavior} / {rule.KeyTransform}");
                EditorGUILayout.EndVertical();
            }
            foreach (var binding in package.RenderBindings.Where(item => Matches(item.Identifier, item.EventTypeIdentifier, item.ProcessModelIdentifier, item.SelectionPolicy)))
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(Short(binding.Identifier), EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Event / policy", $"{Short(binding.EventTypeIdentifier)} / {binding.SelectionPolicy}");
                EditorGUILayout.LabelField("Mappings / variants", $"{binding.SampleMappingIdentifiers.Length} / {binding.SampleVariantIdentifiers.Length}");
                EditorGUILayout.EndVertical();
            }
            foreach (var mapping in package.SynchronizationMappings.Where(item => Matches(item.Identifier, item.SourceTimebaseIdentifier, item.TargetTimebaseIdentifier, item.Method)))
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(Short(mapping.Identifier), EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Mapping", $"{Short(mapping.SourceTimebaseIdentifier)} → {Short(mapping.TargetTimebaseIdentifier)}");
                EditorGUILayout.LabelField("Method / segments", $"{mapping.Method} / {mapping.Segments.Count}");
                EditorGUILayout.EndVertical();
            }
        }

        private void DrawRights()
        {
            foreach (var rights in package.Rights.Where(item => Matches(item.Identifier, item.Statement, item.Attribution, item.License, item.Access)))
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(Short(rights.Identifier), EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Access", rights.Access);
                EditorGUILayout.LabelField("License", rights.License);
                EditorGUILayout.LabelField(rights.Statement, EditorStyles.wordWrappedLabel);
                if (!string.IsNullOrWhiteSpace(rights.Attribution)) EditorGUILayout.LabelField("Attribution: " + rights.Attribution, EditorStyles.wordWrappedLabel);
                EditorGUILayout.EndVertical();
            }
        }

        private void DrawScience()
        {
            foreach (var observation in package.ScientificObservations.Where(item => Matches(item.Identifier, item.ObservedProperty, item.FeatureOfInterestIdentifier, item.ProtocolIdentifier, item.Status, string.Join(" ", item.QualityFlags))))
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(Short(observation.ObservedProperty), EditorStyles.boldLabel);
                EditorGUILayout.SelectableLabel(observation.Identifier, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                EditorGUILayout.LabelField("Feature", Short(observation.FeatureOfInterestIdentifier));
                EditorGUILayout.LabelField("Result", observation.HasNumericValue ? $"{observation.NumericValue} {Short(observation.Unit)}" : observation.ResultJson ?? "—");
                EditorGUILayout.LabelField("Status / time", $"{observation.Status} / {observation.ResultTime}");
                if (observation.QualityFlags.Length > 0) EditorGUILayout.LabelField("Quality", string.Join(", ", observation.QualityFlags));
                EditorGUILayout.EndVertical();
            }
        }

        private void DrawSignals()
        {
            EditorGUILayout.LabelField("Physical components", EditorStyles.boldLabel);
            foreach (var component in package.PhysicalComponents.Where(item => Matches(item.Identifier, item.EntityIdentifier, item.ComponentKind, item.ParentComponentIdentifier)))
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(Short(component.Identifier), EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Kind", Short(component.ComponentKind));
                EditorGUILayout.LabelField("Entity", Short(component.EntityIdentifier));
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.LabelField("Signal transfer functions", EditorStyles.boldLabel);
            foreach (var transfer in package.TransferFunctions.Where(item => Matches(item.Identifier, item.InputUnit, item.OutputUnit, item.SourceLocator, item.Notes)))
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(Short(transfer.Identifier), EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Transform", $"{transfer.InputUnit} → {transfer.OutputUnit}");
                EditorGUILayout.LabelField("Model", $"{transfer.DynamicModel} / {transfer.Interpolation}");
                EditorGUILayout.LabelField("Evidence", $"{transfer.Status} · {transfer.Source} · {transfer.SourceLocator}");
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.LabelField("Protocol bindings", EditorStyles.boldLabel);
            foreach (var binding in package.ProtocolBindings.Where(item => Matches(item.Identifier, item.Protocol, item.Address, item.SourceLocator, item.Status)))
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(Short(binding.Identifier), EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Protocol / direction", $"{binding.Protocol} / {binding.Direction}");
                EditorGUILayout.LabelField("Message", string.IsNullOrWhiteSpace(binding.Address) ? $"{binding.MessageType} · channel {binding.Channel} · number {binding.Number}" : $"{binding.MessageType} · {binding.Address}");
                EditorGUILayout.LabelField("Evidence", $"{binding.Status} · {binding.Source} · {binding.SourceLocator}");
                EditorGUILayout.EndVertical();
            }
        }

        private void DrawCapabilities()
        {
            foreach (var capability in package.Capabilities.Where(item => Matches(item)))
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(Short(capability), EditorStyles.boldLabel);
                EditorGUILayout.SelectableLabel(capability, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                EditorGUILayout.EndVertical();
            }
        }

        private bool Matches(params string[] values) => string.IsNullOrWhiteSpace(search) || values.Any(value => value?.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);

        private static string Short(string value) { if (string.IsNullOrEmpty(value)) return "Unnamed"; var split = Math.Max(value.LastIndexOf(':'), value.LastIndexOf('/')); return split >= 0 && split + 1 < value.Length ? value[(split + 1)..] : value; }

        internal static void AddControlSurface(VaoPackageAsset package)
        {
            var path = AssetDatabase.GetAssetPath(package.Prefab);
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var controls = root.GetComponent<VaoRuntimeControlSurface>();
                if (controls == null) controls = root.AddComponent<VaoRuntimeControlSurface>();
                controls.Package = package; controls.enabled = true;
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            package.ImportSettings.CreateRuntimeControlSurface = true;
            EditorUtility.SetDirty(package);
            AssetDatabase.SaveAssets(); EditorGUIUtility.PingObject(package.Prefab);
        }
    }

    [CustomEditor(typeof(VaoPackageAsset))]
    public sealed class VaoPackageAssetInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var package = (VaoPackageAsset)target;
            EditorGUILayout.LabelField(package.Title, EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Identifier", package.Identifier);
            EditorGUILayout.LabelField("Release", package.ReleaseIdentifier);
            EditorGUILayout.LabelField("Realizations", $"{package.Realizations.Count} ({package.Realizations.Count(item => item.IsMaterialized)} materialized)");
            var state = VaoSourceDependency.State(package, out var source);
            EditorGUILayout.LabelField("Source", source ?? "Missing");
            if (state == VaoSourceState.Changed) EditorGUILayout.HelpBox("The tracked source VAO has changed.", MessageType.Warning);
            if (GUILayout.Button("Open VAO Content Browser")) VaoContentBrowserWindow.Open(package);
            using (new EditorGUI.DisabledScope(state == VaoSourceState.Missing)) if (GUILayout.Button("Preview and reimport…")) VaoReimportWindow.Open(package);
            using (new EditorGUI.DisabledScope(package.Prefab == null)) if (GUILayout.Button("Add generated runtime controls")) VaoContentBrowserWindow.AddControlSurface(package);
        }
    }

    public sealed class VaoReimportWindow : EditorWindow
    {
        [SerializeField] private VaoPackageAsset package;
        private VaoImportOptions options;
        private VaoImportPreview preview;
        private Vector2 scroll;

        public static void Open(VaoPackageAsset value)
        {
            var window = GetWindow<VaoReimportWindow>(true, "VAO Reimport Preview"); window.package = value; window.options = VaoReimport.OptionsFrom(value); window.preview = null; window.minSize = new Vector2(640f, 500f); window.Show();
        }

        private void OnGUI()
        {
            package = (VaoPackageAsset)EditorGUILayout.ObjectField("Existing package", package, typeof(VaoPackageAsset), false);
            if (package == null) return;
            options ??= VaoReimport.OptionsFrom(package);
            EditorGUILayout.LabelField("Source", VaoReimport.ResolveSourcePath(package));
            options.MaterializationMode = (VaoMaterializationMode)EditorGUILayout.EnumPopup("Materialization", options.MaterializationMode);
            options.MaximumMaterializedBytes = EditorGUILayout.LongField("Maximum bytes", options.MaximumMaterializedBytes);
            options.CreatePrefab = EditorGUILayout.Toggle("Create prefab", options.CreatePrefab);
            using (new EditorGUI.DisabledScope(!options.CreatePrefab)) options.CreateRuntimeControlSurface = EditorGUILayout.Toggle("Generated controls", options.CreateRuntimeControlSurface);
            options.GenerateMidiAnimationClips = EditorGUILayout.Toggle("Convert linked MIDI", options.GenerateMidiAnimationClips);
            options.CopyGlbToStreamingAssets = EditorGUILayout.Toggle("Stage GLB", options.CopyGlbToStreamingAssets);
            if (GUILayout.Button("Build verified change preview")) preview = VaoReimport.Preview(VaoReimport.ResolveSourcePath(package), package, options);
            if (preview == null) return;
            if (!preview.IsCompatible) { EditorGUILayout.HelpBox(preview.Error, MessageType.Error); return; }
            EditorGUILayout.HelpBox($"{preview.AddedCount} added, {preview.ChangedCount} changed, {preview.RemovedCount} removed; {preview.MaterializedCount} realizations / {EditorUtility.FormatBytes(preview.MaterializedBytes)} selected. Rights changed: {preview.RightsChanged}. Relations changed: {preview.RelationsChanged}.", preview.RightsChanged ? MessageType.Warning : MessageType.Info);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var change in preview.Changes.Where(item => item.Kind != VaoImportChangeKind.Unchanged)) EditorGUILayout.LabelField(change.Kind.ToString(), $"{change.RealizationIdentifier} · {change.MediaType} · {EditorUtility.FormatBytes(change.ByteSize)}");
            EditorGUILayout.EndScrollView();
            if (GUILayout.Button("Apply transactional reimport…") && EditorUtility.DisplayDialog("Apply VAO update?", "The new import was validated and previewed. The managed import folder will be backed up, updated while preserving stable asset/prefab GUIDs, verified, and restored automatically if any step fails.", "Apply", "Cancel"))
            {
                try { var result = VaoReimport.Apply(preview); EditorGUIUtility.PingObject(result.Package); Close(); }
                catch (Exception exception) { Debug.LogException(exception); EditorUtility.DisplayDialog("VAO reimport failed", exception.Message, "OK"); }
            }
        }
    }

    public sealed class VaoSourceDependencyTracker : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            foreach (var path in imported.Concat(moved).Where(path => path.EndsWith(".vao", StringComparison.OrdinalIgnoreCase)))
            {
                var guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid)) continue;
                foreach (var packageGuid in AssetDatabase.FindAssets("t:VaoPackageAsset"))
                {
                    var package = AssetDatabase.LoadAssetAtPath<VaoPackageAsset>(AssetDatabase.GUIDToAssetPath(packageGuid));
                    if (package != null && package.SourceArchiveGuid == guid && package.SourceArchiveSha256 != AssetDatabase.LoadAssetAtPath<VaoArchiveAsset>(path)?.ArchiveSha256)
                        Debug.LogWarning($"VAO source changed for {package.Title}. Open its Content Browser to preview a transactional reimport.", package);
                }
            }
        }
    }
}
