using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Modavis.Vao.Editor
{
    public static class VaoBatchImport
    {
        public static void Run()
        {
            var archive = Environment.GetEnvironmentVariable("VAO_ARCHIVE");
            if (string.IsNullOrWhiteSpace(archive)) throw new InvalidOperationException("VAO_ARCHIVE is not set.");
            var destination = Environment.GetEnvironmentVariable("VAO_DESTINATION") ?? "Assets/VAO Batch Imports";
            var modeText = Environment.GetEnvironmentVariable("VAO_MATERIALIZATION_MODE") ?? nameof(VaoMaterializationMode.AllEmbedded);
            if (!Enum.TryParse(modeText, true, out VaoMaterializationMode mode)) throw new InvalidOperationException($"Unknown VAO_MATERIALIZATION_MODE {modeText}.");
            var groups = (Environment.GetEnvironmentVariable("VAO_GROUPS") ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(item => item.Trim()).Where(item => item.Length > 0).ToList();
            var maximumText = Environment.GetEnvironmentVariable("VAO_MAX_BYTES");
            var maximum = long.TryParse(maximumText, out var parsedMaximum) ? parsedMaximum : 16L * 1024 * 1024 * 1024;
            var options = new VaoImportOptions
            {
                DestinationAssetPath = destination, VerifyPayloadDigests = true, CreatePrefab = true,
                GenerateMidiAnimationClips = true, CopyGlbToStreamingAssets = true,
                MaterializationMode = mode, MaximumMaterializedBytes = maximum,
                SelectedAssetGroupIdentifiers = new List<string>(groups)
            };
            var result = VaoImporter.Import(archive, options);
            if (result.Package == null || result.Package.FormatVersion != "0.4.0") throw new InvalidOperationException("Batch import did not create a VAO 0.4.0 package asset.");
            if (result.Package.ProfileSections.Count == 0) throw new InvalidOperationException("VAO profile sections were not preserved for runtime access.");
            if (result.Package.LogicalAssets.Count == 0 || result.Package.LogicalAssets.Any(item => item.RealizationIdentifiers == null)) throw new InvalidOperationException("Logical asset identities and realization membership were not compiled.");
            if (result.Package.Prefab == null) throw new InvalidOperationException("Batch import did not create a runtime prefab.");
            if (result.Package.Prefab.GetComponent<VaoMediaPlayer>() == null || result.Package.Prefab.GetComponent<VaoTrackedPlacement>() == null || result.Package.Prefab.GetComponent<VaoPresentationSelector>() == null || result.Package.Prefab.GetComponent<VaoDeterministicExecutor>() == null) throw new InvalidOperationException("The prefab is missing media transport, presentation selection, deterministic execution, or tracker-neutral placement.");
            var materialized = result.Package.Realizations.Where(item => item.IsMaterialized).Select(item => item.Identifier).ToHashSet(StringComparer.Ordinal);
            if (result.Package.SampleBindings.Any(item => (item.Clip != null) != materialized.Contains(item.RealizationIdentifier))) throw new InvalidOperationException("Sample materialization metadata and imported AudioClip references disagree.");
            if (AssetDatabase.LoadAssetAtPath<TextAsset>(result.MaterializationReceiptPath) == null) throw new InvalidOperationException("The validated materialization receipt was not imported.");
            Debug.Log($"VAO_BATCH_IMPORT_OK id={result.Package.Identifier} logicalAssets={result.Package.LogicalAssets.Count} realizations={result.Package.Realizations.Count} materialized={materialized.Count} bytes={result.MaterializedBytes} samples={result.Package.SampleBindings.Count} sampleClips={result.Package.SampleBindings.Count(item => item.Clip != null)} animations={result.Package.AnimationLinks.Count} acoustics={result.Package.AcousticScenes.Count} responsePoints={result.Package.AcousticScenes.Sum(item => item.ResponsePoints.Count)} frames={result.Package.CoordinateFrames.Count} poses={result.Package.Poses.Count} states={result.Package.StateVariables.Count} transitions={result.Package.Transitions.Count} processes={result.Package.ProcessModels.Count} timebases={result.Package.Timebases.Count} mappings={result.Package.SynchronizationMappings.Count} midiBindings={result.Package.ProtocolBindings.Count} sections={result.Package.ProfileSections.Count}");
            AssetDatabase.SaveAssets();
        }
    }
}
