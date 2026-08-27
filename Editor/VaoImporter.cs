using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Modavis.Vao;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Modavis.Vao.Editor
{
    public enum VaoMaterializationMode { AllEmbedded, RuntimeRequired, SelectedAssetGroups, MetadataOnly }

    [Serializable]
    public sealed class VaoImportOptions
    {
        public string DestinationAssetPath = "Assets/VAO Imports";
        public bool CreatePrefab = true;
        public bool CreateRuntimeControlSurface;
        public bool GenerateMidiAnimationClips = true;
        public bool CopyGlbToStreamingAssets = true;
        public bool VerifyPayloadDigests = true;
        public VaoMaterializationMode MaterializationMode = VaoMaterializationMode.AllEmbedded;
        public List<string> SelectedAssetGroupIdentifiers = new();
        public long MaximumMaterializedBytes = 16L * 1024 * 1024 * 1024;
    }

    public sealed class VaoImportResult
    {
        public VaoArchiveInspection Inspection { get; internal set; }
        public VaoPackageAsset Package { get; internal set; }
        public string PackageAssetPath { get; internal set; }
        public string PrefabAssetPath { get; internal set; }
        public List<string> ImportedAssetPaths { get; } = new();
        public long MaterializedBytes { get; internal set; }
        public int SkippedRealizationCount { get; internal set; }
        public string MaterializationReceiptPath { get; internal set; }
    }

    public static class VaoImporter
    {
        public static VaoImportResult Import(string archivePath, VaoImportOptions options = null)
        {
            options ??= new VaoImportOptions();
            var inspection = VaoArchiveReader.Inspect(archivePath, new VaoValidationPolicy { VerifyPayloadDigests = options.VerifyPayloadDigests });
            if (!inspection.IsValid) throw new InvalidDataException("VAO validation failed:\n" + string.Join("\n", inspection.Errors));
            var root = NormalizeDestination(options.DestinationAssetPath, inspection.Title, inspection.ArchiveSha256);
            if (AssetDatabase.IsValidFolder(root) || Directory.Exists(Absolute(root))) throw new IOException($"Import destination already exists: {root}");
            var result = new VaoImportResult { Inspection = inspection };
            var selection = BuildMaterializationSelection(inspection, options);
            result.MaterializedBytes = selection.Sum(id => inspection.EmbeddedRealizations[id].Entry.Length);
            result.SkippedRealizationCount = inspection.EmbeddedRealizations.Count - selection.Count;
            if (result.MaterializedBytes > options.MaximumMaterializedBytes) throw new IOException($"Selected materialization requires {EditorUtility.FormatBytes(result.MaterializedBytes)}, exceeding the configured {EditorUtility.FormatBytes(options.MaximumMaterializedBytes)} limit.");
            try
            {
                Directory.CreateDirectory(Absolute(root));
                var sourceFolder = root + "/Source";
                Directory.CreateDirectory(Absolute(sourceFolder));
                File.WriteAllBytes(Absolute(sourceFolder + "/vao-manifest.json"), inspection.ManifestBytes);
                File.WriteAllBytes(Absolute(sourceFolder + "/vao-carrier.json"), inspection.CarrierBytes);
                result.MaterializationReceiptPath = WriteMaterializationReceipt(inspection, root, options);
                var extracted = ExtractPayload(inspection, root, selection, result.MaterializedBytes);
                var streaming = CopyRuntimeGlb(inspection, extracted, options);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                ConfigureAcousticAudioImporters(inspection.Manifest, extracted);

                result.ImportedAssetPaths.AddRange(extracted.Values);
                var package = ScriptableObject.CreateInstance<VaoPackageAsset>();
                package.name = Sanitize(inspection.Title) + " VAO";
                var packagePath = root + "/" + package.name + ".asset";
                AssetDatabase.CreateAsset(package, packagePath);
                Compile(inspection, package, extracted, streaming, root, options, result);
                package.ImportSettings.ManagedRelativePaths = Directory.EnumerateFiles(Absolute(root), "*", SearchOption.AllDirectories)
                    .Where(path => !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    .Select(path => path.Substring(Absolute(root).Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/'))
                    .OrderBy(path => path, StringComparer.Ordinal).ToArray();
                EditorUtility.SetDirty(package);
                AssetDatabase.SaveAssets();
                result.Package = package;
                result.PackageAssetPath = packagePath;
                Selection.activeObject = package;
                return result;
            }
            catch
            {
                if (Directory.Exists(Absolute(root))) Directory.Delete(Absolute(root), true);
                AssetDatabase.Refresh();
                throw;
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        public static HashSet<string> BuildMaterializationSelection(VaoArchiveInspection inspection, VaoImportOptions options)
        {
            var all = inspection.EmbeddedRealizations.Keys.ToHashSet(StringComparer.Ordinal);
            if (options.MaterializationMode == VaoMaterializationMode.AllEmbedded) return all;
            if (options.MaterializationMode == VaoMaterializationMode.MetadataOnly) return new HashSet<string>(StringComparer.Ordinal);
            var manifest = inspection.Manifest;
            var selected = new HashSet<string>(StringComparer.Ordinal);
            var realizationIdsByLogical = manifest["realizations"]?.OfType<JObject>().GroupBy(item => item.Value<string>("assetId")).ToDictionary(group => group.Key, group => group.Select(item => item.Value<string>("id")).ToArray(), StringComparer.Ordinal) ?? new Dictionary<string, string[]>();
            var sampleRealizations = manifest.SelectToken("playable.sampleVariants")?.OfType<JObject>().Select(item => item.Value<string>("realizationId")).Where(id => id != null).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
            var essentialLogical = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in manifest.SelectToken("acoustics.geometryBindings")?.OfType<JObject>().Where(item => item.Value<string>("role") == "runtime-visual") ?? Enumerable.Empty<JObject>()) essentialLogical.Add(binding.Value<string>("logicalAssetId"));
            foreach (var response in manifest.SelectToken("acoustics.responseSets")?.OfType<JObject>() ?? Enumerable.Empty<JObject>()) essentialLogical.Add(response.Value<string>("logicalAssetId"));
            foreach (var scene in manifest.SelectToken("acoustics.audioScenes")?.OfType<JObject>() ?? Enumerable.Empty<JObject>()) foreach (var id in scene["mediaAssetIds"]?.Values<string>() ?? Enumerable.Empty<string>()) essentialLogical.Add(id);
            foreach (var relation in manifest["relations"]?.OfType<JObject>().Where(item => item.Value<string>("predicate")?.EndsWith("drivesAnimation", StringComparison.Ordinal) == true) ?? Enumerable.Empty<JObject>()) { essentialLogical.Add(relation.Value<string>("subjectId")); essentialLogical.Add(relation.Value<string>("objectId")); }
            foreach (var logical in essentialLogical.Where(id => id != null)) if (realizationIdsByLogical.TryGetValue(logical, out var ids)) foreach (var id in ids) if (!sampleRealizations.Contains(id)) selected.Add(id);
            var selectedGroups = BuildMaterializationGroupSelection(inspection, options);
            var groups = manifest["assetGroups"]?.OfType<JObject>().Where(item => item.Value<string>("id") != null).ToDictionary(item => item.Value<string>("id"), StringComparer.Ordinal) ?? new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var groupId in selectedGroups)
                if (groups.TryGetValue(groupId, out var group)) foreach (var id in group["realizationIds"]?.Values<string>() ?? Enumerable.Empty<string>()) selected.Add(id);
            // Older or hand-authored packages may omit asset-group dependencies;
            // retain the minimum declared visual/acoustic/animation closure needed
            // to construct a truthful runtime prefab.
            if (options.MaterializationMode == VaoMaterializationMode.RuntimeRequired && groups.Count == 0)
                foreach (var id in sampleRealizations) selected.Add(id);
            selected.IntersectWith(all);
            return selected;
        }

        public static HashSet<string> BuildMaterializationGroupSelection(VaoArchiveInspection inspection, VaoImportOptions options)
        {
            var groups = inspection.Manifest["assetGroups"]?.OfType<JObject>().Where(item => item.Value<string>("id") != null).ToDictionary(item => item.Value<string>("id"), StringComparer.Ordinal) ?? new Dictionary<string, JObject>(StringComparer.Ordinal);
            var roots = new HashSet<string>(StringComparer.Ordinal);
            if (options.MaterializationMode == VaoMaterializationMode.AllEmbedded)
                foreach (var id in inspection.Carrier["completeGroupIds"]?.Values<string>() ?? groups.Keys) roots.Add(id);
            else if (options.MaterializationMode == VaoMaterializationMode.RuntimeRequired)
                foreach (var group in groups.Values.Where(item => item.Value<string>("availability") is "offline-required" or "remote-required")) roots.Add(group.Value<string>("id"));
            else if (options.MaterializationMode == VaoMaterializationMode.SelectedAssetGroups)
                foreach (var id in (IEnumerable<string>)options.SelectedAssetGroupIdentifiers ?? Enumerable.Empty<string>()) roots.Add(id);

            var pending = new Stack<string>(roots);
            var closure = new HashSet<string>(StringComparer.Ordinal);
            while (pending.Count > 0)
            {
                var groupId = pending.Pop();
                if (!closure.Add(groupId) || !groups.TryGetValue(groupId, out var group)) continue;
                foreach (var dependency in group["dependsOnGroupIds"]?.Values<string>() ?? Enumerable.Empty<string>()) pending.Push(dependency);
            }
            closure.IntersectWith(groups.Keys);
            return closure;
        }

        private static Dictionary<string, string> ExtractPayload(VaoArchiveInspection inspection, string root, ISet<string> selection, long totalBytes)
        {
            var extracted = new Dictionary<string, string>(StringComparer.Ordinal);
            long completed = 0;
            using var archive = ZipFile.OpenRead(inspection.ArchivePath);
            foreach (var embedded in inspection.EmbeddedRealizations.Values.Where(item => selection.Contains(item.Identifier)).OrderBy(item => item.CarrierPath, StringComparer.Ordinal))
            {
                if (EditorUtility.DisplayCancelableProgressBar("Materializing VAO", embedded.CarrierPath, totalBytes == 0 ? 1f : completed / (float)totalBytes)) throw new OperationCanceledException("VAO materialization was cancelled.");
                var relative = embedded.CarrierPath.StartsWith("payload/", StringComparison.Ordinal) ? embedded.CarrierPath[8..] : embedded.CarrierPath;
                var assetPath = root + "/Payload/" + relative;
                var absolute = Absolute(assetPath);
                Directory.CreateDirectory(Path.GetDirectoryName(absolute) ?? Absolute(root));
                using var input = archive.GetEntry(embedded.CarrierPath)?.Open() ?? throw new InvalidDataException($"Missing payload entry {embedded.CarrierPath}.");
                using var output = new FileStream(absolute, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[1024 * 1024];
                while (true)
                {
                    var read = input.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    output.Write(buffer, 0, read);
                    completed += read;
                    if (EditorUtility.DisplayCancelableProgressBar("Materializing VAO", embedded.CarrierPath, totalBytes == 0 ? 1f : completed / (float)totalBytes)) throw new OperationCanceledException("VAO materialization was cancelled.");
                }
                extracted[embedded.Identifier] = assetPath;
            }
            return extracted;
        }

        private static string WriteMaterializationReceipt(VaoArchiveInspection inspection, string root, VaoImportOptions options)
        {
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var selectedGroups = BuildMaterializationGroupSelection(inspection, options);
            var completeGroups = inspection.Carrier["completeGroupIds"]?.Values<string>().ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(VaoImporter).Assembly);
            if (packageInfo == null) throw new InvalidOperationException("Cannot identify the installed VAO package while creating the materialization receipt.");
            var importerSource = Path.Combine(packageInfo.resolvedPath, "Editor", "VaoImporter.cs");
            var implementationIdentity = HashFile(importerSource);
            var profileRecords = (inspection.Manifest["profiles"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                .Concat(inspection.Manifest["materializableProfiles"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>()).ToList();
            var receipt = new JObject
            {
                ["$schema"] = "https://w3id.org/modavis/vao/0.4.0/schema/materialization-receipt.json", ["type"] = "VAOMaterializationReceipt", ["formatVersion"] = "0.4.0",
                ["releaseId"] = inspection.Manifest.SelectToken("release.id")?.Value<string>(), ["manifestSHA256"] = inspection.Carrier.Value<string>("manifestSHA256"), ["instanceId"] = "urn:uuid:" + Guid.NewGuid().ToString(), ["createdAt"] = now,
                ["implementation"] = new JObject
                {
                    ["name"] = packageInfo.displayName, ["version"] = packageInfo.version,
                    ["identity"] = new JObject { ["algorithm"] = "sha256", ["value"] = implementationIdentity },
                    ["identityScope"] = "source-file",
                    ["identityDescription"] = "SHA-256 of the exact Editor/VaoImporter.cs source file in the installed Unity package; this identifies the receipt-producing entry point, not the complete Unity player or project environment."
                },
                ["sourceCarrier"] = new JObject
                {
                    ["kind"] = "packed-carrier", ["descriptorByteSize"] = inspection.CarrierBytes.LongLength,
                    ["descriptorSHA256"] = HashBytes(inspection.CarrierBytes), ["packedCarrierByteSize"] = new FileInfo(inspection.ArchivePath).Length,
                    ["packedCarrierSHA256"] = inspection.ArchiveSha256
                },
                ["selectedGroupIds"] = new JArray(selectedGroups.OrderBy(id => id, StringComparer.Ordinal)),
                ["acquisitions"] = new JArray(),
                ["profileStates"] = new JArray(profileRecords.Select(item =>
                {
                    var groups = item["groupIds"]?.Values<string>().ToArray();
                    var state = groups == null ? "embedded-valid" : groups.All(selectedGroups.Contains) ? "materialized-valid" : groups.All(completeGroups.Contains) ? "embedded-valid" : "incomplete";
                    return new JObject { ["profileId"] = item.Value<string>("id"), ["state"] = state };
                }))
            };
            var receiptErrors = VaoJsonSchemaValidator.ValidateMaterializationReceipt(receipt);
            if (receiptErrors.Count > 0) throw new InvalidDataException("Generated VAO materialization receipt is invalid:\n" + string.Join("\n", receiptErrors));
            var path = root + "/Source/vao-materialization-receipt.json";
            File.WriteAllText(Absolute(path), receipt.ToString(Formatting.Indented) + "\n", new UTF8Encoding(false));
            return path;
        }

        private static string HashFile(string path)
        {
            using var algorithm = SHA256.Create();
            using var stream = File.OpenRead(path);
            return Hex(algorithm.ComputeHash(stream));
        }

        private static string HashBytes(byte[] value)
        {
            using var algorithm = SHA256.Create();
            return Hex(algorithm.ComputeHash(value));
        }

        private static string Hex(byte[] value) => string.Concat(value.Select(item => item.ToString("x2", CultureInfo.InvariantCulture)));

        private static Dictionary<string, string> CopyRuntimeGlb(VaoArchiveInspection inspection, IReadOnlyDictionary<string, string> extracted, VaoImportOptions options)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!options.CopyGlbToStreamingAssets) return result;
            var folder = "Assets/StreamingAssets/VAO/" + inspection.ArchiveSha256[..12];
            foreach (var pair in extracted)
            {
                var record = inspection.EmbeddedRealizations[pair.Key].ManifestRecord;
                if (record.Value<string>("mediaType") != "model/gltf-binary") continue;
                var fileName = record.Value<string>("sha256")[..12] + "-" + Path.GetFileName(pair.Value);
                var target = folder + "/" + fileName;
                Directory.CreateDirectory(Path.GetDirectoryName(Absolute(target)) ?? Absolute(folder));
                if (!File.Exists(Absolute(target))) File.Copy(Absolute(pair.Value), Absolute(target), false);
                result[pair.Key] = "VAO/" + inspection.ArchiveSha256[..12] + "/" + fileName;
            }
            return result;
        }

        private static void ConfigureAcousticAudioImporters(JObject manifest, IReadOnlyDictionary<string, string> extracted)
        {
            var logicalIds = manifest.SelectToken("acoustics.responseSets")?.OfType<JObject>().Select(item => item.Value<string>("logicalAssetId")).Where(item => item != null).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
            var realizationIds = manifest["realizations"]?.OfType<JObject>().Where(item => logicalIds.Contains(item.Value<string>("assetId"))).Select(item => item.Value<string>("id")) ?? Enumerable.Empty<string>();
            foreach (var id in realizationIds)
            {
                if (!extracted.TryGetValue(id, out var path) || AssetImporter.GetAtPath(path) is not AudioImporter importer) continue;
                var settings = importer.defaultSampleSettings;
                settings.loadType = AudioClipLoadType.DecompressOnLoad;
                settings.preloadAudioData = true;
                importer.defaultSampleSettings = settings;
                importer.loadInBackground = false;
                importer.SaveAndReimport();
            }
        }

        private static void Compile(VaoArchiveInspection inspection, VaoPackageAsset package, IReadOnlyDictionary<string, string> extracted, IReadOnlyDictionary<string, string> streaming, string root, VaoImportOptions options, VaoImportResult result)
        {
            var manifest = inspection.Manifest;
            package.FormatVersion = manifest.Value<string>("formatVersion");
            package.Identifier = manifest.Value<string>("id");
            package.ReleaseIdentifier = manifest.SelectToken("release.id")?.Value<string>();
            package.Title = VaoJson.Localized(manifest["title"]);
            package.Description = VaoJson.Localized(manifest["description"]);
            package.SourceArchiveSha256 = inspection.ArchiveSha256;
            var sourceAssetPath = ToAssetPath(inspection.ArchivePath);
            package.SourceArchivePath = sourceAssetPath ?? inspection.ArchivePath;
            package.SourceArchiveGuid = sourceAssetPath != null ? AssetDatabase.AssetPathToGUID(sourceAssetPath) : string.Empty;
            package.ImportedAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            package.ImportSettings.MaterializationMode = options.MaterializationMode.ToString();
            package.ImportSettings.SelectedAssetGroupIdentifiers = options.SelectedAssetGroupIdentifiers?.ToArray() ?? Array.Empty<string>();
            package.ImportSettings.MaximumMaterializedBytes = options.MaximumMaterializedBytes;
            package.ImportSettings.CreatePrefab = options.CreatePrefab;
            package.ImportSettings.CreateRuntimeControlSurface = options.CreateRuntimeControlSurface;
            package.ImportSettings.GenerateMidiAnimationClips = options.GenerateMidiAnimationClips;
            package.ImportSettings.CopyGlbToStreamingAssets = options.CopyGlbToStreamingAssets;
            package.ImportSettings.VerifyPayloadDigests = options.VerifyPayloadDigests;
            package.RawManifestJson = manifest.ToString(Formatting.None);
            foreach (var name in new[] { "profiles", "scientific", "multimodal", "physicalSystem", "playable", "interactionModel", "runtime", "acoustics", "discovery", "rights", "distributions", "repositoryBindings", "assetGroups" })
                if (manifest[name] != null) package.ProfileSections.Add(new VaoJsonSectionRecord { Name = name, Json = manifest[name].ToString(Formatting.None) });
            package.Capabilities.AddRange(manifest.SelectTokens("$..requiredCapabilities[*]").Values<string>().Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal));

            foreach (var entity in manifest["entities"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                package.Entities.Add(new VaoEntityRecord { Identifier = entity.Value<string>("id"), Kind = entity.Value<string>("kind"), Label = VaoJson.Localized(entity["labels"]), Types = entity["types"]?.Values<string>().ToArray() ?? Array.Empty<string>() });
            foreach (var relation in manifest["relations"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                package.Relations.Add(new VaoRelationRecord { Identifier = relation.Value<string>("id"), SubjectIdentifier = relation.Value<string>("subjectId"), Predicate = relation.Value<string>("predicate"), ObjectIdentifier = relation.Value<string>("objectId"), Status = relation.Value<string>("status"), PropertiesJson = relation["properties"]?.ToString(Formatting.None) });

            var logicalByRealization = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var logical in manifest["logicalAssets"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                package.LogicalAssets.Add(new VaoLogicalAssetRecord
                {
                    Identifier = logical.Value<string>("id"),
                    Label = VaoJson.Localized(logical["labels"]),
                    Roles = logical["roles"]?.Values<string>().ToArray() ?? Array.Empty<string>(),
                    AboutEntityIdentifiers = logical["aboutEntityIds"]?.Values<string>().ToArray() ?? Array.Empty<string>(),
                    RealizationIdentifiers = logical["realizationIds"]?.Values<string>().ToArray() ?? Array.Empty<string>(),
                    PropertiesJson = logical["properties"]?.ToString(Formatting.None)
                });
                foreach (var id in logical["realizationIds"]?.Values<string>() ?? Enumerable.Empty<string>()) logicalByRealization[id] = logical;
            }
            foreach (var realizationRecord in manifest["realizations"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var identifier = realizationRecord.Value<string>("id");
                logicalByRealization.TryGetValue(identifier, out var logical);
                extracted.TryGetValue(identifier, out var assetPath);
                inspection.EmbeddedRealizations.TryGetValue(identifier, out var embedded);
                package.Realizations.Add(new VaoRealizationRecord
                {
                    Identifier = identifier,
                    LogicalAssetIdentifier = realizationRecord.Value<string>("assetId"),
                    MediaType = realizationRecord.Value<string>("mediaType"),
                    Sha256 = realizationRecord.Value<string>("sha256"),
                    ByteSize = realizationRecord.Value<long>("byteSize"),
                    AssetPath = assetPath,
                    CarrierPath = embedded?.CarrierPath,
                    CoordinateFrameIdentifier = realizationRecord.SelectToken("technicalMetadata.coordinateFrameId")?.Value<string>(),
                    IsMaterialized = !string.IsNullOrEmpty(assetPath),
                    RuntimeUri = streaming.TryGetValue(identifier, out var uri) ? uri : !string.IsNullOrEmpty(assetPath) ? new Uri(Absolute(assetPath)).AbsoluteUri : null,
                    Roles = logical?["roles"]?.Values<string>().ToArray() ?? Array.Empty<string>(),
                    RightsIdentifiers = realizationRecord["rightsIds"]?.Values<string>().ToArray() ?? Array.Empty<string>(),
                    DistributionIdentifiers = realizationRecord["distributionIds"]?.Values<string>().ToArray() ?? Array.Empty<string>(),
                    QualityTier = realizationRecord.Value<string>("qualityTier"),
                    ImportedObject = !string.IsNullOrEmpty(assetPath) ? AssetDatabase.LoadMainAssetAtPath(assetPath) : null
                });
            }

            foreach (var distribution in manifest["distributions"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                package.Distributions.Add(new VaoDistributionRecord
                {
                    Identifier = distribution.Value<string>("id"), Kind = distribution.Value<string>("kind"), RepositoryBindingIdentifier = distribution.Value<string>("repositoryBindingId"),
                    PersistentIdentifier = distribution.Value<string>("persistentIdentifier"), ConceptIdentifier = distribution.Value<string>("conceptIdentifier"),
                    RecordIdentifier = distribution.Value<string>("recordIdentifier"), FileIdentifier = distribution.Value<string>("fileIdentifier"), Access = distribution.Value<string>("access"),
                    TransportSha256 = distribution.SelectToken("transportChecksums.sha256")?.Value<string>(), PackRealizationIdentifier = distribution.Value<string>("packRealizationId"),
                    MemberPath = distribution.Value<string>("memberPath"), PackManifestSha256 = distribution.Value<string>("packManifestSHA256")
                });
            foreach (var binding in manifest["repositoryBindings"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                package.RepositoryBindings.Add(new VaoRepositoryBindingRecord
                {
                    Identifier = binding.Value<string>("id"), RepositoryType = binding.Value<string>("repositoryType"), Instance = binding.Value<string>("instance"),
                    ApiProfile = binding.Value<string>("apiProfile"), ResolutionPolicy = binding.Value<string>("resolutionPolicy")
                });
            foreach (var rights in manifest["rights"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                package.Rights.Add(new VaoRightsRecord
                {
                    Identifier = rights.Value<string>("id"), AppliesToIdentifiers = rights["appliesToIds"]?.Values<string>().ToArray() ?? Array.Empty<string>(),
                    License = rights.Value<string>("license"), Statement = VaoJson.Localized(rights["statement"]), Access = rights.Value<string>("access"), Attribution = rights.Value<string>("attribution")
                });
            foreach (var group in manifest["assetGroups"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                package.AssetGroups.Add(new VaoAssetGroupRecord
                {
                    Identifier = group.Value<string>("id"), Label = VaoJson.Localized(group["labels"]), Availability = group.Value<string>("availability"), QualityTier = group.Value<string>("qualityTier"),
                    RealizationIdentifiers = group["realizationIds"]?.Values<string>().ToArray() ?? Array.Empty<string>(), DependencyIdentifiers = group["dependsOnGroupIds"]?.Values<string>().ToArray() ?? Array.Empty<string>(),
                    TotalByteSize = group.Value<long?>("totalByteSize") ?? 0L, Evictable = group.SelectToken("cachePolicy.evictable")?.Value<bool>() ?? true, CachePriority = group.SelectToken("cachePolicy.priority")?.Value<int>() ?? 0
                });

            var sequencesByLogical = ImportMidiSequences(package, root, logicalByRealization, options, result);
            CompileControls(manifest, package);
            CompileExecution(manifest, package);
            CompileSamples(manifest, package);
            CompileAnimations(manifest, package, sequencesByLogical, root, options, result);
            CompileSpatial(manifest, package);
            CompileAcoustics(manifest, package, root, result);
            if (options.CreatePrefab) CreatePrefab(manifest, package, root, options, result);
        }

        private static void CompileSpatial(JObject manifest, VaoPackageAsset package)
        {
            foreach (var frame in manifest.SelectToken("acoustics.coordinateFrames")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                package.CoordinateFrames.Add(new VaoCoordinateFrameRecord
                {
                    Identifier = frame.Value<string>("id"), ParentFrameIdentifier = frame.Value<string>("parentFrameId"), CoordinateType = frame.Value<string>("coordinateType"),
                    Unit = frame.Value<string>("unit"), UpAxis = frame.Value<string>("upAxis"), ForwardAxis = frame.Value<string>("forwardAxis"), Handedness = frame.Value<string>("handedness"),
                    TransformToParent = frame["transformToParent"]?.Values<float>().ToArray() ?? Array.Empty<float>()
                });
            foreach (var pose in manifest.SelectToken("acoustics.poses")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                package.Poses.Add(new VaoPoseRecord
                {
                    Identifier = pose.Value<string>("id"), SubjectIdentifier = pose.Value<string>("subjectId"), CoordinateFrameIdentifier = pose.Value<string>("frameId"), Interpolation = pose.Value<string>("interpolation"),
                    Position = Vector(pose["position"], Vector3.zero), Orientation = Rotation(pose["orientationXYZW"]), Scale = Vector(pose["scale"], Vector3.one)
                });
            foreach (var binding in manifest.SelectToken("acoustics.geometryBindings")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                package.GeometryBindings.Add(new VaoGeometryBindingRecord { Identifier = binding.Value<string>("id"), LogicalAssetIdentifier = binding.Value<string>("logicalAssetId"), SubjectIdentifier = binding.Value<string>("subjectId"), Role = binding.Value<string>("role") });
        }

        private static Vector3 Vector(JToken token, Vector3 fallback)
        {
            var values = token?.Values<float>().ToArray();
            return values is { Length: >= 3 } ? new Vector3(values[0], values[1], values[2]) : fallback;
        }

        private static Quaternion Rotation(JToken token)
        {
            var values = token?.Values<float>().ToArray();
            return values is { Length: >= 4 } ? new Quaternion(values[0], values[1], values[2], values[3]).normalized : Quaternion.identity;
        }

        private static Dictionary<string, VaoMidiSequenceAsset> ImportMidiSequences(VaoPackageAsset package, string root, IReadOnlyDictionary<string, JObject> logicalByRealization, VaoImportOptions options, VaoImportResult result)
        {
            var sequences = new Dictionary<string, VaoMidiSequenceAsset>(StringComparer.Ordinal);
            var generated = root + "/Generated";
            Directory.CreateDirectory(Absolute(generated));
            foreach (var realization in package.Realizations.Where(item => item.IsMaterialized && item.MediaType is "audio/midi" or "audio/x-midi" or "application/midi"))
            {
                var sequence = VaoMidiParser.ParseFile(Absolute(realization.AssetPath), Path.GetFileNameWithoutExtension(realization.AssetPath));
                var path = AssetDatabase.GenerateUniqueAssetPath(generated + "/" + Sanitize(sequence.name) + " MIDI.asset");
                AssetDatabase.CreateAsset(sequence, path);
                sequences[realization.LogicalAssetIdentifier] = sequence;
                result.ImportedAssetPaths.Add(path);
            }
            package.MidiSequences = sequences.Values.ToArray();
            return sequences;
        }

        private static void CompileControls(JObject manifest, VaoPackageAsset package)
        {
            var stateByControl = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var transition in manifest.SelectToken("interactionModel.transitions")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var state = transition["actions"]?.OfType<JObject>().FirstOrDefault(item => item.Value<string>("operation") is "toggle-state" or "set-state")?.Value<string>("targetId");
                if (!string.IsNullOrEmpty(state)) stateByControl[transition.Value<string>("controlId")] = state;
            }
            var stateDefaults = manifest.SelectToken("interactionModel.stateVariables")?.OfType<JObject>().ToDictionary(item => item.Value<string>("id"), item => item.Value<bool?>("defaultValue") ?? false, StringComparer.Ordinal) ?? new Dictionary<string, bool>();
            var midiByControl = manifest.SelectToken("interactionModel.protocolBindings")?.OfType<JObject>().Where(item => item.Value<string>("protocol") is "MIDI-1.0" or "MIDI-2.0").GroupBy(item => item.Value<string>("controlId")).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal) ?? new Dictionary<string, JObject>();
            foreach (var control in manifest.SelectToken("interactionModel.controls")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var id = control.Value<string>("id");
                stateByControl.TryGetValue(id, out var state);
                midiByControl.TryGetValue(id, out var midi);
                package.Controls.Add(new VaoControlRecord { Identifier = id, Label = VaoJson.Localized(control["labels"]), Behavior = control.Value<string>("controlBehavior"), ValueType = control.Value<string>("valueType"), StateVariableIdentifier = state, DefaultBoolean = state != null && stateDefaults.TryGetValue(state, out var value) && value, MidiChannel = midi?.Value<int?>("channel") ?? -1, MidiNumber = midi?.Value<int?>("number") ?? -1, MidiMessageType = midi?.Value<string>("messageType") });
            }
            foreach (var state in manifest.SelectToken("interactionModel.stateVariables")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                package.StateVariables.Add(new VaoStateVariableRecord
                {
                    Identifier = state.Value<string>("id"), Label = VaoJson.Localized(state["labels"]), ValueType = state.Value<string>("valueType"), Persistence = state.Value<string>("persistence"),
                    SubjectEntityIdentifier = state.Value<string>("subjectEntityId"), DefaultValue = Primitive(state["defaultValue"]),
                    HasMinimum = state["minimumValue"] != null, MinimumValue = state.Value<double?>("minimumValue") ?? 0d,
                    HasMaximum = state["maximumValue"] != null, MaximumValue = state.Value<double?>("maximumValue") ?? 0d
                });
            foreach (var transition in manifest.SelectToken("interactionModel.transitions")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var compiled = new VaoTransitionRecord { Identifier = transition.Value<string>("id"), ControlIdentifier = transition.Value<string>("controlId"), EventTypeIdentifier = transition.Value<string>("eventTypeId"), Atomic = transition.Value<bool>("atomic"), ConflictPolicy = transition.Value<string>("conflictPolicy"), Priority = transition.Value<int?>("priority") ?? 0 };
                foreach (var condition in transition["conditions"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                    compiled.Conditions.Add(new VaoStateConditionRecord { StateVariableIdentifier = condition.Value<string>("stateVariableId"), Operator = condition.Value<string>("operator"), Value = Primitive(condition["value"]) });
                foreach (var action in transition["actions"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                    compiled.Actions.Add(new VaoDeclarativeActionRecord { Operation = action.Value<string>("operation"), TargetIdentifier = action.Value<string>("targetId"), HasValue = action["value"] != null, Value = Primitive(action["value"]), KeyOffset = action.Value<int?>("keyOffset") ?? 0, DelayConstraintIdentifier = action.Value<string>("delayConstraintId"), ExecutionGroup = action.Value<string>("executionGroup") });
                package.Transitions.Add(compiled);
            }
            foreach (var binding in manifest.SelectToken("interactionModel.protocolBindings")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                package.ProtocolBindings.Add(new VaoProtocolBindingRecord
                {
                    Identifier = binding.Value<string>("id"), Protocol = binding.Value<string>("protocol"), Direction = binding.Value<string>("direction"), ControlIdentifier = binding.Value<string>("controlId"), EventTypeIdentifier = binding.Value<string>("eventTypeId"), MessageType = binding.Value<string>("messageType"),
                    Channel = binding.Value<int?>("channel") ?? 0, ChannelNumberingBase = binding.Value<int?>("channelNumberingBase") ?? 0, Number = binding.Value<int?>("number") ?? -1,
                    HasActivationValue = binding["activationValue"] != null, ActivationValue = Primitive(binding["activationValue"]),
                    HasDeactivationValue = binding["deactivationValue"] != null, DeactivationValue = Primitive(binding["deactivationValue"]),
                    UmpGroup = binding.Value<int?>("umpGroup") ?? -1, FunctionBlock = binding.Value<int?>("functionBlock") ?? -1, UmpMessageType = binding.Value<int?>("umpMessageType") ?? -1, DataResolutionBits = binding.Value<int?>("dataResolutionBits") ?? 7, JrTimestamp = binding.Value<bool?>("jrTimestamp") ?? false
                });
        }

        private static VaoPrimitiveValue Primitive(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return default;
            if (token.Type == JTokenType.Boolean) return VaoPrimitiveValue.FromBoolean(token.Value<bool>());
            if (token.Type is JTokenType.Integer or JTokenType.Float) return VaoPrimitiveValue.FromNumber(token.Value<double>());
            return VaoPrimitiveValue.FromText(token.Value<string>());
        }

        internal static void CompileExecution(JObject manifest, VaoPackageAsset package)
        {
            foreach (var item in manifest.SelectToken("interactionModel.eventTypes")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                package.EventTypes.Add(new VaoEventTypeRecord
                {
                    Identifier = item.Value<string>("id"), Label = VaoJson.Localized(item["labels"]), EventKind = item.Value<string>("eventKind"), ValueDomain = item.Value<string>("valueDomain")
                });

            var semantics = manifest.SelectToken("interactionModel.executionSemantics") as JObject ?? manifest.SelectToken("runtime.executionSemantics") as JObject;
            if (semantics != null)
            {
                var target = package.ExecutionSemantics;
                target.TimestampOrder = semantics.Value<string>("timestampOrder") ?? target.TimestampOrder;
                target.SimultaneousEventOrder = semantics.Value<string>("simultaneousEventOrder") ?? target.SimultaneousEventOrder;
                target.TransitionEvaluation = semantics.Value<string>("transitionEvaluation") ?? target.TransitionEvaluation;
                target.ActionExecution = semantics.Value<string>("actionExecution") ?? target.ActionExecution;
                target.RunToCompletion = semantics.Value<bool?>("runToCompletion") ?? target.RunToCompletion;
                target.ReentrancyPolicy = semantics.Value<string>("reentrancyPolicy") ?? target.ReentrancyPolicy;
                target.LateEventPolicy = semantics.Value<string>("lateEventPolicy") ?? target.LateEventPolicy;
                target.TimeResolution = semantics.SelectToken("timeResolution.value")?.Value<double>() ?? target.TimeResolution;
                target.TimeResolutionUnit = ShortUnit(semantics.SelectToken("timeResolution.unit")?.Value<string>());
                target.MaximumMicrosteps = semantics.Value<long?>("maximumMicrosteps") ?? target.MaximumMicrosteps;
                target.VoiceAllocation = semantics.Value<string>("voiceAllocation");
                target.MaximumVoices = semantics.Value<long?>("maximumVoices") ?? 0L;
            }

            foreach (var item in manifest.SelectToken("interactionModel.timingConstraints")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                package.TimingConstraints.Add(new VaoTimingConstraintRecord
                {
                    Identifier = item.Value<string>("id"), TimingKind = item.Value<string>("timingKind"), Unit = item.Value<string>("unit"), Minimum = item.Value<double>("minimum"),
                    HasTypical = item["typical"] != null, Typical = item.Value<double?>("typical") ?? 0d, HasMaximum = item["maximum"] != null, Maximum = item.Value<double?>("maximum") ?? 0d,
                    AppliesToIdentifiers = item["appliesToIds"]?.Values<string>().ToArray() ?? Array.Empty<string>()
                });

            foreach (var item in manifest.SelectToken("interactionModel.processModels")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var parameters = (item.SelectToken("probabilityDistribution.parameters") as JObject)?.Properties().OrderBy(value => value.Name, StringComparer.Ordinal).ToList() ?? new List<JProperty>();
                var process = new VaoProcessModelRecord
                {
                    Identifier = item.Value<string>("id"), ProcessKind = item.Value<string>("processKind"), Ordering = item.Value<string>("ordering"),
                    ChildProcessIdentifiers = item["childProcessIds"]?.Values<string>().ToArray() ?? Array.Empty<string>(), TimingConstraintIdentifiers = item["timingConstraintIds"]?.Values<string>().ToArray() ?? Array.Empty<string>(),
                    TerminationPolicy = item.Value<string>("terminationPolicy"), MaximumIterations = item.Value<long?>("maximumIterations") ?? 0L,
                    DurationConstraintIdentifier = item.Value<string>("durationConstraintId"), CancellationControlIdentifier = item.Value<string>("cancellationControlId"), RandomSourceIdentifier = item.Value<string>("randomSourceId"),
                    ProbabilityDistributionKind = item.SelectToken("probabilityDistribution.kind")?.Value<string>(), ProbabilityParameterNames = parameters.Select(value => value.Name).ToArray(), ProbabilityParameterValues = parameters.Select(value => value.Value.Value<long>()).ToArray()
                };
                foreach (var action in item["actions"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>()) process.Actions.Add(CompileAction(action));
                package.ProcessModels.Add(process);
            }

            foreach (var item in manifest.SelectToken("interactionModel.renderBindings")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var binding = new VaoRenderBindingRecord
                {
                    Identifier = item.Value<string>("id"), EventTypeIdentifier = item.Value<string>("eventTypeId"), ProcessModelIdentifier = item.Value<string>("processModelId"), SelectionPolicy = item.Value<string>("selectionPolicy"),
                    SampleMappingIdentifiers = item["sampleMappingIds"]?.Values<string>().ToArray() ?? Array.Empty<string>(), SampleVariantIdentifiers = item["sampleVariantIds"]?.Values<string>().ToArray() ?? Array.Empty<string>()
                };
                foreach (var condition in item["conditions"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>()) binding.Conditions.Add(CompileCondition(condition));
                package.RenderBindings.Add(binding);
            }

            foreach (var item in manifest.SelectToken("interactionModel.routingRules")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var rule = new VaoRoutingRuleRecord
                {
                    Identifier = item.Value<string>("id"), SourceControlIdentifier = item.Value<string>("sourceControlId"), SourceEntityIdentifier = item.Value<string>("sourceEntityId"), TargetEntityIdentifier = item.Value<string>("targetEntityId"),
                    RoutingBehavior = item.Value<string>("routingBehavior"), InputKeyMeaning = item.Value<string>("inputKeyMeaning"), OutputKeyMeaning = item.Value<string>("outputKeyMeaning"),
                    MinimumKey = item.SelectToken("inputRange.minimum")?.Value<int>() ?? 0, MaximumKey = item.SelectToken("inputRange.maximum")?.Value<int>() ?? 127,
                    KeyTransform = item.SelectToken("keyTransform.kind")?.Value<string>(), SemitoneOffset = item.SelectToken("keyTransform.semitoneOffset")?.Value<int>() ?? 0,
                    FixedOutputKeys = item.SelectToken("keyTransform.fixedOutputKeys")?.Values<int>().ToArray() ?? Array.Empty<int>(), DelayConstraintIdentifier = item.Value<string>("delayConstraintId")
                };
                foreach (var entry in item.SelectToken("keyTransform.entries")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                    rule.KeyTransformEntries.Add(new VaoKeyTransformEntryRecord { InputKey = entry.Value<int>("inputKey"), OutputKeys = entry["outputKeys"]?.Values<int>().ToArray() ?? Array.Empty<int>() });
                foreach (var condition in item["conditions"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>()) rule.Conditions.Add(CompileCondition(condition));
                package.RoutingRules.Add(rule);
            }

            foreach (var item in (manifest.SelectToken("runtime.randomSources")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                .Concat(manifest.SelectToken("interactionModel.randomSources")?.OfType<JObject>() ?? Enumerable.Empty<JObject>()))
                package.RandomSources.Add(new VaoRandomSourceRecord { Identifier = item.Value<string>("id"), Algorithm = item.Value<string>("algorithm"), Seed = item.Value<string>("seed"), Stream = item.Value<string>("stream") });

            foreach (var item in manifest.SelectToken("multimodal.timebases")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var rational = item["rate"] as JObject;
                var numerator = rational?.Value<long?>("numerator") ?? 0L;
                var denominator = rational?.Value<long?>("denominator") ?? 1L;
                package.Timebases.Add(new VaoTimebaseRecord
                {
                    Identifier = item.Value<string>("id"), Kind = item.Value<string>("kind"), Unit = item.Value<string>("unit"), RateUnit = item.Value<string>("rateUnit"),
                    HasRationalRate = rational != null, RateNumerator = numerator, RateDenominator = denominator,
                    Rate = rational == null ? item.Value<double>("rate") : numerator / (double)Math.Max(1L, denominator), Origin = item.Value<double>("origin"),
                    TimeScale = item.Value<string>("timeScale"), Epoch = item.Value<string>("epoch"), HasWrapPeriod = item["wrapPeriod"] != null, WrapPeriod = item.Value<double?>("wrapPeriod") ?? 0d
                });
            }
            foreach (var item in manifest.SelectToken("multimodal.tracks")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                package.Tracks.Add(new VaoTrackRecord { Identifier = item.Value<string>("id"), Modality = item.Value<string>("modality"), TimebaseIdentifier = item.Value<string>("timebaseId"), RealizationIdentifier = item.Value<string>("realizationId"), CoordinateFrameIdentifier = item.Value<string>("coordinateFrameId"), ChannelSelector = item.Value<string>("channelSelector"), Continuity = item.Value<string>("continuity") });
            foreach (var item in manifest.SelectToken("multimodal.synchronizationMappings")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var mapping = new VaoSynchronizationMappingRecord { Identifier = item.Value<string>("id"), SourceTimebaseIdentifier = item.Value<string>("sourceTimebaseId"), TargetTimebaseIdentifier = item.Value<string>("targetTimebaseId"), Method = item.Value<string>("method"), ActivityIdentifier = item.Value<string>("activityId") };
                foreach (var segment in item["segments"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>()) mapping.Segments.Add(new VaoClockSegmentRecord { SourceStart = segment.Value<double>("sourceStart"), SourceEndExclusive = segment.Value<double>("sourceEndExclusive"), Scale = segment.Value<double>("scale"), Offset = segment.Value<double>("offset"), DiscontinuityAfter = segment.Value<string>("discontinuityAfter") });
                package.SynchronizationMappings.Add(mapping);
            }
        }

        private static VaoDeclarativeActionRecord CompileAction(JObject action) => new()
        {
            Operation = action.Value<string>("operation"), TargetIdentifier = action.Value<string>("targetId"), HasValue = action["value"] != null, Value = Primitive(action["value"]),
            KeyOffset = action.Value<int?>("keyOffset") ?? 0, DelayConstraintIdentifier = action.Value<string>("delayConstraintId"), ExecutionGroup = action.Value<string>("executionGroup")
        };

        private static VaoStateConditionRecord CompileCondition(JObject condition) => new()
        {
            StateVariableIdentifier = condition.Value<string>("stateVariableId"), Operator = condition.Value<string>("operator"), Value = Primitive(condition["value"])
        };

        private static string ShortUnit(string value)
        {
            if (string.IsNullOrEmpty(value)) return "milliseconds";
            if (value.EndsWith("MilliSEC", StringComparison.OrdinalIgnoreCase)) return "milliseconds";
            if (value.EndsWith("SEC", StringComparison.OrdinalIgnoreCase)) return "seconds";
            return value;
        }

        private static void CompileSamples(JObject manifest, VaoPackageAsset package)
        {
            var variants = manifest.SelectToken("playable.sampleVariants")?.OfType<JObject>().ToDictionary(item => item.Value<string>("id"), StringComparer.Ordinal) ?? new Dictionary<string, JObject>();
            var conditionByMapping = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var render in manifest.SelectToken("interactionModel.renderBindings")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var state = render["conditions"]?.OfType<JObject>().FirstOrDefault(item => item.Value<string>("operator") == "equals" && item.Value<bool?>("value") == true)?.Value<string>("stateVariableId");
                foreach (var mapping in render["sampleMappingIds"]?.Values<string>() ?? Enumerable.Empty<string>()) conditionByMapping[mapping] = state;
            }
            foreach (var mapping in manifest.SelectToken("playable.sampleMappings")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                conditionByMapping.TryGetValue(mapping.Value<string>("id"), out var state);
                foreach (var variantId in mapping["variantIds"]?.Values<string>() ?? Enumerable.Empty<string>())
                {
                    if (!variants.TryGetValue(variantId, out var variant)) continue;
                    var realizationId = variant.Value<string>("realizationId");
                    var realization = package.FindRealization(realizationId);
                    package.SampleBindings.Add(new VaoSampleBinding
                    {
                        MappingIdentifier = mapping.Value<string>("id"), VariantIdentifier = variantId, RealizationIdentifier = realizationId,
                        RankEntityIdentifier = mapping.Value<string>("rankEntityId") ?? mapping.Value<string>("componentEntityId"), StateVariableIdentifier = state,
                        SelectionPolicy = mapping.Value<string>("selectionPolicy"), Trigger = variant.Value<string>("trigger"), SignalRole = variant.Value<string>("signalRole"),
                        RoundRobinGroup = variant.Value<string>("roundRobinGroup"), RoundRobinIndex = variant.Value<int?>("roundRobinIndex") ?? 0, SelectionWeight = variant.Value<float?>("selectionWeight") ?? 1f,
                        MinimumKey = mapping.SelectToken("keyRange.minimum")?.Value<int>() ?? 0, MaximumKey = mapping.SelectToken("keyRange.maximum")?.Value<int>() ?? 127,
                        MinimumVelocity = mapping.SelectToken("velocityRange.minimum")?.Value<int>() ?? 1, MaximumVelocity = mapping.SelectToken("velocityRange.maximum")?.Value<int>() ?? 127,
                        SampleRootKey = mapping.Value<int?>("sampleRootKey") ?? 60, SoundingKeyOffset = mapping.Value<int?>("soundingKeyOffset") ?? 0,
                        GainDecibels = mapping.Value<float?>("gainDB") ?? 0f, PitchTuningCents = mapping.Value<float?>("pitchTuningCents") ?? 0f,
                        NoteOffPolicy = mapping.Value<string>("noteOffPolicy"), Clip = realization?.ImportedObject as AudioClip, RuntimeUri = realization?.RuntimeUri
                    });
                }
            }
        }

        private static void CompileAnimations(JObject manifest, VaoPackageAsset package, IReadOnlyDictionary<string, VaoMidiSequenceAsset> sequencesByLogical, string root, VaoImportOptions options, VaoImportResult result)
        {
            foreach (var relation in manifest["relations"]?.OfType<JObject>().Where(item => item.Value<string>("predicate")?.EndsWith("drivesAnimation", StringComparison.Ordinal) == true) ?? Enumerable.Empty<JObject>())
            {
                var source = relation.Value<string>("subjectId");
                var animation = relation.Value<string>("objectId");
                var target = manifest["relations"]?.OfType<JObject>().FirstOrDefault(item => item.Value<string>("subjectId") == animation && item.Value<string>("predicate")?.EndsWith("targetsAnimation", StringComparison.Ordinal) == true)?.Value<string>("objectId");
                var properties = relation["properties"] as JObject;
                var pattern = properties?.Properties().FirstOrDefault(item => item.Name.EndsWith("targetPathPattern", StringComparison.Ordinal))?.Value.Value<string>() ?? "{midiNote}";
                var axisName = properties?.Properties().FirstOrDefault(item => item.Name.EndsWith("rotationAxis", StringComparison.Ordinal))?.Value.Value<string>() ?? "x";
                var axis = axisName switch { "y" => Vector3.up, "z" => Vector3.forward, _ => Vector3.right };
                var angle = properties?.Properties().FirstOrDefault(item => item.Name.EndsWith("pressedAngleDegrees", StringComparison.Ordinal))?.Value.Value<float?>() ?? -4f;
                var minimumMidiNote = properties?.Properties().FirstOrDefault(item => item.Name.EndsWith("minimumMidiNote", StringComparison.Ordinal))?.Value.Value<int?>() ?? 0;
                var maximumMidiNote = properties?.Properties().FirstOrDefault(item => item.Name.EndsWith("maximumMidiNote", StringComparison.Ordinal))?.Value.Value<int?>() ?? 127;
                JToken Property(string suffix) => properties?.Properties().FirstOrDefault(item => item.Name.EndsWith(suffix, StringComparison.Ordinal))?.Value;
                var layerOrder = Property("layerOrder")?.Value<int?>() ?? package.AnimationLinks.Count;
                var additive = Property("additive")?.Value<bool?>() ?? false;
                var weight = Mathf.Clamp01(Property("weight")?.Value<float?>() ?? 1f);
                var blendSeconds = Mathf.Max(0f, Property("blendSeconds")?.Value<float?>() ?? 0.08f);
                var playbackSpeed = Mathf.Max(0f, Property("playbackSpeed")?.Value<float?>() ?? 1f);
                var speedCurve = ParseAnimationCurve(Property("speedCurve"));
                var maskLogicalId = Property("avatarMaskLogicalAssetId")?.Value<string>();
                var mask = string.IsNullOrEmpty(maskLogicalId) ? null : package.Realizations.FirstOrDefault(item => item.LogicalAssetIdentifier == maskLogicalId && item.ImportedObject is AvatarMask)?.ImportedObject as AvatarMask;
                sequencesByLogical.TryGetValue(source, out var sequence);
                var sourceClip = package.Realizations.FirstOrDefault(item => item.LogicalAssetIdentifier == animation && item.ImportedObject is AnimationClip)?.ImportedObject as AnimationClip;
                AnimationClip generated = null;
                if (options.GenerateMidiAnimationClips && sequence != null)
                {
                    generated = VaoMidiParser.BuildAnimationClip(sequence, Sanitize(package.Title) + " MIDI Keys", pattern, axis, angle, minimumMidiNote, maximumMidiNote);
                    var path = AssetDatabase.GenerateUniqueAssetPath(root + "/Generated/" + generated.name + ".anim");
                    AssetDatabase.CreateAsset(generated, path);
                    result.ImportedAssetPaths.Add(path);
                }
                package.AnimationLinks.Add(new VaoAnimationLink
                {
                    Identifier = relation.Value<string>("id"), SourceLogicalAssetIdentifier = source, AnimationLogicalAssetIdentifier = animation, TargetLogicalAssetIdentifier = target,
                    TargetPathPattern = pattern, MinimumMidiNote = minimumMidiNote, MaximumMidiNote = maximumMidiNote, RotationAxis = axis, PressedAngleDegrees = angle,
                    LayerOrder = layerOrder, Additive = additive, Weight = weight, BlendSeconds = blendSeconds, PlaybackSpeed = playbackSpeed, SpeedCurve = speedCurve, Mask = mask,
                    SourceClip = sourceClip, GeneratedMidiClip = generated, MidiSequence = sequence
                });
            }
        }

        private static AnimationCurve ParseAnimationCurve(JToken token)
        {
            if (token is not JArray values || values.Count == 0) return AnimationCurve.Linear(0f, 1f, 1f, 1f);
            var keys = new List<Keyframe>();
            foreach (var value in values)
            {
                if (value is JObject item)
                    keys.Add(new Keyframe(item.Value<float?>("time") ?? 0f, item.Value<float?>("value") ?? 1f, item.Value<float?>("inTangent") ?? 0f, item.Value<float?>("outTangent") ?? 0f));
                else if (value is JArray pair && pair.Count >= 2)
                    keys.Add(new Keyframe(pair[0].Value<float>(), pair[1].Value<float>()));
            }
            return keys.Count == 0 ? AnimationCurve.Linear(0f, 1f, 1f, 1f) : new AnimationCurve(keys.OrderBy(item => item.time).ToArray());
        }

        private static void CompileAcoustics(JObject manifest, VaoPackageAsset package, string root, VaoImportResult result)
        {
            var responseSets = manifest.SelectToken("acoustics.responseSets")?.OfType<JObject>().ToDictionary(item => item.Value<string>("id"), StringComparer.Ordinal) ?? new Dictionary<string, JObject>();
            var configs = manifest.SelectToken("acoustics.renderConfigurations")?.OfType<JObject>().ToList() ?? new List<JObject>();
            var measurements = manifest.SelectToken("acoustics.measurements")?.OfType<JObject>().Where(item => item.Value<string>("id") != null).ToDictionary(item => item.Value<string>("id"), StringComparer.Ordinal) ?? new Dictionary<string, JObject>();
            var realizationDeclarations = manifest["realizations"]?.OfType<JObject>().Where(item => item.Value<string>("id") != null).ToDictionary(item => item.Value<string>("id"), StringComparer.Ordinal) ?? new Dictionary<string, JObject>();
            foreach (var scene in manifest.SelectToken("acoustics.audioScenes")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var config = configs.FirstOrDefault(item => item.Value<string>("sceneId") == scene.Value<string>("id"));
                var responseId = config?["inputIds"]?.Values<string>().FirstOrDefault(id => responseSets.ContainsKey(id));
                responseSets.TryGetValue(responseId ?? string.Empty, out var response);
                var logicalId = response?.Value<string>("logicalAssetId") ?? scene["mediaAssetIds"]?.Values<string>().FirstOrDefault();
                var candidateRealizations = package.Realizations.Where(item => item.LogicalAssetIdentifier == logicalId).ToList();
                var realization = candidateRealizations.FirstOrDefault();
                var status = response?.Value<string>("representationStatus");
                var compiled = new VaoAcousticSceneRecord
                {
                    Identifier = scene.Value<string>("id"), SceneEntityIdentifier = scene.Value<string>("sceneEntityId"), RepresentationType = scene.Value<string>("representationType"), CoordinateFrameIdentifier = scene.Value<string>("coordinateFrameId"),
                    RenderConfigurationIdentifier = config?.Value<string>("id"), RenderStrategy = config?.Value<string>("strategy"), ResponseSetIdentifier = responseId, ResponseRealizationIdentifier = realization?.Identifier,
                    ResponseKind = response?.Value<string>("responseKind"), InterpolationMethod = response?.SelectToken("interpolation.method")?.Value<string>() ?? (config?.Value<string>("strategy") == "response-interpolation" ? "linear" : "nearest"),
                    InterpolationDomainIdentifier = response?.SelectToken("interpolation.domain")?.Value<string>(), OutsideDomainPolicy = config?.Value<string>("outsideDomainPolicy") ?? response?.SelectToken("interpolation.outsideDomainPolicy")?.Value<string>(),
                    FallbackResponseSetIdentifier = response?.SelectToken("interpolation.fallbackResponseSetId")?.Value<string>(), ListenerMode = config?.SelectToken("listener.mode")?.Value<string>(), ReceiverIdentifier = config?.SelectToken("listener.receiverId")?.Value<string>(),
                    ListenerPoseIdentifier = config?.SelectToken("listener.poseId")?.Value<string>(), InputIdentifiers = config?["inputIds"]?.Values<string>().ToArray() ?? Array.Empty<string>(), FallbackIdentifiers = config?["fallbackIds"]?.Values<string>().ToArray() ?? Array.Empty<string>(),
                    TransitionSeconds = config?.Value<float?>("transitionSeconds") ?? 0f,
                    ImpulseResponse = candidateRealizations.Select(item => item.ImportedObject).OfType<AudioClip>().FirstOrDefault(), RuntimeUri = realization?.RuntimeUri, IsMeasured = status == "measured", IsSimulated = status is "simulated" or "hybrid"
                };
                foreach (var feature in config?["features"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                    compiled.RuntimeFeatures.Add(new VaoAcousticRuntimeFeatureRecord { Feature = feature.Value<string>("feature"), Mode = feature.Value<string>("mode"), InputIdentifiers = feature["inputIds"]?.Values<string>().ToArray() ?? Array.Empty<string>() });

                foreach (var candidate in candidateRealizations)
                {
                    realizationDeclarations.TryGetValue(candidate.Identifier, out var declaration);
                    var metadata = declaration?.SelectToken("technicalMetadata.impulseResponse") as JObject;
                    var encoding = metadata?.Value<string>("encoding");
                    var convention = metadata?.Value<string>("convention");
                    compiled.ResponseEncoding ??= encoding;
                    compiled.SofaConvention ??= convention;
                    VaoSofaAsset sofa = candidate.ImportedObject as VaoSofaAsset;
                    if (sofa == null && candidate.IsMaterialized && (encoding == "AES69-SOFA" || candidate.AssetPath?.EndsWith(".sofa", StringComparison.OrdinalIgnoreCase) == true))
                    {
                        sofa = VaoSofaImporter.Import(candidate.AssetPath, root + "/Generated", package.FindLogicalAsset(logicalId)?.Label ?? responseId, result);
                        candidate.ImportedObject = sofa;
                    }
                    compiled.Sofa ??= sofa;
                    var mappings = metadata?["measurementMappings"]?.OfType<JObject>().ToList() ?? new List<JObject>();
                    if (mappings.Count == 0)
                        foreach (var measurementId in response?["measurementIds"]?.Values<string>() ?? Enumerable.Empty<string>()) mappings.Add(new JObject { ["measurementId"] = measurementId, ["channelIndices"] = new JArray(0) });
                    foreach (var mapping in mappings)
                    {
                        var measurementId = mapping.Value<string>("measurementId");
                        measurements.TryGetValue(measurementId ?? string.Empty, out var measurement);
                        var sourcePoseId = measurement?.Value<string>("sourcePoseId");
                        var receiverPoseId = measurement?.Value<string>("receiverPoseId");
                        compiled.ResponsePoints.Add(new VaoAcousticResponsePointRecord
                        {
                            MeasurementIdentifier = measurementId, RealizationIdentifier = candidate.Identifier, SourceIdentifier = measurement?.Value<string>("sourceId"), ReceiverIdentifier = measurement?.Value<string>("receiverId"),
                            SourcePoseIdentifier = sourcePoseId, ReceiverPoseIdentifier = receiverPoseId, SourcePosition = PosePosition(package, sourcePoseId), ReceiverPosition = PosePosition(package, receiverPoseId),
                            ChannelIndices = mapping["channelIndices"]?.Values<int>().ToArray() ?? Array.Empty<int>(), SofaDataIndex = mapping.Value<int?>("dataIRIndex") ?? -1, DelaySamples = mapping.Value<float?>("delaySamples") ?? 0f,
                            ImpulseResponse = candidate.ImportedObject as AudioClip, Sofa = sofa
                        });
                    }
                }
                package.AcousticScenes.Add(compiled);
            }
        }

        private static Vector3 PosePosition(VaoPackageAsset package, string poseIdentifier)
        {
            if (string.IsNullOrEmpty(poseIdentifier)) return Vector3.zero;
            var pose = package.Poses.FirstOrDefault(item => item.Identifier == poseIdentifier);
            return pose == null ? Vector3.zero : VaoSpatialMath.PoseToUnity(package, pose).MultiplyPoint3x4(Vector3.zero);
        }

        private static void CreatePrefab(JObject manifest, VaoPackageAsset package, string root, VaoImportOptions options, VaoImportResult result)
        {
            var gameObject = new GameObject(string.IsNullOrWhiteSpace(package.Title) ? "VAO Object" : package.Title);
            try
            {
                var visuals = new GameObject("Visuals").transform;
                visuals.SetParent(gameObject.transform, false);
                var runtime = gameObject.AddComponent<VaoRuntimeObject>();
                runtime.Package = package;
                runtime.VisualRoot = visuals;
                var samples = gameObject.AddComponent<VaoSamplePlayer>(); samples.Package = package;
                var executor = gameObject.AddComponent<VaoDeterministicExecutor>(); executor.Package = package;
                var animations = gameObject.AddComponent<VaoLinkedAnimationPlayer>(); animations.Package = package; animations.TargetRoot = visuals;
                var media = gameObject.AddComponent<VaoMediaPlayer>(); media.Package = package; media.LinkedAnimations = animations;
                var placement = gameObject.AddComponent<VaoTrackedPlacement>(); placement.PlacementRoot = gameObject.transform; placement.ContentRoot = gameObject.transform;
                var midiRouter = gameObject.AddComponent<VaoMidiRouter>(); midiRouter.Package = package;
                var midi = gameObject.AddComponent<VaoMidiSequencePlayer>(); midi.SetPackage(package);
                var acoustics = gameObject.AddComponent<VaoAcousticEnvironment>();
                var materializer = gameObject.AddComponent<VaoRuntimeMaterializer>(); materializer.Package = package;
                var presentation = gameObject.AddComponent<VaoPresentationSelector>(); presentation.Package = package;
                if (options.CreateRuntimeControlSurface)
                {
                    var controls = gameObject.AddComponent<VaoRuntimeControlSurface>();
                    controls.Package = package;
                }
                if (package.AcousticScenes.Any(item => item.ImpulseResponse != null || item.Sofa != null || item.ResponsePoints.Any(point => point.ImpulseResponse != null || point.Sofa != null))) gameObject.AddComponent<VaoConvolutionRenderer>();
                acoustics.Package = package;
                var visualRoots = new Dictionary<string, Transform>(StringComparer.Ordinal);

                var subjectAnchors = new Dictionary<string, Transform>(StringComparer.Ordinal);
                foreach (var binding in package.GeometryBindings.Where(item => item.Role == "runtime-visual"))
                {
                    var logicalId = binding.LogicalAssetIdentifier;
                    var candidates = package.Realizations.Where(item => item.LogicalAssetIdentifier == logicalId).ToList();
                    var nativeRecord = candidates.FirstOrDefault(item => item.ImportedObject is GameObject);
                    var native = nativeRecord?.ImportedObject as GameObject;
                    Transform placed = null;
                    if (native != null)
                    {
                        var instance = PrefabUtility.InstantiatePrefab(native) as GameObject ?? UnityEngine.Object.Instantiate(native);
                        instance.name = native.name;
                        instance.transform.SetParent(visuals, false);
                        placed = instance.transform;
                    }
                    else
                    {
                        var glb = candidates.FirstOrDefault(item => item.MediaType == "model/gltf-binary");
                        if (glb == null) continue;
                        var holder = new GameObject(Path.GetFileNameWithoutExtension(glb.AssetPath));
                        holder.transform.SetParent(visuals, false);
                        var loader = holder.AddComponent<VaoGltfRuntimeLoader>(); loader.RealizationIdentifier = glb.Identifier; loader.RuntimeUri = glb.RuntimeUri;
                        nativeRecord = glb;
                        placed = holder.transform;
                    }
                    var pose = package.FindPoseForSubject(binding.SubjectIdentifier);
                    if (pose != null) VaoSpatialMath.Apply(placed, VaoSpatialMath.PoseToUnity(package, pose));
                    else if (nativeRecord?.MediaType == "model/obj") VaoSpatialMath.Apply(placed, VaoSpatialMath.FrameToUnity(package, nativeRecord.CoordinateFrameIdentifier));
                    var anchor = placed.gameObject.AddComponent<VaoSpatialAnchor>();
                    anchor.SubjectIdentifier = binding.SubjectIdentifier; anchor.PoseIdentifier = pose?.Identifier; anchor.CoordinateFrameIdentifier = pose?.CoordinateFrameIdentifier ?? nativeRecord?.CoordinateFrameIdentifier;
                    anchor.Role = package.Entities.Any(item => item.Identifier == binding.SubjectIdentifier && item.Kind == "instrument") ? VaoSpatialRole.Instrument : VaoSpatialRole.Geometry;
                    subjectAnchors[binding.SubjectIdentifier] = placed;
                    visualRoots[logicalId] = placed;
                }
                var anchors = new GameObject("Spatial Anchors").transform;
                anchors.SetParent(gameObject.transform, false);
                foreach (var pose in package.Poses.Where(item => !subjectAnchors.ContainsKey(item.SubjectIdentifier)))
                {
                    var holder = new GameObject(Sanitize(package.Entities.FirstOrDefault(item => item.Identifier == pose.SubjectIdentifier)?.Label ?? pose.SubjectIdentifier));
                    holder.transform.SetParent(anchors, false);
                    VaoSpatialMath.Apply(holder.transform, VaoSpatialMath.PoseToUnity(package, pose));
                    var anchor = holder.AddComponent<VaoSpatialAnchor>();
                    anchor.SubjectIdentifier = pose.SubjectIdentifier; anchor.PoseIdentifier = pose.Identifier; anchor.CoordinateFrameIdentifier = pose.CoordinateFrameIdentifier;
                    var kind = package.Entities.FirstOrDefault(item => item.Identifier == pose.SubjectIdentifier)?.Kind;
                    anchor.Role = kind == "acousticEmitter" ? VaoSpatialRole.AcousticEmitter : kind == "acousticReceiver" ? VaoSpatialRole.AcousticReceiver : kind == "instrument" ? VaoSpatialRole.Instrument : VaoSpatialRole.Other;
                    subjectAnchors[pose.SubjectIdentifier] = holder.transform;
                }
                var emitter = gameObject.GetComponentsInChildren<VaoSpatialAnchor>(true).FirstOrDefault(item => item.Role == VaoSpatialRole.AcousticEmitter)?.transform;
                var receiver = gameObject.GetComponentsInChildren<VaoSpatialAnchor>(true).FirstOrDefault(item => item.Role == VaoSpatialRole.AcousticReceiver)?.transform;
                samples.VoiceRoot = emitter;
                acoustics.EmitterAnchor = emitter;
                acoustics.ReceiverAnchor = receiver;
                foreach (var link in package.AnimationLinks)
                    if (!string.IsNullOrEmpty(link.TargetLogicalAssetIdentifier) && visualRoots.TryGetValue(link.TargetLogicalAssetIdentifier, out var animationRoot)) animations.SetTargetRoot(link.TargetLogicalAssetIdentifier, animationRoot);
                var path = AssetDatabase.GenerateUniqueAssetPath(root + "/" + Sanitize(gameObject.name) + ".prefab");
                package.Prefab = PrefabUtility.SaveAsPrefabAsset(gameObject, path);
                result.PrefabAssetPath = path;
                result.ImportedAssetPaths.Add(path);
            }
            finally { UnityEngine.Object.DestroyImmediate(gameObject); }
        }

        private static string NormalizeDestination(string parent, string title, string archiveSha)
        {
            parent = (parent ?? "Assets/VAO Imports").Replace('\\', '/').TrimEnd('/');
            if (!parent.StartsWith("Assets", StringComparison.Ordinal) || parent.Contains("..", StringComparison.Ordinal)) throw new ArgumentException("VAO import destination must be a safe path below Assets.");
            return parent + "/" + Sanitize(string.IsNullOrWhiteSpace(title) ? "VAO" : title) + "-" + archiveSha[..8];
        }

        private static string Sanitize(string value)
        {
            var invalid = Path.GetInvalidFileNameChars().ToHashSet();
            var text = new string((value ?? "VAO").Select(character => invalid.Contains(character) || character is '/' or '\\' ? '-' : character).ToArray()).Trim();
            return string.IsNullOrWhiteSpace(text) ? "VAO" : text;
        }

        private static string Absolute(string assetPath)
        {
            if (!assetPath.StartsWith("Assets", StringComparison.Ordinal)) throw new ArgumentException($"Not an Assets path: {assetPath}");
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
        }

        private static string ToAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var relative = Path.GetRelativePath(projectRoot, Path.GetFullPath(path)).Replace('\\', '/');
            return relative == "Assets" || relative.StartsWith("Assets/", StringComparison.Ordinal) ? relative : null;
        }
    }
}
