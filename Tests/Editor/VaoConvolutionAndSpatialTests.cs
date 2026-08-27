using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Modavis.Vao.Editor.Tests
{
    public sealed class VaoConvolutionAndSpatialTests
    {
        [Test]
        public void PartitionedConvolutionRendersCompleteImpulseAcrossBlocks()
        {
            var impulse = new[] { 0.5f, 0.25f, 0f, 0f, 0.125f, 0f };
            var kernel = VaoConvolutionKernel.Build(impulse, impulse.Length, 1, 4);
            var state = new VaoConvolutionState(kernel, 1);
            var first = new[] { 1f, 0f, 0f, 0f };
            var second = new float[4];
            Assert.That(state.ProcessInterleaved(first, 1, 1f, 0f), Is.True);
            Assert.That(state.ProcessInterleaved(second, 1, 1f, 0f), Is.True);
            Assert.That(first, Is.EqualTo(new[] { 0.5f, 0.25f, 0f, 0f }).Within(1e-5f));
            Assert.That(second, Is.EqualTo(new[] { 0.125f, 0f, 0f, 0f }).Within(1e-5f));
        }

        [Test]
        public void ZUpMeterPoseMapsToUnityYUpCoordinates()
        {
            var package = ScriptableObject.CreateInstance<VaoPackageAsset>();
            package.CoordinateFrames.Add(new VaoCoordinateFrameRecord { Identifier = "urn:frame", Unit = "http://qudt.org/vocab/unit/M", UpAxis = "+Z", Handedness = "right" });
            var pose = new VaoPoseRecord { CoordinateFrameIdentifier = "urn:frame", Position = new Vector3(2f, 3f, 4f), Scale = Vector3.one };
            try
            {
                var mapped = VaoSpatialMath.PoseToUnity(package, pose).MultiplyPoint3x4(Vector3.zero);
                Assert.That(mapped.x, Is.EqualTo(2f).Within(1e-5f));
                Assert.That(mapped.y, Is.EqualTo(4f).Within(1e-5f));
                Assert.That(mapped.z, Is.EqualTo(-3f).Within(1e-5f));
            }
            finally { Object.DestroyImmediate(package); }
        }

        [Test]
        public void ManagedSofaImporterDecodesFirPositionsAndBinauralChannels()
        {
            var folder = "Assets/QA/SOFA Import " + Guid.NewGuid().ToString("N");
            VaoSofaAsset sofa = null;
            try
            {
                Directory.CreateDirectory(Path.GetFullPath(Path.Combine(Application.dataPath, "..", folder)));
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                var result = new VaoImportResult();
                sofa = VaoSofaImporter.Import("Packages/org.modavis.vao-unity/Tests/Editor/Fixtures/Tiny-HRIR.sofa", folder, "Tiny", result);
                Assert.That(sofa.Convention, Is.EqualTo("SimpleFreeFieldHRIR"));
                Assert.That(sofa.MeasurementCount, Is.EqualTo(3));
                Assert.That(sofa.ReceiverCount, Is.EqualTo(2));
                Assert.That(sofa.FilterLength, Is.EqualTo(8));
                Assert.That(sofa.SampleRate, Is.EqualTo(48000));
                Assert.That(Vector3.Distance(sofa.GetSourcePosition(0), Vector3.forward), Is.LessThan(1e-5f));
                Assert.That(Vector3.Distance(sofa.GetSourcePosition(1), Vector3.left), Is.LessThan(1e-5f));

                var left = sofa.Select(Vector3.left, "nearest");
                Assert.That(left.Indices, Is.EqualTo(new[] { 1 }));
                var impulse = sofa.GetInterleavedResponse(left.Indices[0]);
                var kernel = VaoConvolutionKernel.FromInterleaved(impulse, sofa.FilterLength, sofa.ReceiverCount, sofa.SampleRate, 8, 48000, 0f);
                var state = new VaoConvolutionState(kernel, 2);
                var block = new float[16];
                block[0] = block[1] = 1f;
                Assert.That(state.ProcessInterleaved(block, 2, 1f, 0f), Is.True);
                Assert.That(block[0], Is.EqualTo(1f).Within(1e-5f));
                Assert.That(block[1], Is.EqualTo(0.2f).Within(1e-5f));

                var interpolated = sofa.Select((Vector3.forward + Vector3.left).normalized, "linear");
                Assert.That(interpolated.Count, Is.GreaterThan(1));
                Assert.That(interpolated.Weights.Sum(), Is.EqualTo(1f).Within(1e-5f));
                Assert.That(result.ImportedAssetPaths.Single(), Does.EndWith(".asset"));
            }
            finally
            {
                AssetDatabase.DeleteAsset(folder);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
        }

        [Test]
        public void AcousticEnvironmentSelectsAndSwitchesCompatibleRenderers()
        {
            var package = ScriptableObject.CreateInstance<VaoPackageAsset>();
            package.AcousticScenes.Add(new VaoAcousticSceneRecord { Identifier = "urn:scene", ResponseSetIdentifier = "urn:response" });
            var root = new GameObject("renderer switch test");
            try
            {
                var low = root.AddComponent<TestAcousticRenderer>(); low.Name = "low"; low.Priority = 1;
                var high = root.AddComponent<TestAcousticRenderer>(); high.Name = "high"; high.Priority = 10;
                var environment = root.AddComponent<VaoAcousticEnvironment>(); environment.Package = package;
                Assert.That(environment.Renderer.RendererName, Is.EqualTo("high"));
                Assert.That(environment.SelectRenderer("low"), Is.True);
                Assert.That(environment.Renderer.RendererName, Is.EqualTo("low"));
                Assert.That(environment.SelectNextRenderer(), Is.True);
                Assert.That(environment.Renderer.RendererName, Is.EqualTo("high"));
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(package);
            }
        }
    }

    public sealed class TestAcousticRenderer : MonoBehaviour, IVaoAcousticRenderer, IVaoAcousticRendererCapabilities, IVaoSwitchableAcousticRenderer
    {
        public string Name;
        public int Priority;
        public string RendererName => Name;
        public int RendererPriority => Priority;
        public bool IsReady { get; private set; }
        public string LastError => null;
        public bool CanRender(VaoAcousticSceneRecord scene) => true;
        public bool Prepare(VaoAcousticSceneRecord scene) { IsReady = true; return true; }
        public void SetSpatialContext(Transform emitter, Transform receiver) { }
        public void AttachVoice(GameObject voice, AudioSource source) { }
        public void DetachVoice(GameObject voice, AudioSource source) { }
    }
}
