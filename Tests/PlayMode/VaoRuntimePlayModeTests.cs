using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Modavis.Vao.PlayMode.Tests
{
    public sealed class VaoRuntimePlayModeTests
    {
        [UnityTest]
        public IEnumerator PlayableBackendLayersBlendsCurvesAndSequencesLinkedClips()
        {
            var package = ScriptableObject.CreateInstance<VaoPackageAsset>();
            var horizontal = new AnimationClip { name = "Horizontal mechanism" };
#if UNITY_EDITOR
            horizontal.SetCurve("MovingPart", typeof(Transform), "localPosition.x", AnimationCurve.Linear(0f, 0f, 1f, 1f));
#endif
            var vertical = new AnimationClip { name = "Vertical mechanism" };
#if UNITY_EDITOR
            vertical.SetCurve("MovingPart", typeof(Transform), "localPosition.y", AnimationCurve.Linear(0f, 0f, 1f, 2f));
#endif
            package.AnimationLinks.Add(new VaoAnimationLink { Identifier = "urn:link:horizontal", TargetLogicalAssetIdentifier = "urn:model", SourceClip = horizontal });
            package.AnimationLinks.Add(new VaoAnimationLink { Identifier = "urn:link:vertical", TargetLogicalAssetIdentifier = "urn:model", SourceClip = vertical });

            var root = new GameObject("VAO Playables test");
            var model = new GameObject("Model").transform; model.SetParent(root.transform, false);
            var part = new GameObject("MovingPart").transform; part.SetParent(model, false);
            var player = root.AddComponent<VaoLinkedAnimationPlayer>();
            player.Backend = VaoAnimationBackend.PlayableGraph;
            player.Package = package;
            player.SetTargetRoot("urn:model", model);
            player.SetLayerConfiguration(new VaoAnimationLayerConfiguration
            {
                LinkIdentifier = "urn:link:horizontal", LayerOrder = 0, Weight = 1f, BlendSeconds = 0f, Speed = 0.5f,
                SpeedCurve = AnimationCurve.Constant(0f, 1f, 0.5f)
            });
            player.SetLayerConfiguration(new VaoAnimationLayerConfiguration { LinkIdentifier = "urn:link:vertical", LayerOrder = 1, Weight = 1f, BlendSeconds = 0f, Additive = true });
            try
            {
                player.PlayLinkedClip(0);
                player.PlayLinkedClip(1);
                yield return new WaitForSecondsRealtime(0.12f);
                Assert.That(player.GetActiveBackend(0), Is.EqualTo(VaoAnimationBackend.PlayableGraph));
                Assert.That(model.GetComponent<Animator>(), Is.Not.Null);
#if UNITY_EDITOR
                Assert.That(player.GetLinkedClipTime(0), Is.InRange(0.01f, 0.09f), "The 0.5 speed and 0.5 speed curve must both affect graph time.");
#endif

                player.SetLinkedClipNormalizedTime(0, 0.5f, false);
                player.SetLinkedClipNormalizedTime(1, 0.5f, false);
                player.SetLinkedClipWeight(0, 1f);
                player.SetLinkedClipWeight(1, 1f);
                yield return null;
#if UNITY_EDITOR
                Assert.That(part.localPosition.x, Is.EqualTo(0.5f).Within(0.06f));
                Assert.That(part.localPosition.y, Is.EqualTo(1f).Within(0.08f));
#endif

                player.CrossFadeLinkedClips(0, 1, 0.04f, 0.5f);
                yield return new WaitForSecondsRealtime(0.08f);
                Assert.That(player.GetLinkedClipWeight(0), Is.EqualTo(0f).Within(0.03f));
                Assert.That(player.GetLinkedClipWeight(1), Is.EqualTo(1f).Within(0.03f));

                var sequence = new VaoAnimationSequence
                {
                    Identifier = "urn:sequence:test",
                    Steps = { new VaoAnimationSequenceStep { LinkIndex = 0, StartNormalizedTime = 0f, EndNormalizedTime = 0.05f, Speed = 8f, Weight = 1f, FadeSeconds = 0f } }
                };
                Assert.That(player.PlaySequence(sequence), Is.True);
                var deadline = Time.realtimeSinceStartup + 1f;
                while (player.IsSequencePlaying && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.That(player.IsSequencePlaying, Is.False, "The finite linked-clip sequence must complete.");
                Assert.That(player.GetLinkedClipTime(0), Is.EqualTo(0.05f * player.GetLinkedClipLength(0)).Within(0.02f));
            }
            finally
            {
                Object.Destroy(root);
                Object.Destroy(horizontal);
                Object.Destroy(vertical);
                Object.Destroy(package);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator MediaTransportSynchronizesLinkedAnimationAndTrackedPlacement()
        {
            const string mediaLogicalId = "urn:asset:performance";
            const string mediaRealizationId = "urn:realization:performance";
            const string animationLogicalId = "urn:asset:animation";
            const string modelLogicalId = "urn:asset:model";
            var package = ScriptableObject.CreateInstance<VaoPackageAsset>();
            var mediaClip = AudioClip.Create("Program audio", 44100, 1, 44100, false);
            var sampleClip = AudioClip.Create("Instrument sample", 1000, 1, 44100, false);
            var animationClip = new AnimationClip { name = "Mechanical motion", legacy = true };
            animationClip.SetCurve("MovingPart", typeof(Transform), "localPosition.x", AnimationCurve.Linear(0f, 0f, 1f, 1f));
            package.LogicalAssets.Add(new VaoLogicalAssetRecord { Identifier = mediaLogicalId, Label = "Program 1", Roles = new[] { "performance-recording" }, RealizationIdentifiers = new[] { mediaRealizationId } });
            package.LogicalAssets.Add(new VaoLogicalAssetRecord { Identifier = "urn:asset:sample", Roles = new[] { "sample" }, RealizationIdentifiers = new[] { "urn:realization:sample" } });
            package.Realizations.Add(new VaoRealizationRecord { Identifier = mediaRealizationId, LogicalAssetIdentifier = mediaLogicalId, MediaType = "audio/wav", IsMaterialized = true, ImportedObject = mediaClip });
            package.Realizations.Add(new VaoRealizationRecord { Identifier = "urn:realization:sample", LogicalAssetIdentifier = "urn:asset:sample", MediaType = "audio/wav", IsMaterialized = true, ImportedObject = sampleClip });
            package.SampleBindings.Add(new VaoSampleBinding { RealizationIdentifier = "urn:realization:sample", Clip = sampleClip });
            package.AnimationLinks.Add(new VaoAnimationLink { Identifier = "urn:link:program-animation", SourceLogicalAssetIdentifier = mediaLogicalId, AnimationLogicalAssetIdentifier = animationLogicalId, TargetLogicalAssetIdentifier = modelLogicalId, SourceClip = animationClip });

            var root = new GameObject("VAO media transport test");
            root.AddComponent<AudioListener>();
            var model = new GameObject("Model").transform; model.SetParent(root.transform, false);
            var movingPart = new GameObject("MovingPart").transform; movingPart.SetParent(model, false);
            var visiblePart = new GameObject("VisiblePart"); visiblePart.transform.SetParent(root.transform, false);
            var renderer = visiblePart.AddComponent<MeshRenderer>();
            var collider = visiblePart.AddComponent<BoxCollider>();
            var animations = root.AddComponent<VaoLinkedAnimationPlayer>(); animations.Package = package; animations.SetTargetRoot(modelLogicalId, model);
            var media = root.AddComponent<VaoMediaPlayer>(); media.LinkedAnimations = animations; media.Package = package;
            var placement = root.AddComponent<VaoTrackedPlacement>(); placement.PlacementRoot = root.transform; placement.ContentRoot = root.transform; placement.RefreshContentState();
            var anchor = new GameObject("Tracked anchor").transform;
            try
            {
                Assert.That(media.Entries.Count, Is.EqualTo(1), "Sample realizations must not become recording/program choices.");
                Assert.That(media.SelectedEntry.LogicalAssetIdentifier, Is.EqualTo(mediaLogicalId));
                media.Play();
                yield return new WaitForSecondsRealtime(0.08f);
                media.Pause();
                media.SeekNormalized(0.5f);
                Assert.That(animations.GetLinkedClipTime(0), Is.EqualTo(0.5f).Within(0.03f));
                Assert.That(movingPart.localPosition.x, Is.EqualTo(0.5f).Within(0.05f));

                placement.SetTrackingActive(false);
                Assert.That(renderer.enabled, Is.False);
                Assert.That(collider.enabled, Is.False);
                placement.SetTrackingActive(true);
                Assert.That(renderer.enabled, Is.True);
                Assert.That(collider.enabled, Is.True);
                placement.AttachToAnchor(anchor);
                Assert.That(root.transform.parent, Is.EqualTo(anchor));
                Assert.That(root.transform.localPosition, Is.EqualTo(Vector3.zero));
                placement.SetUniformScale(2f);
                Assert.That(root.transform.localScale, Is.EqualTo(Vector3.one * 2f));
            }
            finally
            {
                Object.Destroy(root);
                Object.Destroy(anchor.gameObject);
                Object.Destroy(animationClip);
                Object.Destroy(mediaClip);
                Object.Destroy(sampleClip);
                Object.Destroy(package);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator MidiControlDrivesStateSampleVoiceAndLinkedKeyMotion()
        {
            const string stateId = "urn:state:stop";
            const string controlId = "urn:control:stop";
            const string eventId = "urn:event:control";
            var package = ScriptableObject.CreateInstance<VaoPackageAsset>();
            var clip = AudioClip.Create("VAO synthetic sample", 44100, 1, 44100, false);
            package.StateVariables.Add(new VaoStateVariableRecord { Identifier = stateId, ValueType = "boolean", DefaultValue = VaoPrimitiveValue.FromBoolean(false) });
            package.Controls.Add(new VaoControlRecord { Identifier = controlId, StateVariableIdentifier = stateId });
            package.Transitions.Add(new VaoTransitionRecord { Identifier = "urn:transition", ControlIdentifier = controlId, EventTypeIdentifier = eventId, Actions = { new VaoDeclarativeActionRecord { Operation = "toggle-state", TargetIdentifier = stateId } } });
            package.ProtocolBindings.Add(new VaoProtocolBindingRecord { Identifier = "urn:binding:program", Protocol = "MIDI-1.0", Direction = "input", ControlIdentifier = controlId, EventTypeIdentifier = eventId, MessageType = "program-change", Channel = 1, ChannelNumberingBase = 1, Number = 1 });
            package.ProtocolBindings.Add(new VaoProtocolBindingRecord { Identifier = "urn:binding:note", Protocol = "MIDI-1.0", Direction = "input", ControlIdentifier = "urn:control:key", EventTypeIdentifier = "urn:event:note", MessageType = "note", Channel = 1, ChannelNumberingBase = 1, Number = 60 });
            package.SampleBindings.Add(new VaoSampleBinding { MappingIdentifier = "urn:mapping", VariantIdentifier = "urn:variant", StateVariableIdentifier = stateId, SelectionPolicy = "single", Trigger = "note-on", MinimumKey = 60, MaximumKey = 60, MinimumVelocity = 1, MaximumVelocity = 127, SampleRootKey = 60, NoteOffPolicy = "envelope", Clip = clip });
            package.AnimationLinks.Add(new VaoAnimationLink { Identifier = "urn:animation-link", TargetLogicalAssetIdentifier = "urn:model:manual", TargetPathPattern = "M1.{midiNote}", MinimumMidiNote = 60, MaximumMidiNote = 60, RotationAxis = Vector3.right, PressedAngleDegrees = -4f });
            package.AnimationLinks.Add(new VaoAnimationLink { Identifier = "urn:animation-link:pedal", TargetLogicalAssetIdentifier = "urn:model:pedal", TargetPathPattern = "Pedal.{midiNote}", MinimumMidiNote = 60, MaximumMidiNote = 60, RotationAxis = Vector3.forward, PressedAngleDegrees = 6f });

            var root = new GameObject("VAO runtime test");
            root.AddComponent<AudioListener>();
            var manualRoot = new GameObject("Manual model").transform; manualRoot.SetParent(root.transform, false);
            var pedalRoot = new GameObject("Pedal model").transform; pedalRoot.SetParent(root.transform, false);
            var key = new GameObject("M1.60").transform;
            key.SetParent(manualRoot, false);
            var pedalKey = new GameObject("Pedal.60").transform; pedalKey.SetParent(pedalRoot, false);
            var rest = key.localRotation;
            var pedalRest = pedalKey.localRotation;
            var player = root.AddComponent<VaoSamplePlayer>(); player.Package = package;
            var animations = root.AddComponent<VaoLinkedAnimationPlayer>(); animations.Package = package; animations.SetTargetRoot("urn:model:manual", manualRoot); animations.SetTargetRoot("urn:model:pedal", pedalRoot);
            var router = root.AddComponent<VaoMidiRouter>(); router.Package = package;
            try
            {
                router.ProcessMidi1(0xc0, 1);
                Assert.That(player.GetState(stateId), Is.True);
                router.ProcessMidi1(0x90, 60, 100);
                yield return new WaitForSecondsRealtime(0.08f);
                Assert.That(root.GetComponentsInChildren<AudioSource>(true), Has.Length.EqualTo(1));
                Assert.That(Quaternion.Angle(rest, key.localRotation), Is.GreaterThan(2f));
                Assert.That(Quaternion.Angle(pedalRest, pedalKey.localRotation), Is.GreaterThan(3f));

                router.ProcessMidi1(0x80, 60, 0);
                yield return new WaitForSecondsRealtime(0.4f);
                Assert.That(Quaternion.Angle(rest, key.localRotation), Is.LessThan(0.25f));
                Assert.That(Quaternion.Angle(pedalRest, pedalKey.localRotation), Is.LessThan(0.25f));
                Assert.That(root.GetComponentsInChildren<AudioSource>(true), Is.Empty);
            }
            finally
            {
                Object.Destroy(root);
                Object.Destroy(clip);
                Object.Destroy(package);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator OptionalMidiJackAdapterDiscoversHardwareAndForwardsDeclaredNotes()
        {
            var package = ScriptableObject.CreateInstance<VaoPackageAsset>();
            var clip = AudioClip.Create("Optional MIDI sample", 44100, 1, 44100, false);
            package.ProtocolBindings.Add(new VaoProtocolBindingRecord { Protocol = "MIDI-1.0", Direction = "input", MessageType = "note", Channel = 1, ChannelNumberingBase = 1, Number = 60 });
            package.SampleBindings.Add(new VaoSampleBinding { MinimumKey = 60, MaximumKey = 60, MinimumVelocity = 1, MaximumVelocity = 127, SampleRootKey = 60, Trigger = "note-on", NoteOffPolicy = "envelope", Clip = clip });
            var root = new GameObject("VAO optional MIDI adapter test");
            root.AddComponent<AudioListener>();
            var samples = root.AddComponent<VaoSamplePlayer>(); samples.Package = package;
            var router = root.AddComponent<VaoMidiRouter>(); router.Package = package;
            var adapter = root.AddComponent<VaoMidiDeviceAdapter>();
            adapter.Provider = VaoMidiDeviceProvider.MidiJack;
            try
            {
                Assert.That(adapter.Connect(), Is.True);
                Assert.That(adapter.ActiveProvider, Is.EqualTo("MidiJack"));
                MidiJack.MidiMaster.Keys[60] = 0.75f;
                yield return null;
                Assert.That(root.GetComponentsInChildren<AudioSource>(true), Has.Length.EqualTo(1));
                MidiJack.MidiMaster.Keys[60] = 0f;
                yield return new WaitForSecondsRealtime(0.4f);
                Assert.That(root.GetComponentsInChildren<AudioSource>(true), Is.Empty);
            }
            finally
            {
                MidiJack.MidiMaster.Reset();
                Object.Destroy(root);
                Object.Destroy(clip);
                Object.Destroy(package);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator OptionalMinisAdapterDiscoversCurrentDeviceAndForwardsDeclaredNotes()
        {
            var package = ScriptableObject.CreateInstance<VaoPackageAsset>();
            var clip = AudioClip.Create("Optional Minis sample", 44100, 1, 44100, false);
            package.ProtocolBindings.Add(new VaoProtocolBindingRecord { Protocol = "MIDI-1.0", Direction = "input", MessageType = "note", Channel = 1, ChannelNumberingBase = 1, Number = 60 });
            package.SampleBindings.Add(new VaoSampleBinding { MinimumKey = 60, MaximumKey = 60, MinimumVelocity = 1, MaximumVelocity = 127, SampleRootKey = 60, Trigger = "note-on", NoteOffPolicy = "envelope", Clip = clip });
            var root = new GameObject("VAO optional Minis adapter test");
            root.AddComponent<AudioListener>();
            var samples = root.AddComponent<VaoSamplePlayer>(); samples.Package = package;
            var router = root.AddComponent<VaoMidiRouter>(); router.Package = package;
            Minis.MidiDevice.current = new Minis.MidiDevice();
            var adapter = root.AddComponent<VaoMidiDeviceAdapter>();
            adapter.Provider = VaoMidiDeviceProvider.Minis;
            try
            {
                Assert.That(adapter.Connect(), Is.True);
                Assert.That(adapter.ActiveProvider, Is.EqualTo("Minis"));
                Minis.MidiDevice.current.GetNote(60).Value = 0.8f;
                yield return null;
                Assert.That(root.GetComponentsInChildren<AudioSource>(true), Has.Length.EqualTo(1));
                Minis.MidiDevice.current.GetNote(60).Value = 0f;
                yield return new WaitForSecondsRealtime(0.4f);
                Assert.That(root.GetComponentsInChildren<AudioSource>(true), Is.Empty);
            }
            finally
            {
                Minis.MidiDevice.current = null;
                Object.Destroy(root);
                Object.Destroy(clip);
                Object.Destroy(package);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator DeterministicExecutorAndAcousticRendererSwitchOperateInPlayMode()
        {
            var package = ScriptableObject.CreateInstance<VaoPackageAsset>();
            var impulse = AudioClip.Create("Test impulse", 64, 1, 48000, false);
            var samples = new float[64]; samples[0] = 1f; impulse.SetData(samples, 0);
            package.StateVariables.Add(new VaoStateVariableRecord { Identifier = "state", ValueType = "number", DefaultValue = VaoPrimitiveValue.FromNumber(0) });
            package.TimingConstraints.Add(new VaoTimingConstraintRecord { Identifier = "delay", Unit = "milliseconds", Minimum = 10 });
            package.ProcessModels.Add(new VaoProcessModelRecord
            {
                Identifier = "process", ProcessKind = "one-shot", Ordering = "single", TerminationPolicy = "completed",
                Actions = { new VaoDeclarativeActionRecord { Operation = "set-state", TargetIdentifier = "state", HasValue = true, Value = VaoPrimitiveValue.FromNumber(7), DelayConstraintIdentifier = "delay" } }
            });
            package.AcousticScenes.Add(new VaoAcousticSceneRecord { Identifier = "scene", ResponseSetIdentifier = "response", ResponseKind = "rir", ResponseEncoding = "WAV", ImpulseResponse = impulse });
            var root = new GameObject("Deterministic acoustics test");
            try
            {
                var player = root.AddComponent<VaoSamplePlayer>(); player.Package = package;
                var executor = root.AddComponent<VaoDeterministicExecutor>(); executor.Package = package;
                root.AddComponent<VaoConvolutionRenderer>();
                var external = root.AddComponent<PlayModeAcousticRenderer>(); external.Name = "external"; external.Priority = 200;
                var environment = root.AddComponent<VaoAcousticEnvironment>(); environment.Package = package;
                yield return null;

                Assert.That(environment.Renderer.RendererName, Is.EqualTo("external"));
                Assert.That(environment.SelectRenderer("VAO position-aware RIR/SOFA convolution"), Is.True);
                Assert.That(environment.ConvolutionAvailable, Is.True);
                Assert.That(environment.SelectNextRenderer(), Is.True);
                Assert.That(environment.Renderer.RendererName, Is.EqualTo("external"));

                Assert.That(executor.StartProcess("process"), Is.True);
                executor.AdvanceTo(executor.CurrentTime + 0.02);
                Assert.That(player.GetStateValue("state").Number, Is.EqualTo(7));
            }
            finally { Object.Destroy(root); Object.Destroy(impulse); Object.Destroy(package); }
            yield return null;
        }

        [UnityTest]
        public IEnumerator RuntimeMaterializationRequiresRightsAwareConsentVerifiesFixityAndCaches()
        {
            const string realizationId = "urn:vao:test:remote:realization";
            const string logicalId = "urn:vao:test:remote:asset";
            const string distributionId = "urn:vao:test:remote:distribution";
            const string repositoryId = "urn:vao:test:remote:repository";
            const string rightsId = "urn:vao:test:remote:rights";
            var temporary = Path.Combine(Path.GetTempPath(), "vao-materializer-test-" + Guid.NewGuid().ToString("N"));
            var cacheRoot = Path.Combine(temporary, "cache");
            var sourcePath = Path.Combine(temporary, "remote.bin");
            Directory.CreateDirectory(temporary);
            var bytes = Encoding.UTF8.GetBytes("verified remote VAO bytes\n");
            File.WriteAllBytes(sourcePath, bytes);
            var digest = string.Concat(SHA256.Create().ComputeHash(bytes).Select(value => value.ToString("x2")));
            var sourceUri = new Uri(sourcePath).AbsoluteUri;
            var package = ScriptableObject.CreateInstance<VaoPackageAsset>();
            package.SourceArchiveSha256 = new string('a', 64);
            package.LogicalAssets.Add(new VaoLogicalAssetRecord { Identifier = logicalId, Label = "Remote evidence", RealizationIdentifiers = new[] { realizationId } });
            var realization = new VaoRealizationRecord
            {
                Identifier = realizationId, LogicalAssetIdentifier = logicalId, MediaType = "application/octet-stream", ByteSize = bytes.Length, Sha256 = digest,
                DistributionIdentifiers = new[] { distributionId }, RightsIdentifiers = new[] { rightsId }
            };
            package.Realizations.Add(realization);
            package.Distributions.Add(new VaoDistributionRecord { Identifier = distributionId, Kind = "repository", RepositoryBindingIdentifier = repositoryId, Access = "restricted", TransportSha256 = digest });
            package.RepositoryBindings.Add(new VaoRepositoryBindingRecord { Identifier = repositoryId, RepositoryType = "test", ResolutionPolicy = "explicit-host-mapping" });
            package.Rights.Add(new VaoRightsRecord { Identifier = rightsId, AppliesToIdentifiers = new[] { realizationId }, Access = "restricted", Statement = "Authorized test access only.", Attribution = "Test author" });
            package.AssetGroups.Add(new VaoAssetGroupRecord { Identifier = "urn:vao:test:remote:group", RealizationIdentifiers = new[] { realizationId }, Evictable = true, CachePriority = 10 });

            var root = new GameObject("VAO runtime materializer test");
            var resolver = root.AddComponent<VaoExplicitRepositoryResolver>();
            resolver.Mappings.Add(new VaoRepositoryUriMapping { DistributionIdentifier = distributionId, DownloadUri = sourceUri, AllowedRedirectPrefix = sourceUri });
            var materializer = root.AddComponent<VaoRuntimeMaterializer>();
            materializer.Package = package;
            materializer.ResolverBehaviour = resolver;
            materializer.EnableRemoteAcquisition = true;
            materializer.AllowFileUris = true;
            materializer.CacheRoot = cacheRoot;
            try
            {
                var plan = materializer.CreatePlan(realizationId);
                Assert.That(plan.CanAcquire, Is.True, plan.Error);
                Assert.That(plan.RequiresRestrictedAccessConfirmation, Is.True);
                Assert.That(plan.RightsStatement, Does.Contain("Authorized test access"));

                var denied = materializer.AcquireAsync(realizationId, null);
                while (!denied.IsCompleted) yield return null;
                Assert.That(denied.Result.Status, Is.EqualTo(VaoMaterializationStatus.Denied));

                var insufficient = materializer.AcquireAsync(realizationId, VaoAcquisitionAuthorization.Approve(plan));
                while (!insufficient.IsCompleted) yield return null;
                Assert.That(insufficient.Result.Status, Is.EqualTo(VaoMaterializationStatus.Denied));

                var acquired = materializer.AcquireAsync(realizationId, VaoAcquisitionAuthorization.Approve(plan, true));
                while (!acquired.IsCompleted) yield return null;
                Assert.That(acquired.Result.Succeeded, Is.True, acquired.Result.Error);
                Assert.That(acquired.Result.FromCache, Is.False);
                Assert.That(File.ReadAllBytes(acquired.Result.LocalPath), Is.EqualTo(bytes));
                Assert.That(materializer.TryGetCachedPath(realizationId, out var cachedPath), Is.True);
                Assert.That(cachedPath, Is.EqualTo(acquired.Result.LocalPath));

                var cached = materializer.AcquireAsync(realizationId, null);
                while (!cached.IsCompleted) yield return null;
                Assert.That(cached.Result.Status, Is.EqualTo(VaoMaterializationStatus.AlreadyAvailable));
                Assert.That(cached.Result.FromCache, Is.True);

                File.WriteAllBytes(cachedPath, Enumerable.Repeat((byte)0x5a, bytes.Length).ToArray());
                Assert.That(materializer.TryGetCachedPath(realizationId, out _), Is.False, "A same-length cache corruption must fail SHA-256 verification and be evicted.");

                realization.IsMaterialized = false;
                realization.RuntimeUri = null;
                realization.Sha256 = new string('0', 64);
                var badPlan = materializer.CreatePlan(realizationId);
                var bad = materializer.AcquireAsync(realizationId, VaoAcquisitionAuthorization.Approve(badPlan, true));
                while (!bad.IsCompleted) yield return null;
                Assert.That(bad.Result.Status, Is.EqualTo(VaoMaterializationStatus.Failed));
                Assert.That(bad.Result.Error, Does.Contain("SHA-256"));
                Assert.That(materializer.TryGetCachedPath(realizationId, out _), Is.False);

                using var cancelledSource = new CancellationTokenSource();
                cancelledSource.Cancel();
                var cancelled = materializer.AcquireAsync(realizationId, VaoAcquisitionAuthorization.Approve(badPlan, true), cancelledSource.Token);
                while (!cancelled.IsCompleted) yield return null;
                Assert.That(cancelled.Result.Status, Is.EqualTo(VaoMaterializationStatus.Cancelled));
            }
            finally
            {
                Object.Destroy(root);
                Object.Destroy(package);
                if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
            }
            yield return null;
        }
    }

    public sealed class PlayModeAcousticRenderer : MonoBehaviour, IVaoAcousticRenderer, IVaoAcousticRendererCapabilities, IVaoSwitchableAcousticRenderer
    {
        public string Name;
        public int Priority;
        public string RendererName => Name;
        public int RendererPriority => Priority;
        public bool IsReady { get; private set; }
        public string LastError => null;
        public bool Prepare(VaoAcousticSceneRecord scene) { IsReady = scene != null; return IsReady; }
        public bool CanRender(VaoAcousticSceneRecord scene) => scene != null;
        public void SetSpatialContext(Transform emitter, Transform receiver) { }
        public void AttachVoice(GameObject voice, AudioSource source) { }
        public void DetachVoice(GameObject voice, AudioSource source) { }
    }
}

namespace MidiJack
{
    public static class MidiMaster
    {
        public static readonly float[] Keys = new float[128];
        public static readonly float[] Knobs = new float[128];
        public static float GetKey(int noteNumber) => Keys[noteNumber];
        public static float GetKnob(int number, float defaultValue = 0f) => Knobs[number];
        public static void Reset()
        {
            System.Array.Clear(Keys, 0, Keys.Length);
            System.Array.Clear(Knobs, 0, Knobs.Length);
        }
    }
}

namespace Minis
{
    public sealed class MidiDevice
    {
        private readonly MidiControl[] notes = Enumerable.Range(0, 128).Select(_ => new MidiControl()).ToArray();
        private readonly MidiControl[] controls = Enumerable.Range(0, 128).Select(_ => new MidiControl()).ToArray();
        public static MidiDevice current { get; set; }
        public MidiControl GetNote(int number) => notes[number];
        public MidiControl GetControl(int number) => controls[number];
    }

    public sealed class MidiControl
    {
        public float Value;
        public float ReadValue() => Value;
    }
}
