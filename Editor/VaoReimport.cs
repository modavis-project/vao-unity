using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Modavis.Vao;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Modavis.Vao.Editor
{
    public enum VaoImportChangeKind { Added, Changed, Removed, MaterializationChanged, Unchanged }

    public sealed class VaoImportChange
    {
        public string RealizationIdentifier;
        public string MediaType;
        public VaoImportChangeKind Kind;
        public long ByteSize;
    }

    public sealed class VaoImportPreview
    {
        public VaoArchiveInspection Inspection;
        public VaoPackageAsset ExistingPackage;
        public VaoImportOptions Options;
        public List<VaoImportChange> Changes = new();
        public long MaterializedBytes;
        public int MaterializedCount;
        public bool RightsChanged;
        public bool RelationsChanged;
        public bool IsCompatible;
        public string Error;
        public int AddedCount => Changes.Count(item => item.Kind == VaoImportChangeKind.Added);
        public int ChangedCount => Changes.Count(item => item.Kind is VaoImportChangeKind.Changed or VaoImportChangeKind.MaterializationChanged);
        public int RemovedCount => Changes.Count(item => item.Kind == VaoImportChangeKind.Removed);
    }

    public static class VaoReimport
    {
        internal static Action BeforePostSyncVerificationForTests;
        private static readonly Regex GuidPattern = new(@"(?m)^guid:\s*([0-9a-f]{32})\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase) { ".asset", ".prefab", ".anim", ".controller", ".mat", ".meta", ".json", ".txt", ".md", ".unity" };

        public static string ResolveSourcePath(VaoPackageAsset package)
        {
            if (package == null) return null;
            if (!string.IsNullOrEmpty(package.SourceArchiveGuid))
            {
                var current = AssetDatabase.GUIDToAssetPath(package.SourceArchiveGuid);
                if (!string.IsNullOrEmpty(current)) return current;
            }
            return package.SourceArchivePath;
        }

        public static VaoImportOptions OptionsFrom(VaoPackageAsset package)
        {
            var saved = package?.ImportSettings;
            var options = new VaoImportOptions();
            if (saved == null) return options;
            if (Enum.TryParse(saved.MaterializationMode, out VaoMaterializationMode mode)) options.MaterializationMode = mode;
            options.SelectedAssetGroupIdentifiers = saved.SelectedAssetGroupIdentifiers?.ToList() ?? new List<string>();
            options.MaximumMaterializedBytes = saved.MaximumMaterializedBytes > 0 ? saved.MaximumMaterializedBytes : options.MaximumMaterializedBytes;
            options.CreatePrefab = saved.CreatePrefab;
            options.CreateRuntimeControlSurface = saved.CreateRuntimeControlSurface;
            options.GenerateMidiAnimationClips = saved.GenerateMidiAnimationClips;
            options.CopyGlbToStreamingAssets = saved.CopyGlbToStreamingAssets;
            options.VerifyPayloadDigests = saved.VerifyPayloadDigests;
            return options;
        }

        public static VaoImportPreview Preview(string archivePath, VaoPackageAsset existing, VaoImportOptions options = null)
        {
            options ??= OptionsFrom(existing);
            options = Clone(options);
            var preview = new VaoImportPreview { ExistingPackage = existing, Options = options };
            if (existing == null) { preview.Error = "No existing VAO package was supplied."; return preview; }
            if (string.IsNullOrWhiteSpace(archivePath) || (!archivePath.StartsWith("Assets", StringComparison.Ordinal) && !File.Exists(archivePath))) { preview.Error = "The source VAO archive cannot be found."; return preview; }
            VaoArchiveInspection inspection;
            try { inspection = VaoArchiveReader.Inspect(archivePath, new VaoValidationPolicy { VerifyPayloadDigests = options.VerifyPayloadDigests }); }
            catch (Exception exception) { preview.Error = "Source validation could not be completed: " + exception.Message; return preview; }
            preview.Inspection = inspection;
            if (!inspection.IsValid) { preview.Error = "Source validation failed:\n" + string.Join("\n", inspection.Errors); return preview; }
            if (!string.Equals(existing.Identifier, inspection.Identifier, StringComparison.Ordinal)) { preview.Error = $"The source identifies {inspection.Identifier}, not the existing package {existing.Identifier}."; return preview; }

            var selection = VaoImporter.BuildMaterializationSelection(inspection, options);
            preview.MaterializedCount = selection.Count;
            preview.MaterializedBytes = selection.Sum(id => inspection.EmbeddedRealizations[id].Entry.Length);
            if (preview.MaterializedBytes > options.MaximumMaterializedBytes)
            {
                preview.Error = $"The selected update requires {EditorUtility.FormatBytes(preview.MaterializedBytes)}, exceeding the configured {EditorUtility.FormatBytes(options.MaximumMaterializedBytes)} limit.";
                return preview;
            }
            var incoming = inspection.Manifest["realizations"]?.OfType<JObject>().Where(item => item.Value<string>("id") != null).ToDictionary(item => item.Value<string>("id"), StringComparer.Ordinal) ?? new Dictionary<string, JObject>();
            var existingById = existing.Realizations.Where(item => !string.IsNullOrEmpty(item.Identifier)).ToDictionary(item => item.Identifier, StringComparer.Ordinal);
            foreach (var pair in incoming.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                var desiredMaterialized = selection.Contains(pair.Key);
                if (!existingById.TryGetValue(pair.Key, out var old))
                {
                    preview.Changes.Add(new VaoImportChange { RealizationIdentifier = pair.Key, MediaType = pair.Value.Value<string>("mediaType"), ByteSize = pair.Value.Value<long>("byteSize"), Kind = VaoImportChangeKind.Added });
                    continue;
                }
                var changed = old.Sha256 != pair.Value.Value<string>("sha256") || old.MediaType != pair.Value.Value<string>("mediaType") || old.ByteSize != pair.Value.Value<long>("byteSize");
                preview.Changes.Add(new VaoImportChange
                {
                    RealizationIdentifier = pair.Key, MediaType = pair.Value.Value<string>("mediaType"), ByteSize = pair.Value.Value<long>("byteSize"),
                    Kind = changed ? VaoImportChangeKind.Changed : old.IsMaterialized != desiredMaterialized ? VaoImportChangeKind.MaterializationChanged : VaoImportChangeKind.Unchanged
                });
            }
            foreach (var old in existing.Realizations.Where(item => !incoming.ContainsKey(item.Identifier)))
                preview.Changes.Add(new VaoImportChange { RealizationIdentifier = old.Identifier, MediaType = old.MediaType, ByteSize = old.ByteSize, Kind = VaoImportChangeKind.Removed });

            try
            {
                var oldManifest = JObject.Parse(existing.RawManifestJson);
                preview.RightsChanged = !JToken.DeepEquals(oldManifest["rights"], inspection.Manifest["rights"]);
                preview.RelationsChanged = !JToken.DeepEquals(oldManifest["relations"], inspection.Manifest["relations"]);
            }
            catch { preview.RightsChanged = preview.RelationsChanged = true; }
            preview.IsCompatible = true;
            return preview;
        }

        public static VaoImportResult Apply(VaoImportPreview preview)
        {
            if (preview?.IsCompatible != true || preview.ExistingPackage == null || preview.Inspection == null) throw new InvalidOperationException(preview?.Error ?? "The reimport preview is not compatible.");
            var existingPath = AssetDatabase.GetAssetPath(preview.ExistingPackage);
            if (string.IsNullOrEmpty(existingPath) || !existingPath.StartsWith("Assets/", StringComparison.Ordinal)) throw new InvalidOperationException("The existing package must be a saved asset below Assets.");
            var existingPrefabPath = preview.ExistingPackage.Prefab != null ? AssetDatabase.GetAssetPath(preview.ExistingPackage.Prefab) : null;
            var targetRoot = Path.GetDirectoryName(existingPath)?.Replace('\\', '/') ?? throw new InvalidOperationException("The existing package has no import root.");
            var token = Guid.NewGuid().ToString("N");
            // Unity's AssetDatabase ignores dot-prefixed folders, so staging must use a normal Assets path.
            var stagingParent = "Assets/VAO_Reimport_Staging_" + token;
            var temporaryBase = Path.Combine(Path.GetTempPath(), "vao-unity-reimport-" + token);
            var backup = Path.Combine(temporaryBase, "backup");
            var prepared = Path.Combine(temporaryBase, "prepared");
            Directory.CreateDirectory(temporaryBase);
            VaoImportResult staged = null;
            var stagingDeleted = false;
            var preserveTemporary = false;
            try
            {
                var stagedOptions = Clone(preview.Options);
                stagedOptions.DestinationAssetPath = stagingParent;
                staged = VaoImporter.Import(preview.Inspection.ArchivePath, stagedOptions);
                if (staged.Inspection.ArchiveSha256 != preview.Inspection.ArchiveSha256 || staged.Inspection.Identifier != preview.Inspection.Identifier)
                    throw new InvalidDataException("The source archive changed after the preview. Build a new verified change preview before applying it.");
                var stageRoot = Path.GetDirectoryName(staged.PackageAssetPath)?.Replace('\\', '/') ?? throw new InvalidOperationException("Staged import has no root.");
                CopyDirectory(Absolute(targetRoot), backup);
                Prepare(stageRoot, targetRoot, existingPath, existingPrefabPath, staged, prepared);
                // Prepared files are self-contained. Remove their AssetDatabase originals before copying so newly added assets never coexist with duplicate GUIDs.
                DeleteAssetTree(stagingParent);
                stagingDeleted = true;
                Synchronize(targetRoot, ManagedPaths(preview.ExistingPackage, targetRoot), prepared);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(existingPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                BeforePostSyncVerificationForTests?.Invoke();

                var updated = AssetDatabase.LoadAssetAtPath<VaoPackageAsset>(existingPath);
                if (updated == null || updated.Identifier != preview.Inspection.Identifier || updated.SourceArchiveSha256 != preview.Inspection.ArchiveSha256)
                    throw new InvalidDataException($"Transactional reimport verification failed after synchronization (asset: {updated != null}, identifier: {updated?.Identifier ?? "missing"}, archive SHA-256: {updated?.SourceArchiveSha256 ?? "missing"}).");
                updated.ImportSettings.ManagedRelativePaths = Directory.EnumerateFiles(prepared, "*", SearchOption.AllDirectories)
                    .Where(path => !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    .Select(path => Relative(prepared, path)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
                if (preview.Options.CreatePrefab && !string.IsNullOrEmpty(existingPrefabPath))
                {
                    AssetDatabase.ImportAsset(existingPrefabPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    updated.Prefab = AssetDatabase.LoadAssetAtPath<GameObject>(existingPrefabPath);
                    if (updated.Prefab == null) throw new InvalidDataException("Transactional reimport could not load the synchronized prefab.");
                }
                EditorUtility.SetDirty(updated);
                AssetDatabase.SaveAssets();

                // Force a final reload after staging removal so no object reference can remain bound to staged assets.
                Selection.activeObject = updated;
                var finalPrefabPath = updated.Prefab != null ? AssetDatabase.GetAssetPath(updated.Prefab) : existingPrefabPath ?? targetRoot + "/" + Path.GetFileName(staged.PrefabAssetPath ?? string.Empty);
                if (!string.IsNullOrEmpty(finalPrefabPath) && File.Exists(Absolute(finalPrefabPath))) AssetDatabase.ImportAsset(finalPrefabPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                AssetDatabase.ImportAsset(existingPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                updated = AssetDatabase.LoadAssetAtPath<VaoPackageAsset>(existingPath);
                if (updated == null || preview.Options.CreatePrefab && updated.Prefab == null) throw new InvalidDataException("Transactional reimport produced an unresolved final prefab reference.");

                staged.Package = updated;
                staged.PackageAssetPath = existingPath;
                staged.PrefabAssetPath = updated.Prefab != null ? AssetDatabase.GetAssetPath(updated.Prefab) : null;
                staged.ImportedAssetPaths.Clear();
                staged.ImportedAssetPaths.AddRange(updated.ImportSettings.ManagedRelativePaths.Select(path => targetRoot + "/" + path));
                Selection.activeObject = updated;
                return staged;
            }
            catch (Exception original)
            {
                if (Directory.Exists(backup))
                {
                    try
                    {
                        var target = Absolute(targetRoot);
                        if (Directory.Exists(target)) Directory.Delete(target, true);
                        CopyDirectory(backup, target);
                        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                        AssetDatabase.ImportAsset(existingPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    }
                    catch (Exception rollback)
                    {
                        preserveTemporary = true;
                        throw new AggregateException($"VAO reimport failed and the automatic rollback also failed. Recovery data remains at {temporaryBase}.", original, rollback);
                    }
                }
                throw;
            }
            finally
            {
                if (!stagingDeleted) try { DeleteAssetTree(stagingParent); } catch (Exception cleanup) { Debug.LogWarning("Could not remove VAO reimport staging: " + cleanup.Message); }
                if (!preserveTemporary && Directory.Exists(temporaryBase) && temporaryBase.StartsWith(Path.GetTempPath(), StringComparison.Ordinal)) try { Directory.Delete(temporaryBase, true); } catch (Exception cleanup) { Debug.LogWarning("Could not remove VAO reimport temporary backup: " + cleanup.Message); }
                EditorUtility.ClearProgressBar();
            }
        }

        private static void Prepare(string stageRoot, string targetRoot, string targetPackagePath, string targetPrefabPath, VaoImportResult staged, string prepared)
        {
            Directory.CreateDirectory(prepared);
            var stageAbsolute = Absolute(stageRoot);
            var targetAbsolute = Absolute(targetRoot);
            var stagePackageRelative = Relative(stageAbsolute, Absolute(staged.PackageAssetPath));
            var targetPackageRelative = Relative(targetAbsolute, Absolute(targetPackagePath));
            var stagePrefabRelative = string.IsNullOrEmpty(staged.PrefabAssetPath) ? null : Relative(stageAbsolute, Absolute(staged.PrefabAssetPath));
            var targetPrefabRelative = string.IsNullOrEmpty(targetPrefabPath) ? stagePrefabRelative : Relative(targetAbsolute, Absolute(targetPrefabPath));
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal) { [stagePackageRelative] = targetPackageRelative };
            if (!string.IsNullOrEmpty(stagePrefabRelative) && !string.IsNullOrEmpty(targetPrefabRelative)) aliases[stagePrefabRelative] = targetPrefabRelative;
            var prefabFileIds = !string.IsNullOrEmpty(staged.PrefabAssetPath) && !string.IsNullOrEmpty(targetPrefabPath)
                ? BuildPrefabFileIdMap(staged.PrefabAssetPath, targetPrefabPath)
                : new Dictionary<ulong, ulong>();

            var guidMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var meta in Directory.EnumerateFiles(stageAbsolute, "*.meta", SearchOption.AllDirectories))
            {
                var sourceRelative = Relative(stageAbsolute, meta);
                var assetRelative = sourceRelative[..^5];
                var destinationAssetRelative = aliases.TryGetValue(assetRelative, out var alias) ? alias : assetRelative;
                var targetMeta = Path.Combine(targetAbsolute, destinationAssetRelative.Replace('/', Path.DirectorySeparatorChar) + ".meta");
                if (!File.Exists(targetMeta)) continue;
                var stagedGuid = ReadGuid(meta);
                var targetGuid = ReadGuid(targetMeta);
                if (!string.IsNullOrEmpty(stagedGuid) && !string.IsNullOrEmpty(targetGuid) && stagedGuid != targetGuid) guidMap[stagedGuid] = targetGuid;
            }

            foreach (var source in Directory.EnumerateFiles(stageAbsolute, "*", SearchOption.AllDirectories))
            {
                var relative = Relative(stageAbsolute, source);
                var isMeta = relative.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
                var assetRelative = isMeta ? relative[..^5] : relative;
                var destinationAssetRelative = aliases.TryGetValue(assetRelative, out var alias) ? alias : assetRelative;
                var destinationRelative = destinationAssetRelative + (isMeta ? ".meta" : string.Empty);
                var destination = Path.Combine(prepared, destinationRelative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? prepared);
                File.Copy(source, destination, true);
            }

            if (!string.IsNullOrEmpty(targetPrefabRelative) && prefabFileIds.Count > 0)
            {
                var preparedPrefab = Path.Combine(prepared, targetPrefabRelative.Replace('/', Path.DirectorySeparatorChar));
                RewritePrefabFileIds(preparedPrefab, prefabFileIds);
            }

            foreach (var meta in Directory.EnumerateFiles(prepared, "*.meta", SearchOption.AllDirectories))
            {
                var relative = Relative(prepared, meta);
                var targetMeta = Path.Combine(targetAbsolute, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(targetMeta)) File.Copy(targetMeta, meta, true);
            }
            foreach (var path in Directory.EnumerateFiles(prepared, "*", SearchOption.AllDirectories).Where(path => TextExtensions.Contains(Path.GetExtension(path))))
            {
                string text;
                try { text = File.ReadAllText(path); } catch { continue; }
                var changed = false;
                foreach (var pair in guidMap)
                {
                    if (!text.Contains(pair.Key, StringComparison.Ordinal)) continue;
                    text = text.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
                    changed = true;
                }
                if (changed) File.WriteAllText(path, text);
            }
        }

        private static Dictionary<ulong, ulong> BuildPrefabFileIdMap(string stagedPath, string targetPath)
        {
            var stagedRoot = AssetDatabase.LoadAssetAtPath<GameObject>(stagedPath);
            var targetRoot = AssetDatabase.LoadAssetAtPath<GameObject>(targetPath);
            var result = new Dictionary<ulong, ulong>();
            if (stagedRoot == null || targetRoot == null) return result;
            var stagedObjects = PrefabObjectsBySemanticKey(stagedRoot);
            var targetObjects = PrefabObjectsBySemanticKey(targetRoot);
            foreach (var pair in stagedObjects)
            {
                if (!targetObjects.TryGetValue(pair.Key, out var target)) continue;
                var stagedId = GlobalObjectId.GetGlobalObjectIdSlow(pair.Value).targetObjectId;
                var targetId = GlobalObjectId.GetGlobalObjectIdSlow(target).targetObjectId;
                if (stagedId != 0 && targetId != 0 && stagedId != targetId) result[stagedId] = targetId;
            }
            return result;
        }

        private static Dictionary<string, UnityEngine.Object> PrefabObjectsBySemanticKey(GameObject root)
        {
            var result = new Dictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                var path = SemanticTransformPath(root.transform, transform);
                result["game-object:" + path] = transform.gameObject;
                var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var component in transform.GetComponents<Component>().Where(item => item != null))
                {
                    var type = component.GetType().AssemblyQualifiedName ?? component.GetType().FullName;
                    ordinals.TryGetValue(type, out var ordinal);
                    ordinals[type] = ordinal + 1;
                    result[$"component:{path}:{type}:{ordinal}"] = component;
                }
            }
            return result;
        }

        private static string SemanticTransformPath(Transform root, Transform value)
        {
            if (value == root) return "$root";
            var segments = new Stack<string>();
            for (var current = value; current != null && current != root; current = current.parent)
            {
                var sameNameOrdinal = 0;
                for (var index = 0; index < current.GetSiblingIndex(); index++) if (current.parent.GetChild(index).name == current.name) sameNameOrdinal++;
                segments.Push(current.name.Replace("/", "%2F") + "[" + sameNameOrdinal + "]");
            }
            return string.Join("/", segments);
        }

        private static void RewritePrefabFileIds(string path, IReadOnlyDictionary<ulong, ulong> fileIds)
        {
            if (!File.Exists(path)) return;
            var text = File.ReadAllText(path);
            var replacements = fileIds.ToDictionary(item => item.Key.ToString(), item => item.Value.ToString(), StringComparer.Ordinal);
            text = Regex.Replace(text, @"(?m)(^--- !u!\d+ &)(\d+)(\s*$)", match => replacements.TryGetValue(match.Groups[2].Value, out var replacement) ? match.Groups[1].Value + replacement + match.Groups[3].Value : match.Value, RegexOptions.CultureInvariant);
            text = Regex.Replace(text, @"(?<=fileID: )(\d+)(?=\s*[,}])", match => replacements.TryGetValue(match.Groups[1].Value, out var replacement) ? replacement : match.Value, RegexOptions.CultureInvariant);
            File.WriteAllText(path, text);
        }

        private static void Synchronize(string targetRoot, IEnumerable<string> oldManaged, string prepared)
        {
            var target = Absolute(targetRoot);
            var incoming = Directory.EnumerateFiles(prepared, "*", SearchOption.AllDirectories).Select(path => Relative(prepared, path)).ToHashSet(StringComparer.Ordinal);
            foreach (var relative in oldManaged ?? Enumerable.Empty<string>())
            {
                if (incoming.Contains(relative)) continue;
                var path = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".meta")) File.Delete(path + ".meta");
            }
            foreach (var source in Directory.EnumerateFiles(prepared, "*", SearchOption.AllDirectories))
            {
                var relative = Relative(prepared, source);
                var destination = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? target);
                File.Copy(source, destination, true);
            }
        }

        private static IEnumerable<string> ManagedPaths(VaoPackageAsset package, string root)
        {
            if (package.ImportSettings.ManagedRelativePaths is { Length: > 0 }) return package.ImportSettings.ManagedRelativePaths;
            var absoluteRoot = Absolute(root);
            var known = new HashSet<string>(StringComparer.Ordinal);
            void AddAsset(UnityEngine.Object value)
            {
                var path = value != null ? AssetDatabase.GetAssetPath(value) : null;
                if (!string.IsNullOrEmpty(path) && path.StartsWith(root + "/", StringComparison.Ordinal)) known.Add(Relative(absoluteRoot, Absolute(path)));
            }
            AddAsset(package); AddAsset(package.Prefab);
            foreach (var item in package.Realizations) AddAsset(item.ImportedObject);
            foreach (var item in package.MidiSequences) AddAsset(item);
            foreach (var item in package.AnimationLinks) { AddAsset(item.SourceClip); AddAsset(item.GeneratedMidiClip); }
            foreach (var folder in new[] { "Source", "Payload", "Generated" })
            {
                var path = Path.Combine(absoluteRoot, folder);
                if (Directory.Exists(path)) foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Where(item => !item.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))) known.Add(Relative(absoluteRoot, file));
            }
            return known;
        }

        private static VaoImportOptions Clone(VaoImportOptions value) => new()
        {
            DestinationAssetPath = value.DestinationAssetPath, CreatePrefab = value.CreatePrefab, CreateRuntimeControlSurface = value.CreateRuntimeControlSurface, GenerateMidiAnimationClips = value.GenerateMidiAnimationClips,
            CopyGlbToStreamingAssets = value.CopyGlbToStreamingAssets, VerifyPayloadDigests = value.VerifyPayloadDigests, MaterializationMode = value.MaterializationMode,
            SelectedAssetGroupIdentifiers = value.SelectedAssetGroupIdentifiers?.ToList() ?? new List<string>(), MaximumMaterializedBytes = value.MaximumMaterializedBytes
        };

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(destination, Relative(source, directory).Replace('/', Path.DirectorySeparatorChar)));
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Relative(source, file).Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destination);
                File.Copy(file, target, true);
            }
        }

        private static string ReadGuid(string meta)
        {
            var match = GuidPattern.Match(File.ReadAllText(meta));
            return match.Success ? match.Groups[1].Value : null;
        }

        private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
        private static string Absolute(string assetPath) => Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));

        private static void DeleteAssetTree(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Assets/VAO_Reimport_Staging_", StringComparison.Ordinal)) return;
            var absolute = Absolute(assetPath);
            if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
            if (File.Exists(absolute + ".meta")) File.Delete(absolute + ".meta");
            AssetDatabase.Refresh();
        }
    }
}
