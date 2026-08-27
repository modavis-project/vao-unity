using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Modavis.Vao.Editor
{
    public sealed class VaoArchiveInspection
    {
        public string ArchivePath { get; internal set; }
        public JObject Manifest { get; internal set; }
        public JObject Carrier { get; internal set; }
        public byte[] ManifestBytes { get; internal set; }
        public byte[] CarrierBytes { get; internal set; }
        public string ArchiveSha256 { get; internal set; }
        public long VerifiedPayloadBytes { get; internal set; }
        public Dictionary<string, VaoEmbeddedRealization> EmbeddedRealizations { get; } = new(StringComparer.Ordinal);
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
        public bool IsValid => Errors.Count == 0;
        public string Identifier => Manifest?.Value<string>("id");
        public string Title => VaoJson.Localized(Manifest?["title"]);
    }

    public sealed class VaoEmbeddedRealization
    {
        public string Identifier { get; internal set; }
        public string CarrierPath { get; internal set; }
        public JObject ManifestRecord { get; internal set; }
        public ZipArchiveEntry Entry { get; internal set; }
    }

    public sealed class VaoValidationPolicy
    {
        public int MaximumEntries { get; set; } = 100000;
        public int MaximumPathSegments { get; set; } = 128;
        public int MaximumJsonDepth { get; set; } = 128;
        public long MaximumDescriptorBytes { get; set; } = 32L * 1024 * 1024;
        public long MaximumEntryBytes { get; set; } = 16L * 1024 * 1024 * 1024;
        public long MaximumTotalBytes { get; set; } = 64L * 1024 * 1024 * 1024;
        public double MaximumCompressionRatio { get; set; } = 200d;
        public bool VerifyPayloadDigests { get; set; } = true;
    }

    public static class VaoArchiveReader
    {
        public const string FormatVersion = "0.4.0";
        public const string ManifestName = "vao-manifest.json";
        public const string CarrierName = "META-INF/vao-carrier.json";
        public const string MediaType = "application/vnd.modavis.vao+zip";
        public const string Schema = "https://w3id.org/modavis/vao/0.4.0/schema/manifest.json";
        public const string Context = "https://w3id.org/modavis/vao/0.4.0/context.jsonld";
        public const string CoreProfile = "https://w3id.org/modavis/vao/profile/core/0.4.0";
        public const string DynamicProfile = "https://w3id.org/modavis/vao/profile/dynamic-delivery/0.4.0";
        public const string ScientificProfile = "https://w3id.org/modavis/vao/profile/scientific/0.4.0";
        public const string MultimodalProfile = "https://w3id.org/modavis/vao/profile/multimodal/0.4.0";
        public const string PhysicalProfile = "https://w3id.org/modavis/vao/profile/physical-instrument/0.4.0";
        public const string RuntimeProfile = "https://w3id.org/modavis/vao/profile/deterministic-runtime/0.4.0";
        public const string SpatialProfile = "https://w3id.org/modavis/vao/profile/spatial/0.4.0";
        public const string AcousticsProfile = "https://w3id.org/modavis/vao/profile/acoustics/0.4.0";
        public const string PlayableProfile = "https://w3id.org/modavis/vao/profile/playable/0.4.0";
        private const string CapabilityBase = "https://w3id.org/modavis/vao/vocab/capability/";

        private static readonly HashSet<string> RequiredRoot = new(StringComparer.Ordinal)
        {
            "$schema", "@context", "type", "formatVersion", "id", "release", "createdAt", "modifiedAt", "title",
            "conformsTo", "profiles", "materializableProfiles", "modavisBinding", "primaryEntityId", "focusEntityIds",
            "entities", "relations", "scientific", "multimodal", "physicalSystem", "runtime", "discovery", "logicalAssets",
            "realizations", "distributions", "repositoryBindings", "assetGroups", "rights", "integrity"
        };

        private static readonly HashSet<string> AllowedRoot = new(RequiredRoot, StringComparer.Ordinal)
        {
            "description", "acoustics", "playable", "interactionModel", "captureDocumentation", "extensions"
        };

        public static VaoArchiveInspection Inspect(string path, VaoValidationPolicy policy = null)
        {
            policy ??= new VaoValidationPolicy();
            var result = new VaoArchiveInspection { ArchivePath = Path.GetFullPath(path) };
            try
            {
                result.ArchiveSha256 = HashFile(result.ArchivePath, SHA256.Create());
                ValidateFirstEntry(result.ArchivePath, result.Errors);
                ValidateZipMetadata(result.ArchivePath, policy, result.Errors);
                if (result.Errors.Count > 0) return result;
                using var archive = ZipFile.OpenRead(result.ArchivePath);
                ValidateEntries(archive, policy, result);
                if (result.Errors.Count > 0) return result;
                var mimetype = ReadBounded(Find(archive, "mimetype"), 256);
                if (!mimetype.SequenceEqual(Encoding.UTF8.GetBytes(MediaType))) result.Errors.Add("The mimetype entry is not the exact VAO media type.");
                result.ManifestBytes = ReadBounded(Find(archive, ManifestName), policy.MaximumDescriptorBytes);
                var carrierBytes = ReadBounded(Find(archive, CarrierName), policy.MaximumDescriptorBytes);
                result.CarrierBytes = carrierBytes;
                result.Manifest = ParseStrict(result.ManifestBytes, ManifestName, policy.MaximumJsonDepth);
                result.Carrier = ParseStrict(carrierBytes, CarrierName, policy.MaximumJsonDepth);
                result.Errors.AddRange(VaoJsonSchemaValidator.ValidateManifest(result.Manifest));
                result.Errors.AddRange(VaoJsonSchemaValidator.ValidateCarrier(result.Carrier));
                ValidateManifest(result);
                ValidateCarrier(archive, policy, result);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or CryptographicException or UnauthorizedAccessException or OverflowException)
            {
                result.Errors.Add($"Cannot inspect VAO: {exception.Message}");
            }
            result.Errors.Sort(StringComparer.Ordinal);
            result.Warnings.Sort(StringComparer.Ordinal);
            return result;
        }

        public static void ExtractVerified(VaoArchiveInspection inspection, string destinationDirectory)
        {
            if (inspection == null || !inspection.IsValid) throw new InvalidOperationException("Only a valid, verified VAO can be extracted.");
            var root = Path.GetFullPath(destinationDirectory);
            Directory.CreateDirectory(root);
            using var archive = ZipFile.OpenRead(inspection.ArchivePath);
            foreach (var mapping in inspection.EmbeddedRealizations.Values.OrderBy(item => item.CarrierPath, StringComparer.Ordinal))
            {
                var target = Path.GetFullPath(Path.Combine(root, mapping.CarrierPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException($"Unsafe extraction path {mapping.CarrierPath}.");
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? root);
                using var input = Find(archive, mapping.CarrierPath).Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }
        }

        private static void ValidateFirstEntry(string path, ICollection<string> errors)
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[30];
            if (stream.Read(header) != header.Length || BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x04034b50)
            {
                errors.Add("The archive does not begin with a ZIP local-file header.");
                return;
            }
            var method = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[26..]);
            var nameBytes = new byte[nameLength];
            if (stream.Read(nameBytes, 0, nameBytes.Length) != nameBytes.Length || Encoding.UTF8.GetString(nameBytes) != "mimetype") errors.Add("mimetype must be the first ZIP entry.");
            if (method != 0) errors.Add("mimetype must be stored without compression.");
        }

        private static void ValidateZipMetadata(string path, VaoValidationPolicy policy, ICollection<string> errors)
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 22) { errors.Add("The carrier is not a complete ZIP archive."); return; }

            var tailLength = (int)Math.Min(stream.Length, 65557L);
            var tail = new byte[tailLength];
            stream.Position = stream.Length - tailLength;
            ReadExactly(stream, tail);
            var eocd = -1;
            for (var index = tail.Length - 22; index >= 0; index--)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index)) != 0x06054b50) continue;
                var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20));
                if (index + 22 + commentLength == tail.Length) { eocd = index; break; }
            }
            if (eocd < 0) { errors.Add("The carrier has no valid ZIP end-of-central-directory record."); return; }

            if (BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 4)) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 6)) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 8)) != BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10)))
            {
                errors.Add("Multi-disk ZIP carriers are forbidden.");
                return;
            }

            ulong entryCount = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10));
            ulong centralSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12));
            ulong centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16));
            if (entryCount == ushort.MaxValue || centralSize == uint.MaxValue || centralOffset == uint.MaxValue)
            {
                var eocdAbsolute = stream.Length - tailLength + eocd;
                if (eocdAbsolute < 20) { errors.Add("The ZIP64 locator is missing."); return; }
                stream.Position = eocdAbsolute - 20;
                var locator = new byte[20];
                ReadExactly(stream, locator);
                if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != 0x07064b50) { errors.Add("The ZIP64 locator is invalid."); return; }
                if (BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(4)) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(16)) != 1) { errors.Add("Multi-disk ZIP64 carriers are forbidden."); return; }
                var zip64Offset = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
                if (zip64Offset > (ulong)Math.Max(0L, stream.Length - 56)) { errors.Add("The ZIP64 directory offset is invalid."); return; }
                stream.Position = (long)zip64Offset;
                var zip64 = new byte[56];
                ReadExactly(stream, zip64);
                if (BinaryPrimitives.ReadUInt32LittleEndian(zip64) != 0x06064b50) { errors.Add("The ZIP64 end-of-central-directory record is invalid."); return; }
                if (BinaryPrimitives.ReadUInt32LittleEndian(zip64.AsSpan(16)) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(zip64.AsSpan(20)) != 0
                    || BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(24)) != BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(32)))
                {
                    errors.Add("Multi-disk ZIP64 carriers are forbidden.");
                    return;
                }
                entryCount = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(32));
                centralSize = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(40));
                centralOffset = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(48));
            }

            if (entryCount > (ulong)policy.MaximumEntries) { errors.Add("Archive exceeds the configured entry-count limit."); return; }
            if (centralOffset > (ulong)stream.Length || centralSize > (ulong)stream.Length - centralOffset)
            {
                errors.Add("The ZIP central directory lies outside the carrier.");
                return;
            }

            stream.Position = (long)centralOffset;
            var strictUtf8 = new UTF8Encoding(false, true);
            for (ulong index = 0; index < entryCount; index++)
            {
                var header = new byte[46];
                ReadExactly(stream, header);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50) { errors.Add("The ZIP central directory is malformed."); return; }
                var flags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
                var method = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10));
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
                var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30));
                var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
                var externalAttributes = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(38));
                var nameBytes = new byte[nameLength];
                ReadExactly(stream, nameBytes);
                var extraBytes = new byte[extraLength];
                ReadExactly(stream, extraBytes);
                string name;
                try { name = strictUtf8.GetString(nameBytes); }
                catch (DecoderFallbackException) { errors.Add("ZIP entry names must be valid UTF-8."); name = "<invalid UTF-8>"; }
                if ((flags & 0x0001) != 0 || (flags & 0x0040) != 0) errors.Add($"Encrypted ZIP entry {name} is forbidden.");
                if (method != 0 && method != 8) errors.Add($"ZIP entry {name} uses unsupported compression method {method}; only Stored and Deflate are allowed.");
                var fileType = (externalAttributes >> 16) & 0xF000;
                if (fileType != 0 && fileType != 0x4000 && fileType != 0x8000) errors.Add($"Special-file ZIP entry {name} is forbidden.");
                stream.Seek(commentLength, SeekOrigin.Current);

                var returnPosition = stream.Position;
                var localOffset = ResolveLocalHeaderOffset(header, extraBytes);
                if (localOffset > (ulong)Math.Max(0L, stream.Length - 30)) errors.Add($"ZIP entry {name} has an invalid local-header offset.");
                else
                {
                    stream.Position = (long)localOffset;
                    var local = new byte[30];
                    ReadExactly(stream, local);
                    if (BinaryPrimitives.ReadUInt32LittleEndian(local) != 0x04034b50) errors.Add($"ZIP entry {name} has no matching local-file header.");
                    else
                    {
                        var localFlags = BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(6));
                        var localMethod = BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(8));
                        var localNameLength = BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(26));
                        var localNameBytes = new byte[localNameLength];
                        ReadExactly(stream, localNameBytes);
                        string localName;
                        try { localName = strictUtf8.GetString(localNameBytes); }
                        catch (DecoderFallbackException) { localName = "<invalid UTF-8>"; }
                        if (localName != name) errors.Add($"ZIP entry {name} disagrees with its local-file header name.");
                        if (localFlags != flags) errors.Add($"ZIP entry {name} has inconsistent local and central flags.");
                        if ((localFlags & 0x0001) != 0 || (localFlags & 0x0040) != 0) errors.Add($"Encrypted local ZIP entry {name} is forbidden.");
                        if (localMethod != method || (localMethod != 0 && localMethod != 8)) errors.Add($"ZIP entry {name} has inconsistent or unsupported local compression metadata.");
                    }
                }
                stream.Position = returnPosition;
            }
            if ((ulong)stream.Position != centralOffset + centralSize) errors.Add("The ZIP central directory size is inconsistent.");
        }

        private static void ReadExactly(Stream stream, byte[] buffer)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read <= 0) throw new InvalidDataException("Unexpected end of ZIP data.");
                offset += read;
            }
        }

        private static ulong ResolveLocalHeaderOffset(byte[] centralHeader, byte[] extra)
        {
            var offset32 = BinaryPrimitives.ReadUInt32LittleEndian(centralHeader.AsSpan(42));
            if (offset32 != uint.MaxValue) return offset32;
            for (var field = 0; field + 4 <= extra.Length;)
            {
                var identifier = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(field));
                var length = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(field + 2));
                var start = field + 4;
                var end = start + length;
                if (end > extra.Length) throw new InvalidDataException("Malformed ZIP extra field.");
                if (identifier == 0x0001)
                {
                    var cursor = start;
                    if (BinaryPrimitives.ReadUInt32LittleEndian(centralHeader.AsSpan(24)) == uint.MaxValue) cursor += 8;
                    if (BinaryPrimitives.ReadUInt32LittleEndian(centralHeader.AsSpan(20)) == uint.MaxValue) cursor += 8;
                    if (cursor + 8 > end) throw new InvalidDataException("ZIP64 local-header offset is missing.");
                    return BinaryPrimitives.ReadUInt64LittleEndian(extra.AsSpan(cursor));
                }
                field = end;
            }
            throw new InvalidDataException("ZIP64 local-header offset is missing.");
        }

        private static void ValidateEntries(ZipArchive archive, VaoValidationPolicy policy, VaoArchiveInspection result)
        {
            if (archive.Entries.Count > policy.MaximumEntries) result.Errors.Add("Archive exceeds the configured entry-count limit.");
            var raw = new HashSet<string>(StringComparer.Ordinal);
            var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
            var portable = new Dictionary<string, string>(StringComparer.Ordinal);
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;
                if (!IsSafeCarrierPath(name, allowStructural: true)) result.Errors.Add($"Unsafe archive path {name}.");
                if (name.Split('/').Length > policy.MaximumPathSegments) result.Errors.Add($"Archive path {name} exceeds the configured segment-depth limit.");
                if (!raw.Add(name)) result.Errors.Add($"Duplicate archive path {name}.");
                var key = name.Normalize(NormalizationForm.FormC);
                if (normalized.TryGetValue(key, out var normalizedPrevious)) result.Errors.Add($"Archive paths {normalizedPrevious} and {name} collide after NFC normalization.");
                else normalized[key] = name;
                var portableKey = PortablePathKey(key);
                if (portable.TryGetValue(portableKey, out var portablePrevious) && portablePrevious != name) result.Errors.Add($"Archive paths {portablePrevious} and {name} collide after NFC/case-fold normalization.");
                else portable[portableKey] = name;
                if (IsSymbolicLink(entry)) result.Errors.Add($"Symbolic-link archive entry {name} is forbidden.");
                if (entry.Length > policy.MaximumEntryBytes) result.Errors.Add($"Archive entry {name} exceeds the configured size limit.");
                total = checked(total + entry.Length);
                if (entry.Length > 0 && entry.CompressedLength == 0) result.Errors.Add($"Archive entry {name} has an impossible zero-byte compressed representation.");
                if (entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > policy.MaximumCompressionRatio) result.Errors.Add($"Archive entry {name} exceeds the configured compression ratio.");
                if (name.EndsWith("/", StringComparison.Ordinal) && !name.StartsWith("payload/", StringComparison.Ordinal)) result.Errors.Add($"Unknown carrier directory {name}.");
                else if (!name.EndsWith("/", StringComparison.Ordinal) && name != "mimetype" && name != ManifestName && name != CarrierName && !name.StartsWith("payload/", StringComparison.Ordinal)) result.Errors.Add($"Unknown carrier entry {name}.");
            }
            if (total > policy.MaximumTotalBytes) result.Errors.Add("Archive exceeds the configured total expansion limit.");
            foreach (var required in new[] { "mimetype", ManifestName, CarrierName })
                if (archive.GetEntry(required) == null) result.Errors.Add($"Missing required carrier entry {required}.");
        }

        private static void ValidateManifest(VaoArchiveInspection result)
        {
            var manifest = result.Manifest;
            foreach (var name in RequiredRoot) if (manifest[name] == null) result.Errors.Add($"Manifest is missing required root property {name}.");
            foreach (var property in manifest.Properties()) if (!AllowedRoot.Contains(property.Name)) result.Errors.Add($"Manifest has unknown root property {property.Name}.");
            if (manifest.Value<string>("$schema") != Schema) result.Errors.Add("Manifest does not use the immutable VAO 0.4.0 schema IRI.");
            if (manifest.Value<string>("formatVersion") != FormatVersion) result.Errors.Add("Manifest formatVersion is not 0.4.0.");
            if (manifest.Value<string>("type") != "VirtualAcousticObject") result.Errors.Add("Manifest type is not VirtualAcousticObject.");
            if (manifest["@context"] is not JArray contexts || !contexts.Values<string>().Contains(Context, StringComparer.Ordinal)) result.Errors.Add("Manifest does not contain the immutable VAO 0.4.0 context IRI.");
            var claims = manifest["conformsTo"]?.Values<string>().ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
            var profileRecords = (manifest["profiles"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                .Concat(manifest["materializableProfiles"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>()).ToList();
            var profiles = profileRecords.Select(item => item.Value<string>("id")).Where(item => !string.IsNullOrEmpty(item)).ToHashSet(StringComparer.Ordinal);
            foreach (var required in new[] { CoreProfile, DynamicProfile })
                if (!claims.Contains(required) || !profiles.Contains(required)) result.Errors.Add($"Manifest must embed and claim {required}.");
            if (manifest["logicalAssets"] is not JArray { Count: > 0 }) result.Errors.Add("Manifest requires at least one logical asset.");
            if (manifest["realizations"] is not JArray { Count: > 0 }) result.Errors.Add("Manifest requires at least one realization.");
            ValidateIdentifiersAndReferences(manifest, result);
            ValidateProfileTruth(manifest, claims, profileRecords, result.Errors);
            ValidateRuntimeTraces(manifest, result.Errors);
            if (manifest.DescendantsAndSelf().OfType<JValue>().Any(item => item.Type == JTokenType.Float && item.Value is double number && (double.IsNaN(number) || double.IsInfinity(number)))) result.Errors.Add("Manifest contains a non-finite number.");
        }

        private static void ValidateProfileTruth(JObject manifest, HashSet<string> claims, IReadOnlyCollection<JObject> profileRecords, ICollection<string> errors)
        {
            var profiles = profileRecords.Select(item => item.Value<string>("id")).Where(item => !string.IsNullOrEmpty(item)).ToHashSet(StringComparer.Ordinal);
            var acoustics = manifest["acoustics"] as JObject;
            var interaction = manifest["interactionModel"] as JObject;
            var requirements = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [ScientificProfile] = RegistryHasItems(manifest["scientific"]),
                [MultimodalProfile] = RegistryHasItems(manifest["multimodal"]),
                [PhysicalProfile] = RegistryHasItems(manifest["physicalSystem"]),
                [PlayableProfile] = manifest["playable"] is JObject || interaction != null || manifest["captureDocumentation"] is JObject,
                [SpatialProfile] = acoustics != null && new[] { "coordinateFrames", "poses", "geometryBindings" }.Any(name => acoustics[name] is JArray { Count: > 0 })
                    || (manifest.SelectToken("multimodal.tracks")?.OfType<JObject>() ?? Enumerable.Empty<JObject>()).Any(item => item["coordinateFrameId"] != null),
                [AcousticsProfile] = acoustics != null && new[] { "materialModels", "measurements", "responseSets", "metricSets", "audioScenes", "renderConfigurations" }.Any(name => acoustics[name] is JArray { Count: > 0 }),
                [RuntimeProfile] = manifest.SelectToken("runtime.conformanceTraces") is JArray { Count: > 0 }
                    || manifest.SelectToken("runtime.randomSources") is JArray { Count: > 0 }
                    || manifest.SelectToken("runtime.renderers") is JArray { Count: > 0 }
                    || (interaction?["processModels"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>()).Any(item => item.Value<string>("processKind") == "stochastic")
            };
            foreach (var pair in requirements)
                if (pair.Value && (!claims.Contains(pair.Key) || !profiles.Contains(pair.Key))) errors.Add($"Manifest content requires embedded and claimed profile {pair.Key}.");
            if (profiles.Contains(AcousticsProfile) && !profiles.Contains(SpatialProfile)) errors.Add("The Acoustics profile requires the Spatial profile.");
            foreach (var claim in claims)
                if (!profiles.Contains(claim)) errors.Add($"Claimed profile {claim} has no embedded profile record.");

            var requiredCapabilities = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [CoreProfile] = new[] { CapabilityBase + "core-graph", CapabilityBase + "fixity" },
                [DynamicProfile] = new[] { CapabilityBase + "immutable-release", CapabilityBase + "carrier-mapping" },
                [PlayableProfile] = new[] { CapabilityBase + "interaction" },
                [ScientificProfile] = new[] { CapabilityBase + "typed-scientific-provenance" },
                [MultimodalProfile] = new[] { CapabilityBase + "multimodal-synchronization" },
                [PhysicalProfile] = new[] { CapabilityBase + "physical-system-topology" },
                [RuntimeProfile] = new[] { CapabilityBase + "deterministic-render-trace" },
                [SpatialProfile] = new[] { CapabilityBase + "spatial" }
            };
            foreach (var record in profileRecords)
            {
                var profile = record.Value<string>("id");
                if (!requiredCapabilities.TryGetValue(profile ?? string.Empty, out var required)) continue;
                var declared = record["requiredCapabilities"]?.Values<string>().ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
                foreach (var capability in required.Where(capability => !declared.Contains(capability))) errors.Add($"Profile {profile} omits mandatory capability {capability}.");
            }
            if (profileRecords.FirstOrDefault(item => item.Value<string>("id") == AcousticsProfile) is { } acousticProfile)
            {
                var acousticCapabilities = acousticProfile["requiredCapabilities"]?.Values<string>() ?? Enumerable.Empty<string>();
                var names = new HashSet<string>(new[] { "semantic-building-model", "measured-impulse-response", "simulated-impulse-response", "position-registered-acoustic-scene", "visual-acoustic-scene", "spatial-response-field", "spatial-audio-scene", "source-directivity", "room-acoustic-metrics", "building-acoustic-performance", "tracked-listener-convolution", "tracked-sources", "geometry-acoustic-rendering", "hybrid-acoustic-rendering", "learned-acoustic-field" }.Select(name => CapabilityBase + name), StringComparer.Ordinal);
                if (!acousticCapabilities.Any(names.Contains)) errors.Add("The Acoustics profile requires at least one standard acoustic capability.");
            }
            foreach (var binding in manifest.SelectToken("interactionModel.protocolBindings")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                if (binding.Value<string>("protocol") != "MIDI-2.0") continue;
                foreach (var field in new[] { "umpGroup", "functionBlock", "umpMessageType", "dataResolutionBits" }) if (binding[field] == null) errors.Add($"MIDI 2.0 binding {binding.Value<string>("id")} lacks {field}.");
                if (binding.Value<bool?>("jrTimestamp") != true) errors.Add($"MIDI 2.0 binding {binding.Value<string>("id")} must declare JR timestamp handling.");
            }
        }

        private static bool RegistryHasItems(JToken token) => token is JObject value && value.Properties().Any(property => property.Value is JArray array && array.Count > 0);

        private static void ValidateIdentifiersAndReferences(JObject manifest, VaoArchiveInspection result)
        {
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in manifest.DescendantsAndSelf().OfType<JProperty>().Where(item => item.Name == "id" && item.Value.Type == JTokenType.String))
            {
                if (IsOpaqueExtension(property)) continue;
                var id = property.Value.Value<string>();
                var location = property.Path;
                if (string.IsNullOrWhiteSpace(id) || !Uri.TryCreate(id, UriKind.Absolute, out _)) result.Errors.Add($"Invalid identifier at {location}.");
                else if (!ids.TryAdd(id, location)) result.Errors.Add($"Identifier {id} is duplicated at {ids[id]} and {location}.");
            }
            var rootId = manifest.Value<string>("id");
            if (!string.IsNullOrEmpty(rootId)) ids.TryAdd(rootId, "id");
            foreach (var property in manifest.DescendantsAndSelf().OfType<JProperty>())
            {
                if (IsOpaqueExtension(property)) continue;
                if (property.Name.EndsWith("Id", StringComparison.Ordinal) && property.Value.Type == JTokenType.String) ValidateReference(property.Value.Value<string>(), property.Path, ids, result);
                else if (property.Name.EndsWith("Ids", StringComparison.Ordinal) && property.Value is JArray array)
                    foreach (var value in array.Values<string>()) ValidateReference(value, property.Path, ids, result);
            }
        }

        private static bool IsOpaqueExtension(JProperty property)
        {
            for (var parent = property.Parent?.Parent; parent != null; parent = parent.Parent)
                if (parent is JProperty ancestor && ancestor.Name is "properties" or "extensions" or "parameterValues") return true;
            return false;
        }

        private static void ValidateReference(string value, string path, IReadOnlyDictionary<string, string> ids, VaoArchiveInspection result)
        {
            if (string.IsNullOrEmpty(value) || ids.ContainsKey(value)) return;
            if (value.StartsWith("urn:", StringComparison.Ordinal)) result.Errors.Add($"Unresolved local reference {value} at {path}.");
            else result.Warnings.Add($"External reference {value} at {path} was not dereferenced.");
        }

        private static void ValidateRuntimeTraces(JObject manifest, ICollection<string> errors)
        {
            foreach (var trace in manifest.SelectToken("runtime.conformanceTraces")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var digest = trace.SelectToken("digest.value")?.Value<string>();
                var canonical = new JObject
                {
                    ["initialState"] = trace["initialState"]?.DeepClone() ?? new JObject(),
                    ["inputEvents"] = trace["inputEvents"]?.DeepClone() ?? new JArray(),
                    ["expected"] = trace["expected"]?.DeepClone() ?? new JObject()
                };
                var actual = HashBytes(VaoJsonCanonicalizer.Canonicalize(canonical), SHA256.Create());
                if (!string.Equals(digest, actual, StringComparison.Ordinal)) errors.Add($"Runtime trace {trace.Value<string>("id")} has an invalid canonical digest.");
            }
        }

        private static void ValidateCarrier(ZipArchive archive, VaoValidationPolicy policy, VaoArchiveInspection result)
        {
            var carrier = result.Carrier;
            if (carrier.Value<string>("$schema") != "https://w3id.org/modavis/vao/0.4.0/schema/carrier.json") result.Errors.Add("Carrier uses the wrong immutable schema IRI.");
            if (carrier.Value<string>("formatVersion") != FormatVersion) result.Errors.Add("Carrier formatVersion is not 0.4.0.");
            if (carrier.Value<string>("releaseId") != result.Manifest.SelectToken("release.id")?.Value<string>()) result.Errors.Add("Carrier releaseId does not match manifest release.id.");
            if (carrier.Value<long?>("manifestByteSize") != result.ManifestBytes.LongLength) result.Errors.Add("Carrier manifestByteSize does not match exact manifest bytes.");
            if (carrier.Value<string>("manifestSHA256") != HashBytes(result.ManifestBytes, SHA256.Create())) result.Errors.Add("Carrier manifestSHA256 does not match exact manifest bytes.");
            var realizations = result.Manifest["realizations"]?.OfType<JObject>().Where(item => item["id"] != null).ToDictionary(item => item.Value<string>("id"), StringComparer.Ordinal) ?? new Dictionary<string, JObject>();
            var payloadEntries = archive.Entries.Where(item => item.FullName.StartsWith("payload/", StringComparison.Ordinal) && !item.FullName.EndsWith("/", StringComparison.Ordinal)).ToDictionary(item => item.FullName.Normalize(NormalizationForm.FormC), StringComparer.Ordinal);
            var mappedPaths = new HashSet<string>(StringComparer.Ordinal);
            var portableMappedPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mapping in carrier["embeddedRealizations"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var id = mapping.Value<string>("realizationId");
                var path = mapping.Value<string>("path");
                if (!IsSafeCarrierPath(path, allowStructural: false)) { result.Errors.Add($"Unsafe carrier mapping path {path}."); continue; }
                var normalizedPath = path.Normalize(NormalizationForm.FormC);
                if (!mappedPaths.Add(normalizedPath)) result.Errors.Add($"Carrier maps path {path} more than once.");
                if (!portableMappedPaths.Add(PortablePathKey(normalizedPath))) result.Errors.Add($"Carrier mapping path {path} collides after NFC/case-fold normalization.");
                if (result.EmbeddedRealizations.ContainsKey(id)) result.Errors.Add($"Carrier maps realization {id} more than once.");
                if (!realizations.TryGetValue(id, out var record)) { result.Errors.Add($"Carrier maps unknown realization {id}."); continue; }
                if (!payloadEntries.TryGetValue(path.Normalize(NormalizationForm.FormC), out var entry)) { result.Errors.Add($"Carrier path {path} is missing."); continue; }
                result.EmbeddedRealizations[id] = new VaoEmbeddedRealization { Identifier = id, CarrierPath = path, ManifestRecord = record, Entry = entry };
                if (entry.Length != record.Value<long>("byteSize")) result.Errors.Add($"Realization {id} byteSize does not match its embedded entry.");
                if (policy.VerifyPayloadDigests && VerifyRealization(entry, record, result.Errors)) result.VerifiedPayloadBytes += entry.Length;
            }
            if (!mappedPaths.SetEquals(payloadEntries.Keys)) result.Errors.Add("Carrier payload closure does not equal its embedded mapping.");
            var groups = result.Manifest["assetGroups"]?.OfType<JObject>().ToDictionary(item => item.Value<string>("id"), StringComparer.Ordinal) ?? new Dictionary<string, JObject>();
            foreach (var groupId in carrier["completeGroupIds"]?.Values<string>() ?? Enumerable.Empty<string>())
            {
                if (!groups.TryGetValue(groupId, out var group)) { result.Errors.Add($"Carrier declares unknown complete group {groupId}."); continue; }
                var required = new HashSet<string>(StringComparer.Ordinal);
                var visited = new HashSet<string>(StringComparer.Ordinal);
                var pending = new Stack<string>(new[] { groupId });
                while (pending.Count > 0)
                {
                    var current = pending.Pop();
                    if (!visited.Add(current) || !groups.TryGetValue(current, out var currentGroup)) continue;
                    foreach (var realizationId in currentGroup["realizationIds"]?.Values<string>() ?? Enumerable.Empty<string>()) required.Add(realizationId);
                    foreach (var dependency in currentGroup["dependsOnGroupIds"]?.Values<string>() ?? Enumerable.Empty<string>()) pending.Push(dependency);
                }
                if (!required.All(result.EmbeddedRealizations.ContainsKey)) result.Errors.Add($"Carrier complete group {groupId} is incomplete.");
            }
            var mode = carrier.Value<string>("carrierMode");
            if (mode == "bootstrap" && result.EmbeddedRealizations.Count == 0) result.Errors.Add("A bootstrap carrier must embed at least one realization.");
            if (mode == "preservation-closure")
            {
                if (!result.EmbeddedRealizations.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(realizations.Keys)) result.Errors.Add("Preservation closure does not embed every realization.");
                if (!(carrier["completeGroupIds"]?.Values<string>().ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal)).SetEquals(groups.Keys)) result.Errors.Add("Preservation closure must mark every asset group complete.");
            }
        }

        private static bool VerifyRealization(ZipArchiveEntry entry, JObject record, ICollection<string> errors)
        {
            var sha256 = SHA256.Create();
            SHA512 sha512 = null;
            var declared512 = record["contentDigests"]?.OfType<JObject>().FirstOrDefault(item => item.Value<string>("algorithm") == "sha512")?.Value<string>("value");
            if (declared512 != null) sha512 = SHA512.Create();
            var chunks = record.SelectToken("chunking.chunks")?.OfType<JObject>().OrderBy(item => item.Value<int>("index")).ToList() ?? new List<JObject>();
            var position = 0L;
            var chunkDigests = new List<byte[]>();
            using var stream = entry.Open();
            if (chunks.Count == 0)
            {
                Pump(stream, entry.Length, sha256, sha512, null);
                position = entry.Length;
            }
            else
            {
                for (var index = 0; index < chunks.Count; index++)
                {
                    var chunk = chunks[index];
                    if (chunk.Value<int>("index") != index || chunk.Value<long>("offset") != position) errors.Add($"Realization {record.Value<string>("id")} has non-contiguous chunk metadata.");
                    var algorithm = chunk.SelectToken("digest.algorithm")?.Value<string>();
                    using HashAlgorithm chunkHash = algorithm == "sha512" ? (HashAlgorithm)SHA512.Create() : SHA256.Create();
                    var length = chunk.Value<long>("length");
                    Pump(stream, length, sha256, sha512, chunkHash);
                    position += length;
                    chunkHash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    var digest = chunkHash.Hash;
                    chunkDigests.Add(digest);
                    if (Hex(digest) != chunk.SelectToken("digest.value")?.Value<string>()) errors.Add($"Realization {record.Value<string>("id")} chunk {index} fails its digest.");
                }
                if (position != entry.Length) errors.Add($"Realization {record.Value<string>("id")} chunks do not cover byteSize.");
            }
            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            sha512?.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var ok = Hex(sha256.Hash) == record.Value<string>("sha256");
            if (!ok) errors.Add($"Embedded realization {record.Value<string>("id")} fails SHA-256.");
            if (sha512 != null && Hex(sha512.Hash) != declared512) { errors.Add($"Embedded realization {record.Value<string>("id")} fails SHA-512."); ok = false; }
            var root = record.SelectToken("chunking.merkleRoot") as JObject;
            if (root != null && chunkDigests.Count > 0)
            {
                var actual = MerkleRoot(chunkDigests, root.Value<string>("algorithm"));
                if (actual != root.Value<string>("value")) { errors.Add($"Realization {record.Value<string>("id")} has an invalid Merkle root."); ok = false; }
            }
            return ok;
        }

        private static void Pump(Stream stream, long length, HashAlgorithm primary, HashAlgorithm secondary, HashAlgorithm chunk)
        {
            var buffer = new byte[1024 * 1024];
            var remaining = length;
            while (remaining > 0)
            {
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0) throw new InvalidDataException("Embedded realization ended early.");
                primary.TransformBlock(buffer, 0, read, buffer, 0);
                secondary?.TransformBlock(buffer, 0, read, buffer, 0);
                chunk?.TransformBlock(buffer, 0, read, buffer, 0);
                remaining -= read;
            }
        }

        private static string MerkleRoot(List<byte[]> digests, string algorithm)
        {
            byte[] Hash(byte[] bytes)
            {
                using HashAlgorithm hasher = algorithm == "sha512" ? SHA512.Create() : SHA256.Create();
                return hasher.ComputeHash(bytes);
            }
            var level = digests.Select(digest => Hash(new byte[] { 0 }.Concat(digest).ToArray())).ToList();
            while (level.Count > 1)
            {
                if (level.Count % 2 != 0) level.Add(level[^1]);
                var next = new List<byte[]>();
                for (var index = 0; index < level.Count; index += 2) next.Add(Hash(new byte[] { 1 }.Concat(level[index]).Concat(level[index + 1]).ToArray()));
                level = next;
            }
            return Hex(level[0]);
        }

        private static JObject ParseStrict(byte[] bytes, string label, int maximumDepth)
        {
            var utf8 = new UTF8Encoding(false, true);
            var source = utf8.GetString(bytes);
            ValidateStrictJsonDomain(source, label);
            using var text = new StringReader(source);
            using var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double, MaxDepth = maximumDepth };
            var token = JToken.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error, CommentHandling = CommentHandling.Load, LineInfoHandling = LineInfoHandling.Load });
            if (reader.Read()) throw new JsonReaderException($"{label} contains trailing content.");
            return token as JObject ?? throw new JsonReaderException($"{label} root must be an object.");
        }

        private static void ValidateStrictJsonDomain(string source, string label)
        {
            for (var index = 0; index < source.Length; index++)
            {
                var character = source[index];
                if (character == '"')
                {
                    ValidateJsonString(source, ref index, label);
                    continue;
                }
                if (character == '/') throw new JsonReaderException($"{label} contains a JSON comment, which is not permitted.");
                if (character != '-' && (character < '0' || character > '9')) continue;
                var start = index;
                if (character == '-') index++;
                while (index < source.Length && char.IsDigit(source[index])) index++;
                if (index < source.Length && source[index] == '.') { index++; while (index < source.Length && char.IsDigit(source[index])) index++; }
                if (index < source.Length && source[index] is 'e' or 'E')
                {
                    index++;
                    if (index < source.Length && source[index] is '+' or '-') index++;
                    while (index < source.Length && char.IsDigit(source[index])) index++;
                }
                var token = source.Substring(start, index - start);
                index--;
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || double.IsNaN(number) || double.IsInfinity(number))
                    throw new JsonReaderException($"{label} contains a number outside finite binary64: {token}.");
                var significand = token.Split('e', 'E')[0];
                if (number == 0d && significand.Any(value => value is >= '1' and <= '9'))
                    throw new JsonReaderException($"{label} contains a non-zero number that underflows binary64: {token}.");
                if (token.IndexOfAny(new[] { '.', 'e', 'E' }) < 0 && BigInteger.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                    && BigInteger.Abs(integer) > 9007199254740991L)
                    throw new JsonReaderException($"{label} contains an integer outside -(2^53-1)..2^53-1: {token}.");
            }
        }

        private static void ValidateJsonString(string source, ref int index, string label)
        {
            for (index++; index < source.Length; index++)
            {
                var character = source[index];
                if (character == '"') return;
                if (character == '\\')
                {
                    if (++index >= source.Length) throw new JsonReaderException($"{label} contains an unterminated escape sequence.");
                    if (source[index] != 'u') continue;
                    var scalar = ReadUnicodeEscape(source, index, label);
                    index += 4;
                    if (scalar is >= 0xD800 and <= 0xDBFF)
                    {
                        if (index + 6 >= source.Length || source[index + 1] != '\\' || source[index + 2] != 'u') throw new JsonReaderException($"{label} contains an unpaired UTF-16 high surrogate.");
                        var low = ReadUnicodeEscape(source, index + 2, label);
                        if (low is < 0xDC00 or > 0xDFFF) throw new JsonReaderException($"{label} contains an unpaired UTF-16 high surrogate.");
                        index += 6;
                    }
                    else if (scalar is >= 0xDC00 and <= 0xDFFF) throw new JsonReaderException($"{label} contains an unpaired UTF-16 low surrogate.");
                    continue;
                }
                if (character < 0x20) throw new JsonReaderException($"{label} contains an unescaped control character.");
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 >= source.Length || !char.IsLowSurrogate(source[index + 1])) throw new JsonReaderException($"{label} contains an unpaired UTF-16 high surrogate.");
                    index++;
                }
                else if (char.IsLowSurrogate(character)) throw new JsonReaderException($"{label} contains an unpaired UTF-16 low surrogate.");
            }
            throw new JsonReaderException($"{label} contains an unterminated string.");
        }

        private static int ReadUnicodeEscape(string source, int uIndex, string label)
        {
            if (uIndex + 4 >= source.Length) throw new JsonReaderException($"{label} contains an incomplete Unicode escape.");
            var value = 0;
            for (var offset = 1; offset <= 4; offset++)
            {
                var digit = source[uIndex + offset];
                var decoded = digit is >= '0' and <= '9' ? digit - '0' : digit is >= 'a' and <= 'f' ? digit - 'a' + 10 : digit is >= 'A' and <= 'F' ? digit - 'A' + 10 : -1;
                if (decoded < 0) throw new JsonReaderException($"{label} contains an invalid Unicode escape.");
                value = value * 16 + decoded;
            }
            return value;
        }

        private static byte[] ReadBounded(ZipArchiveEntry entry, long maximum)
        {
            if (entry == null) throw new InvalidDataException("Required ZIP entry is missing.");
            if (entry.Length > maximum) throw new InvalidDataException($"Entry {entry.FullName} exceeds its size limit.");
            using var stream = entry.Open();
            using var memory = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
            stream.CopyTo(memory);
            return memory.ToArray();
        }

        private static ZipArchiveEntry Find(ZipArchive archive, string name) => archive.GetEntry(name) ?? throw new InvalidDataException($"Missing ZIP entry {name}.");

        internal static bool IsSafeCarrierPath(string value, bool allowStructural)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Contains('\\') || value.StartsWith("/", StringComparison.Ordinal)
                || value.Any(character => character < 0x20 || character == 0x7f)) return false;
            var parts = value.Split('/');
            if (parts.Any(part => part.Length == 0 || part is "." or "..")) return false;
            if (allowStructural) return true;
            return value.StartsWith("payload/", StringComparison.Ordinal);
        }

        // Canonical decomposition plus invariant uppercase supplies the simple
        // Unicode fold. These mappings add compatibility/full-fold expansions. The result is
        // deliberately conservative for locale-sensitive edge cases: rejecting an
        // additional ambiguous name is safer than extracting two names as one file.
        private static string PortablePathKey(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (var character in value.Normalize(NormalizationForm.FormC).Normalize(NormalizationForm.FormD))
            {
                switch (character)
                {
                    case '\u00DF': case '\u1E9E': builder.Append("SS"); break;
                    case '\u0130': builder.Append("I\u0307"); break;
                    case '\u0149': builder.Append("\u02BCN"); break;
                    case '\u017F': builder.Append('S'); break;
                    case '\u03F4': builder.Append('\u0398'); break;
                    case '\u0587': builder.Append("\u0535\u0552"); break;
                    case '\u1E9A': builder.Append("A\u02BE"); break;
                    case '\uFB00': builder.Append("FF"); break;
                    case '\uFB01': builder.Append("FI"); break;
                    case '\uFB02': builder.Append("FL"); break;
                    case '\uFB03': builder.Append("FFI"); break;
                    case '\uFB04': builder.Append("FFL"); break;
                    case '\uFB05': case '\uFB06': builder.Append("ST"); break;
                    case '\uFB13': builder.Append("\u0544\u0546"); break;
                    case '\uFB14': builder.Append("\u0544\u0535"); break;
                    case '\uFB15': builder.Append("\u0544\u053B"); break;
                    case '\uFB16': builder.Append("\u054E\u0546"); break;
                    case '\uFB17': builder.Append("\u0544\u053D"); break;
                    default: builder.Append(character.ToString().ToUpperInvariant()); break;
                }
            }
            return builder.ToString();
        }

        private static bool IsSymbolicLink(ZipArchiveEntry entry) => ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

        private static string HashFile(string path, HashAlgorithm algorithm)
        {
            using (algorithm)
            using (var stream = File.OpenRead(path)) return Hex(algorithm.ComputeHash(stream));
        }

        private static string HashBytes(byte[] data, HashAlgorithm algorithm)
        {
            using (algorithm) return Hex(algorithm.ComputeHash(data));
        }

        private static string Hex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var value in bytes) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }

    }

    internal static class VaoJson
    {
        public static string Localized(JToken token)
        {
            if (token is not JObject value) return token?.Value<string>() ?? string.Empty;
            return value.Value<string>("en") ?? value.Properties().Select(property => property.Value.Value<string>()).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)) ?? string.Empty;
        }
    }
}
