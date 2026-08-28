using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Modavis.Vao;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Modavis.Vao.Editor.Tests
{
    public sealed class VaoArchiveReaderTests
    {
        private string temporaryDirectory;

        [SetUp]
        public void SetUp()
        {
            temporaryDirectory = Path.Combine(Path.GetTempPath(), "vao-unity-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            VaoReimport.BeforePostSyncVerificationForTests = null;
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true);
        }

        [Test]
        public void ValidPreservationClosurePassesExactByteValidation()
        {
            var path = BuildArchive();
            var result = VaoArchiveReader.Inspect(path);
            Assert.That(result.IsValid, Is.True, string.Join("; ", result.Errors));
            Assert.That(result.Identifier, Is.EqualTo("urn:vao:test:unity:minimal"));
            Assert.That(result.VerifiedPayloadBytes, Is.EqualTo(Encoding.UTF8.GetByteCount("vao-test\n")));
            Assert.That(result.EmbeddedRealizations.Count, Is.EqualTo(1));
        }

        [Test]
        public void PublishedVaoStandardMinimalCarrierImportsWithFinalReceiptContract()
        {
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(VaoArchiveReader).Assembly);
            var archive = Path.Combine(packageInfo.resolvedPath, "Tests", "Editor", "Fixtures", "VAO-Standard-Minimal-0.4.0.vao");
            var inspection = VaoArchiveReader.Inspect(archive);
            Assert.That(inspection.IsValid, Is.True, string.Join("; ", inspection.Errors));
            Assert.That(inspection.ArchiveSha256, Is.EqualTo("1cb8e10c3da1013aacf0e310bfcf60a34959c99ad20e01ece64e3687fa8fe336"));

            var destination = "Assets/QA/Final Standard " + Guid.NewGuid().ToString("N");
            try
            {
                var imported = VaoImporter.Import(archive, new VaoImportOptions { DestinationAssetPath = destination, CreatePrefab = false });
                var receiptAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(imported.MaterializationReceiptPath);
                Assert.That(receiptAsset, Is.Not.Null);
                using var receiptReader = new JsonTextReader(new StringReader(receiptAsset.text)) { DateParseHandling = DateParseHandling.None };
                var receipt = JObject.Load(receiptReader);
                Assert.That(receipt.SelectToken("implementation.identityScope")?.Value<string>(), Is.EqualTo("source-file"));
                Assert.That(receipt.SelectToken("sourceCarrier.kind")?.Value<string>(), Is.EqualTo("packed-carrier"));
                Assert.That(receipt.SelectToken("sourceCarrier.packedCarrierSHA256")?.Value<string>(), Is.EqualTo(inspection.ArchiveSha256));
                Assert.That(receipt["acquisitions"], Is.Empty, "Embedded bytes are carrier evidence, not repository acquisitions.");
                Assert.That(VaoJsonSchemaValidator.ValidateMaterializationReceipt(receipt), Is.Empty);
            }
            finally
            {
                AssetDatabase.DeleteAsset(destination);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
        }

        [Test]
        public void PinnedVaoStandard05MinimalCarrierImportsWithMatchingReceiptContract()
        {
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(VaoArchiveReader).Assembly);
            var archive = Path.Combine(packageInfo.resolvedPath, "Tests", "Editor", "Fixtures", "VAO-Standard-Minimal-0.5.0.vao");
            var inspection = VaoArchiveReader.Inspect(archive);
            Assert.That(inspection.IsValid, Is.True, string.Join("; ", inspection.Errors));
            Assert.That(inspection.FormatVersion, Is.EqualTo("0.5.0"));
            Assert.That(inspection.CarrierIdentifier, Is.EqualTo("urn:uuid:03000000-0000-4000-8000-000000000040"));
            Assert.That(inspection.ArchiveSha256, Is.EqualTo("9bc7ff7eb06cd50a66ab5bfeabdecaef68c8b24a15f5b47bc0013811a241403e"));

            var destination = "Assets/QA/Pinned Standard 05 " + Guid.NewGuid().ToString("N");
            try
            {
                var imported = VaoImporter.Import(archive, new VaoImportOptions { DestinationAssetPath = destination, CreatePrefab = false });
                var receiptAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(imported.MaterializationReceiptPath);
                Assert.That(receiptAsset, Is.Not.Null);
                using var receiptReader = new JsonTextReader(new StringReader(receiptAsset.text)) { DateParseHandling = DateParseHandling.None };
                var receipt = JObject.Load(receiptReader);
                Assert.That(receipt.Value<string>("formatVersion"), Is.EqualTo("0.5.0"));
                Assert.That(receipt.Value<string>("$schema"), Is.EqualTo("https://w3id.org/modavis/vao/0.5.0/schema/materialization-receipt.json"));
                Assert.That(VaoJsonSchemaValidator.ValidateMaterializationReceipt(receipt), Is.Empty);
            }
            finally
            {
                AssetDatabase.DeleteAsset(destination);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
        }

        [Test]
        public void Rfc8785CanonicalizationMatchesThePublishedRuntimeTrace()
        {
            var tuple = new JObject
            {
                ["expected"] = new JObject
                {
                    ["state"] = new JObject
                    {
                        ["urn:vao:fixture:kinoorgel:state:tibia-enabled"] = true,
                        ["urn:vao:fixture:kinoorgel:state:shutter-stage"] = 17
                    },
                    ["emittedEvents"] = new JArray(), ["renderBindingIds"] = new JArray()
                },
                ["inputEvents"] = new JArray(new JObject
                {
                    ["value"] = true, ["timestamp"] = 0, ["sequence"] = 0, ["priority"] = 0,
                    ["eventTypeId"] = "urn:vao:fixture:kinoorgel:event:control-on", ["controlId"] = "urn:vao:fixture:kinoorgel:control:tibia-stop"
                }),
                ["initialState"] = new JObject()
            };
            using var hash = SHA256.Create();
            Assert.That(Hex(hash.ComputeHash(VaoJsonCanonicalizer.Canonicalize(tuple))), Is.EqualTo("32fb66e02d08b16b3039d34934146f24d3b69e6f2bec0b0cf60e4ea8c8818f4f"));
        }

        [TestCase("VAO-Standard-Cuntz-Positiv-0.4.0.json", "f494397d2c297a59b61f5a09b42b79e641d697ed820014a46f64968e429f5ea1")]
        [TestCase("VAO-Standard-Kinoorgel-0.4.0.json", "597e6d4d4055e765b94c269f847054cee43cba79b6090f104f4c075653d93add")]
        public void PublishedVaoStandardProfileDescriptorsPassTheVendoredFinalSchema(string fileName, string expectedSha256)
        {
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(VaoArchiveReader).Assembly);
            var path = Path.Combine(packageInfo.resolvedPath, "Tests", "Editor", "Fixtures", fileName);
            var bytes = File.ReadAllBytes(path);
            using (var hash = SHA256.Create()) Assert.That(Hex(hash.ComputeHash(bytes)), Is.EqualTo(expectedSha256));
            using var text = new StringReader(Encoding.UTF8.GetString(bytes));
            using var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None };
            var manifest = JObject.Load(reader);
            Assert.That(VaoJsonSchemaValidator.ValidateManifest(manifest), Is.Empty);
        }

        [Test]
        public void PayloadTamperingIsRejected()
        {
            var result = VaoArchiveReader.Inspect(BuildArchive(corruptDigest: true));
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors.Any(item => item.Contains("fails SHA-256")), Is.True, string.Join("; ", result.Errors));
        }

        [Test]
        public void TraversalPathIsRejectedBeforeExtraction()
        {
            var result = VaoArchiveReader.Inspect(BuildArchive(unsafePath: true));
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors.Any(item => item.Contains("Unsafe")), Is.True, string.Join("; ", result.Errors));
        }

        [Test]
        public void UnsupportedZipCompressionIsRejectedBeforeOpeningEntries()
        {
            var path = BuildArchive();
            PatchFirstCentralHeader(path, bytes => { bytes[10] = 12; bytes[11] = 0; });
            var result = VaoArchiveReader.Inspect(path);
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors.Any(item => item.Contains("unsupported compression method 12")), Is.True, string.Join("; ", result.Errors));
        }

        [Test]
        public void EncryptedZipEntryIsRejectedBeforeOpeningEntries()
        {
            var path = BuildArchive();
            PatchFirstCentralHeader(path, bytes => bytes[8] |= 1);
            var result = VaoArchiveReader.Inspect(path);
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors.Any(item => item.Contains("Encrypted ZIP entry")), Is.True, string.Join("; ", result.Errors));
        }

        [Test]
        public void EncryptedLocalZipHeaderIsRejectedEvenWhenCentralMetadataIsClear()
        {
            var path = BuildArchive();
            var bytes = File.ReadAllBytes(path);
            bytes[6] |= 1;
            File.WriteAllBytes(path, bytes);
            var result = VaoArchiveReader.Inspect(path);
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors.Any(item => item.Contains("Encrypted local ZIP entry")), Is.True, string.Join("; ", result.Errors));
        }

        [Test]
        public void SpecialFileZipEntryIsRejectedBeforeOpeningEntries()
        {
            var path = BuildArchive();
            PatchFirstCentralHeader(path, bytes => { bytes[40] = 0; bytes[41] = 0xA0; });
            var result = VaoArchiveReader.Inspect(path);
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors.Any(item => item.Contains("Special-file ZIP entry")), Is.True, string.Join("; ", result.Errors));
        }

        [Test]
        public void FullUnicodeCaseFoldPathCollisionIsRejected()
        {
            var result = VaoArchiveReader.Inspect(BuildArchive(payloadPathOverride: "payload/stra\u00DFe.bin", additionalPayloadPath: "payload/strasse.bin"));
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors.Any(item => item.Contains("case-fold normalization")), Is.True, string.Join("; ", result.Errors));
        }

        [Test]
        public void NormativeSchemaRejectsUnknownNestedProperty()
        {
            var result = VaoArchiveReader.Inspect(BuildArchive(invalidSchema: true));
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors.Any(item => item.Contains("$.release.unexpected") && item.Contains("not an allowed property")), Is.True, string.Join("; ", result.Errors));
        }

        [Test]
        public void MaterializationModesSelectExactCarrierRealizations()
        {
            var inspection = VaoArchiveReader.Inspect(BuildArchive());
            Assert.That(inspection.IsValid, Is.True, string.Join("; ", inspection.Errors));
            var metadata = VaoImporter.BuildMaterializationSelection(inspection, new VaoImportOptions { MaterializationMode = VaoMaterializationMode.MetadataOnly });
            var primary = (JObject)inspection.Manifest["assetGroups"][0];
            primary["realizationIds"] = new JArray();
            primary["dependsOnGroupIds"] = new JArray("urn:vao:test:unity:minimal:dependency");
            ((JArray)inspection.Manifest["assetGroups"]).Add(new JObject
            {
                ["id"] = "urn:vao:test:unity:minimal:dependency",
                ["realizationIds"] = new JArray("urn:vao:test:unity:minimal:realization"),
                ["dependsOnGroupIds"] = new JArray("urn:vao:test:unity:minimal:group")
            });
            var options = new VaoImportOptions { MaterializationMode = VaoMaterializationMode.SelectedAssetGroups, SelectedAssetGroupIdentifiers = { "urn:vao:test:unity:minimal:group" } };
            var groups = VaoImporter.BuildMaterializationGroupSelection(inspection, options);
            var selected = VaoImporter.BuildMaterializationSelection(inspection, options);
            Assert.That(metadata, Is.Empty);
            Assert.That(groups, Is.EquivalentTo(new[] { "urn:vao:test:unity:minimal:group", "urn:vao:test:unity:minimal:dependency" }));
            Assert.That(selected, Is.EquivalentTo(new[] { "urn:vao:test:unity:minimal:realization" }), "Dependencies must be included transitively and cycles must terminate.");
        }

        [Test]
        public void ControlStateTogglesWithoutHostSpecificManifestLogic()
        {
            var package = ScriptableObject.CreateInstance<VaoPackageAsset>();
            package.Controls.Add(new VaoControlRecord { Identifier = "urn:control", StateVariableIdentifier = "urn:state", DefaultBoolean = false });
            var gameObject = new GameObject("player");
            try
            {
                var player = gameObject.AddComponent<VaoSamplePlayer>();
                player.Package = package;
                Assert.That(player.GetState("urn:state"), Is.False);
                Assert.That(player.ToggleControl("urn:control"), Is.True);
                Assert.That(player.GetState("urn:state"), Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                UnityEngine.Object.DestroyImmediate(package);
            }
        }

        [Test]
        public void DeclarativeTransitionAndMidiBindingsExecuteAtRuntime()
        {
            const string stateId = "urn:state";
            const string controlId = "urn:control";
            const string eventId = "urn:event";
            var package = ScriptableObject.CreateInstance<VaoPackageAsset>();
            package.StateVariables.Add(new VaoStateVariableRecord { Identifier = stateId, ValueType = "boolean", DefaultValue = VaoPrimitiveValue.FromBoolean(false) });
            package.Controls.Add(new VaoControlRecord { Identifier = controlId, StateVariableIdentifier = stateId });
            package.Transitions.Add(new VaoTransitionRecord { Identifier = "urn:transition", ControlIdentifier = controlId, EventTypeIdentifier = eventId, Actions = { new VaoDeclarativeActionRecord { Operation = "toggle-state", TargetIdentifier = stateId } } });
            package.ProtocolBindings.Add(new VaoProtocolBindingRecord { Identifier = "urn:binding:midi1", Protocol = "MIDI-1.0", Direction = "input", ControlIdentifier = controlId, EventTypeIdentifier = eventId, MessageType = "program-change", Channel = 1, ChannelNumberingBase = 1, Number = 1 });
            package.ProtocolBindings.Add(new VaoProtocolBindingRecord { Identifier = "urn:binding:midi2", Protocol = "MIDI-2.0", Direction = "input", ControlIdentifier = controlId, EventTypeIdentifier = eventId, MessageType = "note", Channel = 0, ChannelNumberingBase = 0, Number = 60, UmpGroup = 0, UmpMessageType = 4 });
            var gameObject = new GameObject("router");
            try
            {
                var player = gameObject.AddComponent<VaoSamplePlayer>();
                player.Package = package;
                var router = gameObject.AddComponent<VaoMidiRouter>();
                router.Package = package;
                router.ProcessMidi1(0xc0, 1);
                Assert.That(player.GetState(stateId), Is.True);

                var note = -1;
                player.NoteStarted += (number, _) => note = number;
                router.ProcessMidi2Ump(0x40903c00u, 0xffff0000u);
                Assert.That(note, Is.EqualTo(60));
                note = -1;
                router.ProcessMidi2Ump(0x40903d00u, 0xffff0000u);
                Assert.That(note, Is.EqualTo(-1), "A live note outside the declared protocol binding must be ignored.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                UnityEngine.Object.DestroyImmediate(package);
            }
        }

        [Test]
        public void Midi2PreservesThirtyTwoBitValuesAndHonorsFunctionBlocksAndJrTime()
        {
            var package = ScriptableObject.CreateInstance<VaoPackageAsset>();
            package.ProtocolBindings.Add(new VaoProtocolBindingRecord
            {
                Identifier = "urn:binding:midi2:cc", Protocol = "MIDI-2.0", Direction = "input",
                ControlIdentifier = "urn:control:cc", EventTypeIdentifier = "urn:event:cc", MessageType = "control-change",
                Channel = 0, ChannelNumberingBase = 0, Number = 10, UmpGroup = 0, FunctionBlock = 2, UmpMessageType = 4
            });
            var gameObject = new GameObject("MIDI 2.0 router");
            try
            {
                var router = gameObject.AddComponent<VaoMidiRouter>();
                router.Package = package;
                uint received = 0;
                router.HighResolutionBindingReceived += (_, value) => received = value;

                router.ProcessMidi2Ump(0x40b00a00u, 0x89abcdefu, functionBlock: 1);
                Assert.That(received, Is.Zero, "A message from a different function block must be ignored.");

                router.ProcessUmpUtility(0x00101234u); // JR Clock
                router.ProcessUmpUtility(0x00201234u); // zero-delay JR Timestamp
                router.ProcessMidi2Ump(0x40b00a00u, 0x89abcdefu, functionBlock: 2);
                Assert.That(received, Is.EqualTo(0x89abcdefu));
                Assert.That(router.PendingJrDelaySeconds, Is.Zero.Within(1e-6));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                UnityEngine.Object.DestroyImmediate(package);
            }
        }

        [Test]
        public void TransactionalReimportPreservesGuidsOverridesUserFilesAndGeneratedControls()
        {
            var firstArchive = BuildArchive(payloadText: "vao-test-v1\n");
            var secondArchive = BuildArchive(payloadText: "vao-test-v2\n");
            var parent = "Assets/QA/Transactional Reimport " + Guid.NewGuid().ToString("N");
            GameObject instance = null;
            Scene overrideScene = default;
            try
            {
                var imported = VaoImporter.Import(firstArchive, new VaoImportOptions
                {
                    DestinationAssetPath = parent,
                    CreatePrefab = true,
                    CreateRuntimeControlSurface = true
                });
                var packagePath = imported.PackageAssetPath;
                var prefabPath = imported.PrefabAssetPath;
                Assert.That(imported.Package.Prefab.GetComponent<VaoPresentationSelector>(), Is.Not.Null);
                Assert.That(imported.Package.Prefab.GetComponent<VaoDeterministicExecutor>(), Is.Not.Null);
                var packageGuid = AssetDatabase.AssetPathToGUID(packagePath);
                var prefabGuid = AssetDatabase.AssetPathToGUID(prefabPath);
                var absolutePackagePath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", packagePath));
                var userFile = Path.Combine(Path.GetDirectoryName(absolutePackagePath) ?? string.Empty, "Host Notes.txt");
                File.WriteAllText(userFile, "host-authored and unmanaged\n");
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

                overrideScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                instance = (GameObject)PrefabUtility.InstantiatePrefab(imported.Package.Prefab);
                instance.transform.localScale = Vector3.one * 1.75f;
                PrefabUtility.RecordPrefabInstancePropertyModifications(instance.transform);
                Assert.That(EditorSceneManager.SaveScene(overrideScene, parent + "/Prefab Override Test.unity"), Is.True);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                overrideScene = default;
                instance = null;

                var existingPackage = AssetDatabase.LoadAssetAtPath<VaoPackageAsset>(packagePath);
                var preview = VaoReimport.Preview(secondArchive, existingPackage);
                Assert.That(preview.IsCompatible, Is.True, preview.Error);
                Assert.That(preview.ChangedCount, Is.EqualTo(1));
                var expectedArchiveHash = preview.Inspection.ArchiveSha256;
                var updated = VaoReimport.Apply(preview);

                Assert.That(AssetDatabase.AssetPathToGUID(packagePath), Is.EqualTo(packageGuid));
                Assert.That(AssetDatabase.AssetPathToGUID(prefabPath), Is.EqualTo(prefabGuid));
                Assert.That(updated.Package.SourceArchiveSha256, Is.EqualTo(expectedArchiveHash));
                Assert.That(updated.Package.Prefab.GetComponent<VaoRuntimeControlSurface>(), Is.Not.Null);
                Assert.That(updated.Package.Prefab.GetComponent<VaoPresentationSelector>(), Is.Not.Null);
                Assert.That(updated.Package.ImportSettings.CreateRuntimeControlSurface, Is.True);
                Assert.That(updated.Package.Capabilities.Count, Is.GreaterThanOrEqualTo(4));
                Assert.That(File.ReadAllText(userFile), Is.EqualTo("host-authored and unmanaged\n"));
                Assert.That(updated.Package.ImportSettings.ManagedRelativePaths, Does.Not.Contain("Host Notes.txt"));
                Assert.That(Directory.EnumerateDirectories(Application.dataPath, "VAO_Reimport_Staging_*", SearchOption.TopDirectoryOnly), Is.Empty);
                overrideScene = EditorSceneManager.OpenScene(parent + "/Prefab Override Test.unity", OpenSceneMode.Single);
                instance = overrideScene.GetRootGameObjects().Single(item => item.name == "Minimal Unity VAO");
                Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(instance), Is.Not.Null);
                Assert.That(instance.transform.localScale, Is.EqualTo(Vector3.one * 1.75f));
                Assert.That(PrefabUtility.HasPrefabInstanceAnyOverrides(instance, false), Is.True);
            }
            finally
            {
                if (overrideScene.IsValid() && overrideScene.isLoaded) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                else if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
                AssetDatabase.DeleteAsset(parent);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
        }

        [Test]
        public void TransactionalReimportRestoresOriginalTreeAfterPostSyncFailure()
        {
            var firstArchive = BuildArchive(payloadText: "rollback-v1\n");
            var secondArchive = BuildArchive(payloadText: "rollback-v2\n");
            var parent = "Assets/QA/Reimport Rollback " + Guid.NewGuid().ToString("N");
            try
            {
                var imported = VaoImporter.Import(firstArchive, new VaoImportOptions { DestinationAssetPath = parent });
                var packagePath = imported.PackageAssetPath;
                var originalHash = imported.Package.SourceArchiveSha256;
                var originalGuid = AssetDatabase.AssetPathToGUID(packagePath);
                var preview = VaoReimport.Preview(secondArchive, imported.Package);
                Assert.That(preview.IsCompatible, Is.True, preview.Error);
                VaoReimport.BeforePostSyncVerificationForTests = () => throw new InvalidOperationException("Injected verification failure");

                var failure = Assert.Throws<InvalidOperationException>(() => VaoReimport.Apply(preview));
                Assert.That(failure.Message, Does.Contain("Injected verification failure"));
                VaoReimport.BeforePostSyncVerificationForTests = null;

                var restored = AssetDatabase.LoadAssetAtPath<VaoPackageAsset>(packagePath);
                Assert.That(restored, Is.Not.Null);
                Assert.That(restored.SourceArchiveSha256, Is.EqualTo(originalHash));
                Assert.That(AssetDatabase.AssetPathToGUID(packagePath), Is.EqualTo(originalGuid));
                Assert.That(restored.Prefab, Is.Not.Null);
                Assert.That(Directory.EnumerateDirectories(Application.dataPath, "VAO_Reimport_Staging_*", SearchOption.TopDirectoryOnly), Is.Empty);
            }
            finally
            {
                VaoReimport.BeforePostSyncVerificationForTests = null;
                AssetDatabase.DeleteAsset(parent);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
        }

        [Test]
        public void TransactionalReimportCanAddANewPrefabWithoutDuplicateStagingGuids()
        {
            var firstArchive = BuildArchive(payloadText: "no-prefab-v1\n");
            var secondArchive = BuildArchive(payloadText: "with-prefab-v2\n");
            var parent = "Assets/QA/Reimport Added Prefab " + Guid.NewGuid().ToString("N");
            try
            {
                var imported = VaoImporter.Import(firstArchive, new VaoImportOptions { DestinationAssetPath = parent, CreatePrefab = false });
                Assert.That(imported.Package.Prefab, Is.Null);
                var options = VaoReimport.OptionsFrom(imported.Package);
                options.CreatePrefab = true;
                options.CreateRuntimeControlSurface = true;
                var preview = VaoReimport.Preview(secondArchive, imported.Package, options);
                Assert.That(preview.IsCompatible, Is.True, preview.Error);
                var updated = VaoReimport.Apply(preview).Package;

                Assert.That(updated.Prefab, Is.Not.Null);
                Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(updated.Prefab)), Is.Not.Empty);
                Assert.That(updated.Prefab.GetComponent<VaoRuntimeControlSurface>(), Is.Not.Null);
                Assert.That(updated.Prefab.GetComponent<VaoPresentationSelector>(), Is.Not.Null);
                Assert.That(Directory.EnumerateDirectories(Application.dataPath, "VAO_Reimport_Staging_*", SearchOption.TopDirectoryOnly), Is.Empty);
            }
            finally
            {
                AssetDatabase.DeleteAsset(parent);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
        }

        [Test]
        public void RuntimeCacheEnforcesPinnedQuotaEvictsEligibleContentAndRemovesStalePartials()
        {
            var root = Path.Combine(temporaryDirectory, "cache");
            Directory.CreateDirectory(root);
            var stale = Path.Combine(root, "ab.bin.partial-crash");
            File.WriteAllBytes(stale, new byte[] { 9, 9 });
            var firstBytes = Encoding.UTF8.GetBytes("first1");
            var firstHash = Hex(SHA256.Create().ComputeHash(firstBytes));
            var firstPath = Path.Combine(root, firstHash + ".bin");
            File.WriteAllBytes(firstPath, firstBytes);
            var cache = new VaoRuntimeCache(root, 10);
            Assert.That(File.Exists(stale), Is.False);
            cache.Commit(firstHash, firstPath, firstBytes.Length, true, 0);
            Assert.That(cache.Reserve(6, new string('b', 64), 0), Is.True);
            Assert.That(File.Exists(firstPath), Is.False, "Eligible least-priority content should be evicted to satisfy quota.");

            File.WriteAllBytes(firstPath, firstBytes);
            cache.Commit(firstHash, firstPath, firstBytes.Length, false, 100);
            Assert.That(cache.Reserve(6, new string('c', 64), 100), Is.False, "Pinned content must never be evicted by quota reservation.");
            Assert.That(File.Exists(firstPath), Is.True);
        }

        [Test]
        public void RepositoryResolverRejectsPrefixConfusionAndMaterializerRejectsMalformedFixity()
        {
            var root = new GameObject("resolver policy test");
            var package = ScriptableObject.CreateInstance<VaoPackageAsset>();
            try
            {
                var resolver = root.AddComponent<VaoExplicitRepositoryResolver>();
                resolver.Mappings.Add(new VaoRepositoryUriMapping
                {
                    DistributionIdentifier = "urn:distribution",
                    DownloadUri = "https://trusted.example/files/item.bin",
                    AllowedRedirectPrefix = "https://trusted.example/files"
                });
                Assert.That(resolver.IsUriAllowed(new Uri("https://trusted.example/files/item.bin")), Is.True);
                Assert.That(resolver.IsUriAllowed(new Uri("https://trusted.example/files/sub/item.bin")), Is.True);
                Assert.That(resolver.IsUriAllowed(new Uri("https://trusted.example/files-evil/item.bin")), Is.False);
                Assert.That(resolver.IsUriAllowed(new Uri("https://trusted.example.evil/files/item.bin")), Is.False);
                Assert.That(resolver.IsUriAllowed(new Uri("https://user@trusted.example/files/item.bin")), Is.False);

                package.Realizations.Add(new VaoRealizationRecord { Identifier = "urn:bad", ByteSize = 12, Sha256 = "../not-a-digest" });
                var materializer = root.AddComponent<VaoRuntimeMaterializer>();
                materializer.Package = package;
                var plan = materializer.CreatePlan("urn:bad");
                Assert.That(plan.CanAcquire, Is.False);
                Assert.That(plan.Error, Does.Contain("SHA-256"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(package);
            }
        }

        [Test]
        public void InProjectSourceDependencyTracksGuidMovesAndDigestChanges()
        {
            var first = BuildArchive(payloadText: "source-v1\n");
            var second = BuildArchive(payloadText: "source-v2\n");
            var parent = "Assets/QA/Source Tracking " + Guid.NewGuid().ToString("N");
            var absoluteParent = Path.GetFullPath(Path.Combine(Application.dataPath, "..", parent));
            var source = parent + "/Source.vao";
            var moved = parent + "/Moved Source.vao";
            try
            {
                Directory.CreateDirectory(absoluteParent);
                File.Copy(first, Path.Combine(absoluteParent, "Source.vao"));
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                var imported = VaoImporter.Import(source, new VaoImportOptions { DestinationAssetPath = parent + "/Import" });
                Assert.That(imported.Package.SourceArchiveGuid, Is.Not.Empty);
                Assert.That(VaoReimport.ResolveSourcePath(imported.Package), Is.EqualTo(source));
                Assert.That(VaoSourceDependency.State(imported.Package, out _), Is.EqualTo(VaoSourceState.UpToDate));

                Assert.That(AssetDatabase.MoveAsset(source, moved), Is.Empty);
                Assert.That(VaoReimport.ResolveSourcePath(imported.Package), Is.EqualTo(moved));
                File.Copy(second, Path.Combine(absoluteParent, "Moved Source.vao"), true);
                AssetDatabase.ImportAsset(moved, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                Assert.That(VaoSourceDependency.State(imported.Package, out var resolved), Is.EqualTo(VaoSourceState.Changed));
                Assert.That(resolved, Is.EqualTo(moved));
            }
            finally
            {
                AssetDatabase.DeleteAsset(parent);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
        }

        private string BuildArchive(bool corruptDigest = false, bool unsafePath = false, bool invalidSchema = false, string payloadText = "vao-test\n", string payloadPathOverride = null, string additionalPayloadPath = null)
        {
            var payload = Encoding.UTF8.GetBytes(payloadText);
            var digest = Hex(SHA256.Create().ComputeHash(payload));
            var payloadPath = unsafePath ? "payload/../escape.bin" : payloadPathOverride ?? "payload/evidence/test.bin";
            var rootId = "urn:vao:test:unity:minimal";
            var releaseId = rootId + ":release:1";
            var entityId = rootId + ":entity";
            var assetId = rootId + ":asset";
            var realizationId = rootId + ":realization";
            var groupId = rootId + ":group";
            var rightsId = rootId + ":rights";
            var agentId = rootId + ":agent";
            var activityId = rootId + ":activity";
            var protocolId = rootId + ":protocol";
            var core = VaoArchiveReader.CoreProfile;
            var dynamic = VaoArchiveReader.DynamicProfile;
            var scientific = "https://w3id.org/modavis/vao/profile/scientific/0.4.0";
            var manifest = new JObject
            {
                ["$schema"] = VaoArchiveReader.Schema,
                ["@context"] = new JArray(VaoArchiveReader.Context),
                ["type"] = "VirtualAcousticObject",
                ["formatVersion"] = "0.4.0",
                ["id"] = rootId,
                ["release"] = new JObject { ["id"] = releaseId, ["revision"] = 1, ["contentVersion"] = "test" },
                ["createdAt"] = "2026-08-25T00:00:00Z",
                ["modifiedAt"] = "2026-08-25T00:00:00Z",
                ["title"] = new JObject { ["en"] = "Minimal Unity VAO" },
                ["conformsTo"] = new JArray(core, dynamic, scientific),
                ["profiles"] = new JArray(
                    new JObject { ["id"] = core, ["version"] = "0.4.0", ["requiredCapabilities"] = new JArray("https://w3id.org/modavis/vao/vocab/capability/core-graph", "https://w3id.org/modavis/vao/vocab/capability/fixity") },
                    new JObject { ["id"] = dynamic, ["version"] = "0.4.0", ["requiredCapabilities"] = new JArray("https://w3id.org/modavis/vao/vocab/capability/immutable-release", "https://w3id.org/modavis/vao/vocab/capability/carrier-mapping") },
                    new JObject { ["id"] = scientific, ["version"] = "0.4.0", ["requiredCapabilities"] = new JArray("https://w3id.org/modavis/vao/vocab/capability/typed-scientific-provenance") }),
                ["materializableProfiles"] = new JArray(),
                ["modavisBinding"] = new JObject
                {
                    ["ontologyIRI"] = "https://w3id.org/modavis/ontology", ["ontologyVersionIRI"] = "https://w3id.org/modavis/ontology/0.1.0",
                    ["ontologyVersion"] = "0.1.0", ["ontologyStatus"] = "released", ["mappingIRI"] = "https://w3id.org/modavis/vao/0.4.0/modavis-mapping", ["mappingVersion"] = "0.4.0"
                },
                ["primaryEntityId"] = entityId,
                ["focusEntityIds"] = new JArray(entityId),
                ["entities"] = new JArray(new JObject { ["id"] = entityId, ["kind"] = "instrument", ["types"] = new JArray("https://example.org/Instrument"), ["labels"] = new JObject { ["en"] = "Test" } }),
                ["relations"] = new JArray(),
                ["scientific"] = new JObject
                {
                    ["agents"] = new JArray(new JObject { ["id"] = agentId, ["agentKind"] = "software-agent", ["labels"] = new JObject { ["en"] = "Unity VAO test builder" } }),
                    ["activities"] = new JArray(new JObject
                    {
                        ["id"] = activityId, ["activityKind"] = "authoring", ["startedAt"] = "2026-08-25T00:00:00Z", ["endedAt"] = "2026-08-25T00:00:00Z",
                        ["agentIds"] = new JArray(agentId), ["protocolId"] = protocolId, ["inputIds"] = new JArray(), ["outputIds"] = new JArray(realizationId)
                    }),
                    ["observations"] = new JArray(), ["analyses"] = new JArray(), ["calibrations"] = new JArray(),
                    ["protocols"] = new JArray(new JObject { ["id"] = protocolId, ["labels"] = new JObject { ["en"] = "Synthetic test authoring" }, ["procedure"] = "Create exact synthetic bytes for importer tests.", ["version"] = "1" }),
                    ["softwareEnvironments"] = new JArray(), ["claims"] = new JArray(), ["reviews"] = new JArray(), ["consents"] = new JArray()
                },
                ["multimodal"] = EmptyRegistry("timebases", "tracks", "synchronizationMappings", "annotations"),
                ["physicalSystem"] = EmptyRegistry("components", "ports", "connections", "sensors", "actuators", "stateBindings"),
                ["runtime"] = new JObject
                {
                    ["executionSemantics"] = new JObject
                    {
                        ["actionExecution"] = "execution-group-then-array-order", ["lateEventPolicy"] = "reject", ["reentrancyPolicy"] = "queue", ["runToCompletion"] = true,
                        ["simultaneousEventOrder"] = "priority-then-event-id", ["timeResolution"] = new JObject { ["unit"] = "http://qudt.org/vocab/unit/MilliSEC", ["value"] = 1 },
                        ["timestampOrder"] = "ascending", ["transitionEvaluation"] = "snapshot", ["maximumMicrosteps"] = 10000,
                        ["voiceAllocation"] = "lowest-free-then-oldest", ["maximumVoices"] = 1024
                    },
                    ["randomSources"] = new JArray(), ["renderers"] = new JArray(), ["conformanceTraces"] = new JArray()
                },
                ["discovery"] = new JObject { ["resourceType"] = "Dataset", ["creatorAgentIds"] = new JArray(agentId), ["contributorAgentIds"] = new JArray(), ["relatedIdentifiers"] = new JArray(), ["fundingReferences"] = new JArray(), ["subjects"] = new JArray() },
                ["logicalAssets"] = new JArray(new JObject { ["id"] = assetId, ["type"] = "LogicalAsset", ["roles"] = new JArray("https://example.org/evidence"), ["aboutEntityIds"] = new JArray(entityId), ["realizationIds"] = new JArray(realizationId), ["properties"] = new JObject() }),
                ["realizations"] = new JArray(new JObject { ["id"] = realizationId, ["type"] = "Realization", ["assetId"] = assetId, ["variantSetId"] = "test", ["qualityTier"] = "preservation", ["mediaType"] = "application/octet-stream", ["byteSize"] = payload.Length, ["sha256"] = corruptDigest ? new string('0', 64) : digest, ["contentDigests"] = new JArray(new JObject { ["algorithm"] = "sha256", ["value"] = corruptDigest ? new string('0', 64) : digest }), ["representationStatus"] = "https://w3id.org/modavis/vao/vocab/representation-status/authored", ["rightsIds"] = new JArray(rightsId), ["provenanceIds"] = new JArray(activityId), ["technicalMetadata"] = new JObject { ["kind"] = "data" }, ["distributionIds"] = new JArray() }),
                ["distributions"] = new JArray(),
                ["repositoryBindings"] = new JArray(),
                ["assetGroups"] = new JArray(new JObject { ["id"] = groupId, ["type"] = "AssetGroup", ["labels"] = new JObject { ["en"] = "Test" }, ["selectionSetId"] = "test", ["qualityTier"] = "preservation", ["availability"] = "offline-required", ["selectionPolicy"] = "independent", ["realizationIds"] = new JArray(realizationId), ["dependsOnGroupIds"] = new JArray(), ["totalByteSize"] = payload.Length, ["requiredCapabilities"] = new JArray(), ["materializesProfileIds"] = new JArray(), ["cachePolicy"] = new JObject { ["evictable"] = false, ["priority"] = 100 } }),
                ["rights"] = new JArray(new JObject { ["id"] = rightsId, ["appliesToIds"] = new JArray(rootId, assetId, realizationId), ["statement"] = new JObject { ["en"] = "Synthetic test bytes." }, ["access"] = "open" }),
                ["integrity"] = new JObject { ["algorithm"] = "sha256", ["manifestDigestLocation"] = "external-release-and-carrier-descriptors", ["carrierDescriptor"] = VaoArchiveReader.CarrierName }
            };
            if (invalidSchema) ((JObject)manifest["release"])["unexpected"] = true;
            var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToString(Formatting.None) + "\n");
            var carrier = new JObject
            {
                ["$schema"] = "https://w3id.org/modavis/vao/0.4.0/schema/carrier.json",
                ["type"] = "VAOCarrier",
                ["formatVersion"] = "0.4.0",
                ["releaseId"] = releaseId,
                ["manifestSHA256"] = Hex(SHA256.Create().ComputeHash(manifestBytes)),
                ["manifestByteSize"] = manifestBytes.Length,
                ["carrierMode"] = "preservation-closure",
                ["embeddedRealizations"] = new JArray(new JObject { ["realizationId"] = realizationId, ["path"] = payloadPath }),
                ["completeGroupIds"] = new JArray(groupId)
            };
            var path = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N") + ".vao");
            var entries = new List<KeyValuePair<string, byte[]>>
            {
                new KeyValuePair<string, byte[]>("mimetype", Encoding.UTF8.GetBytes(VaoArchiveReader.MediaType)),
                new KeyValuePair<string, byte[]>(VaoArchiveReader.ManifestName, manifestBytes),
                new KeyValuePair<string, byte[]>(VaoArchiveReader.CarrierName, Encoding.UTF8.GetBytes(carrier.ToString(Formatting.None) + "\n")),
                new KeyValuePair<string, byte[]>(payloadPath, payload)
            };
            if (!string.IsNullOrEmpty(additionalPayloadPath)) entries.Add(new KeyValuePair<string, byte[]>(additionalPayloadPath, Encoding.UTF8.GetBytes("collision")));
            WriteStoredZip(path, entries);
            return path;
        }

        private static void PatchFirstCentralHeader(string path, Action<byte[]> patch)
        {
            var bytes = File.ReadAllBytes(path);
            var offset = -1;
            for (var index = bytes.Length - 46; index >= 0; index--)
            {
                if (bytes[index] == 0x50 && bytes[index + 1] == 0x4B && bytes[index + 2] == 0x01 && bytes[index + 3] == 0x02) offset = index;
            }
            if (offset < 0) throw new InvalidDataException("Test ZIP has no central-directory header.");
            var header = new byte[46];
            Buffer.BlockCopy(bytes, offset, header, 0, header.Length);
            patch(header);
            Buffer.BlockCopy(header, 0, bytes, offset, header.Length);
            File.WriteAllBytes(path, bytes);
        }

        private static JObject EmptyRegistry(params string[] names)
        {
            var value = new JObject();
            foreach (var name in names) value[name] = new JArray();
            return value;
        }

        private static void WriteStoredZip(string path, IEnumerable<KeyValuePair<string, byte[]>> values)
        {
            var entries = values.ToList();
            var offsets = new List<uint>();
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, false);
            foreach (var entry in entries)
            {
                var name = Encoding.UTF8.GetBytes(entry.Key);
                var crc = Crc32(entry.Value);
                offsets.Add((uint)stream.Position);
                writer.Write(0x04034b50u);
                writer.Write((ushort)20); writer.Write((ushort)0); writer.Write((ushort)0);
                writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(crc);
                writer.Write((uint)entry.Value.Length); writer.Write((uint)entry.Value.Length);
                writer.Write((ushort)name.Length); writer.Write((ushort)0);
                writer.Write(name); writer.Write(entry.Value);
            }
            var centralOffset = (uint)stream.Position;
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                var name = Encoding.UTF8.GetBytes(entry.Key);
                var crc = Crc32(entry.Value);
                writer.Write(0x02014b50u);
                writer.Write((ushort)20); writer.Write((ushort)20); writer.Write((ushort)0); writer.Write((ushort)0);
                writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(crc);
                writer.Write((uint)entry.Value.Length); writer.Write((uint)entry.Value.Length);
                writer.Write((ushort)name.Length); writer.Write((ushort)0); writer.Write((ushort)0);
                writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(0u); writer.Write(offsets[index]);
                writer.Write(name);
            }
            var centralSize = (uint)stream.Position - centralOffset;
            writer.Write(0x06054b50u);
            writer.Write((ushort)0); writer.Write((ushort)0);
            writer.Write((ushort)entries.Count); writer.Write((ushort)entries.Count);
            writer.Write(centralSize); writer.Write(centralOffset); writer.Write((ushort)0);
        }

        private static uint Crc32(byte[] bytes)
        {
            var crc = 0xffffffffu;
            foreach (var value in bytes)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
            }
            return ~crc;
        }

        private static string Hex(byte[] bytes) => string.Concat(bytes.Select(value => value.ToString("x2")));
    }
}
