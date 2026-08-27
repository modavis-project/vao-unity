using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Modavis.Vao.Editor.Tests
{
    public sealed class VaoPresentationResolverTests
    {
        [Test]
        public void ResolverBuildsDeclaredCompanionBundleWithoutPullingInstrumentSamples()
        {
            const string entity = "urn:test:instrument";
            var package = ScriptableObject.CreateInstance<VaoPackageAsset>();
            var texture = new Texture2D(2, 2);
            var caption = new TextAsset("A declared caption.");
            var model = new GameObject("Presentation model");
            try
            {
                AddLogical(package, "urn:asset:program", "Program", entity, "performance-recording");
                AddLogical(package, "urn:asset:artwork", "Disc artwork", entity, "poster-image");
                AddLogical(package, "urn:asset:caption", "Caption", entity, "caption");
                AddLogical(package, "urn:asset:model", "Instrument model", entity, "three-dimensional-model");
                AddLogical(package, "urn:asset:sample", "C4 sample", entity, "instrument-sample", "audio-master");
                AddLogical(package, "urn:asset:unrelated", "Unrelated", "urn:test:other", "poster-image");
                package.Relations.Add(new VaoRelationRecord { Identifier = "urn:relation:artwork", SubjectIdentifier = "urn:asset:program", Predicate = "https://example.org/hasArtwork", ObjectIdentifier = "urn:asset:artwork" });
                package.Relations.Add(new VaoRelationRecord { Identifier = "urn:relation:model", SubjectIdentifier = entity, Predicate = "https://w3id.org/modavis/vao/ontology#hasRepresentation", ObjectIdentifier = "urn:asset:model" });
                AddRealization(package, "urn:asset:program", "audio/wav", null, true);
                AddRealization(package, "urn:asset:artwork", "image/png", texture, true, "restricted", "Museum access", "Museum");
                AddRealization(package, "urn:asset:caption", "text/plain", caption, true);
                AddRealization(package, "urn:asset:model", "model/gltf-binary", model, true);
                AddRealization(package, "urn:asset:sample", "audio/wav", null, true);

                var bundle = VaoPresentationResolver.Resolve(package, "urn:asset:program");

                Assert.That(bundle, Is.Not.Null);
                Assert.That(bundle.Primary.LogicalAssetIdentifier, Is.EqualTo("urn:asset:program"));
                Assert.That(bundle.First(VaoPresentationRole.Artwork)?.ImportedObject, Is.EqualTo(texture));
                Assert.That(bundle.First(VaoPresentationRole.Caption)?.ImportedObject, Is.EqualTo(caption));
                Assert.That(bundle.First(VaoPresentationRole.Model)?.ImportedObject, Is.EqualTo(model));
                Assert.That(bundle.Items.Any(item => item.LogicalAssetIdentifier == "urn:asset:sample"), Is.False);
                Assert.That(bundle.Items.Any(item => item.LogicalAssetIdentifier == "urn:asset:unrelated"), Is.False);
                var artwork = bundle.First(VaoPresentationRole.Artwork);
                Assert.That(artwork.RelationIdentifiers, Does.Contain("urn:relation:artwork"));
                Assert.That(artwork.Access, Is.EqualTo("restricted"));
                Assert.That(artwork.RightsStatement, Does.Contain("Museum access"));
                Assert.That(artwork.Attribution, Is.EqualTo("Museum"));
            }
            finally
            {
                Object.DestroyImmediate(model);
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(package);
            }
        }

        [Test]
        public void ResolverCanExcludeSharedAndUnmaterializedCompanions()
        {
            var package = ScriptableObject.CreateInstance<VaoPackageAsset>();
            try
            {
                AddLogical(package, "urn:asset:primary", "Primary", "urn:entity", "performance-recording");
                AddLogical(package, "urn:asset:shared", "Shared caption", "urn:entity", "caption");
                AddLogical(package, "urn:asset:explicit", "Explicit poster", "urn:other", "poster");
                package.Relations.Add(new VaoRelationRecord { Identifier = "urn:relation:poster", SubjectIdentifier = "urn:asset:primary", Predicate = "urn:predicate:hasPoster", ObjectIdentifier = "urn:asset:explicit" });
                AddRealization(package, "urn:asset:primary", "audio/wav", null, true);
                AddRealization(package, "urn:asset:shared", "text/plain", null, true);
                AddRealization(package, "urn:asset:explicit", "image/png", null, false);

                var bundle = VaoPresentationResolver.Resolve(package, "urn:asset:primary", new VaoPresentationResolveOptions { IncludeSharedEntityCompanions = false, IncludeUnmaterialized = false });

                Assert.That(bundle.Items, Has.Count.EqualTo(1));
                Assert.That(bundle.PrimaryLogicalAssetIdentifier, Is.EqualTo("urn:asset:primary"));
            }
            finally { Object.DestroyImmediate(package); }
        }

        [Test]
        public void TrackingAdapterNormalizesArFoundationStyleTrackingStates()
        {
            var probe = new TrackingProbe { trackingState = TrackingProbeState.Tracking };
            Assert.That(VaoTrackingSdkAdapter.EvaluateTrackingState(probe, false), Is.True);
            probe.trackingState = TrackingProbeState.Limited;
            Assert.That(VaoTrackingSdkAdapter.EvaluateTrackingState(probe, false), Is.False);
            Assert.That(VaoTrackingSdkAdapter.EvaluateTrackingState(probe, true), Is.True);
            probe.trackingState = TrackingProbeState.None;
            Assert.That(VaoTrackingSdkAdapter.EvaluateTrackingState(probe, true), Is.False);
        }

        private static void AddLogical(VaoPackageAsset package, string id, string label, string entity, params string[] roles)
            => package.LogicalAssets.Add(new VaoLogicalAssetRecord { Identifier = id, Label = label, AboutEntityIdentifiers = new[] { entity }, Roles = roles });

        private static void AddRealization(VaoPackageAsset package, string logicalId, string mediaType, Object imported, bool materialized, string access = null, string statement = null, string attribution = null)
        {
            var realizationId = logicalId + ":realization";
            var rightsId = logicalId + ":rights";
            package.Realizations.Add(new VaoRealizationRecord
            {
                Identifier = realizationId, LogicalAssetIdentifier = logicalId, MediaType = mediaType,
                ImportedObject = imported, IsMaterialized = materialized, RuntimeUri = materialized ? "file:///test" : null,
                RightsIdentifiers = string.IsNullOrEmpty(access) ? System.Array.Empty<string>() : new[] { rightsId }
            });
            package.FindLogicalAsset(logicalId).RealizationIdentifiers = new[] { realizationId };
            if (!string.IsNullOrEmpty(access)) package.Rights.Add(new VaoRightsRecord { Identifier = rightsId, Access = access, Statement = statement, Attribution = attribution });
        }


        private enum TrackingProbeState { None, Limited, Tracking }
        private sealed class TrackingProbe { public TrackingProbeState trackingState { get; set; } }
    }
}
