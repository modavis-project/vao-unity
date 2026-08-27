using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace Modavis.Vao
{
    public enum VaoPresentationRole { Primary, Artwork, Caption, Model, Annotation, Explanation, Animation, Audio, Video, Document, Other }

    [Serializable]
    public sealed class VaoPresentationResolveOptions
    {
        public bool IncludeSharedEntityCompanions = true;
        [Range(1, 256)] public int MaximumCompanions = 64;
        public bool IncludeUnmaterialized = true;
    }

    [Serializable]
    public sealed class VaoPresentationItem
    {
        public string LogicalAssetIdentifier;
        public string Label;
        public VaoPresentationRole Role;
        public string[] DeclaredRoles = Array.Empty<string>();
        public string[] AboutEntityIdentifiers = Array.Empty<string>();
        public string[] RelationIdentifiers = Array.Empty<string>();
        public string[] RelationPredicates = Array.Empty<string>();
        public string RealizationIdentifier;
        public string MediaType;
        public string RuntimeUri;
        public bool IsMaterialized;
        public Object ImportedObject;
        public string RightsStatement;
        public string Attribution;
        public string Access;
    }

    [Serializable]
    public sealed class VaoPresentationBundle
    {
        public string PrimaryLogicalAssetIdentifier;
        public string Label;
        public List<VaoPresentationItem> Items = new();

        public VaoPresentationItem Primary => Items.FirstOrDefault(item => item.Role == VaoPresentationRole.Primary);
        public IEnumerable<VaoPresentationItem> Companions => Items.Where(item => item.Role != VaoPresentationRole.Primary);
        public IEnumerable<VaoPresentationItem> WithRole(VaoPresentationRole role) => Items.Where(item => item.Role == role);
        public VaoPresentationItem First(VaoPresentationRole role) => Items.FirstOrDefault(item => item.Role == role);
    }

    [Serializable] public sealed class VaoPresentationBundleEvent : UnityEvent<VaoPresentationBundle> { }

    /// <summary>Resolves a logical asset and its declared presentation companions without filename inference.</summary>
    public static class VaoPresentationResolver
    {
        private static readonly string[] PresentationPredicates =
        {
            "hasrepresentation", "isrepresentationof", "hasthumbnail", "thumbnail", "hasposter", "poster", "hasartwork", "artwork",
            "hascaption", "caption", "hasannotation", "annotation", "hastranscript", "transcript", "hasdescription", "description",
            "hasexplanation", "explanation", "accompanies", "companion", "depicts", "documentedby", "documentation",
            "drivesanimation", "targetsanimation", "hasmedia", "haspart"
        };

        private static readonly string[] PresentationRoleTokens =
        {
            "thumbnail", "poster", "artwork", "image", "photograph", "caption", "subtitle", "transcript", "annotation",
            "explanation", "description", "documentation", "readme", "three-dimensional-model", "spatial-model", "animation",
            "video", "recording", "performance", "spoken", "audio-program", "document"
        };

        private static readonly string[] SuppressedSharedRoleTokens = { "instrument-sample", "audio-master", "impulse-response", "source-evidence", "paradata-record" };

        public static VaoPresentationBundle Resolve(VaoPackageAsset package, string primaryLogicalAssetIdentifier, VaoPresentationResolveOptions options = null)
        {
            if (package == null) throw new ArgumentNullException(nameof(package));
            options ??= new VaoPresentationResolveOptions();
            var primary = package.FindLogicalAsset(primaryLogicalAssetIdentifier);
            if (primary == null) return null;

            var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
            foreach (var relation in package.Relations)
            {
                var local = LocalName(relation.Predicate);
                if (!IsPresentationPredicate(local)) continue;
                if (relation.SubjectIdentifier == primary.Identifier && package.FindLogicalAsset(relation.ObjectIdentifier) != null)
                    Add(candidates, relation.ObjectIdentifier, 100, relation);
                if (relation.ObjectIdentifier == primary.Identifier && package.FindLogicalAsset(relation.SubjectIdentifier) != null)
                    Add(candidates, relation.SubjectIdentifier, 95, relation);
            }

            var about = new HashSet<string>(primary.AboutEntityIdentifiers ?? Array.Empty<string>(), StringComparer.Ordinal);
            if (about.Count > 0)
            {
                foreach (var relation in package.Relations)
                {
                    var local = LocalName(relation.Predicate);
                    if (!IsPresentationPredicate(local)) continue;
                    if (about.Contains(relation.SubjectIdentifier) && package.FindLogicalAsset(relation.ObjectIdentifier) != null)
                        Add(candidates, relation.ObjectIdentifier, 85, relation);
                    if (about.Contains(relation.ObjectIdentifier) && package.FindLogicalAsset(relation.SubjectIdentifier) != null)
                        Add(candidates, relation.SubjectIdentifier, 80, relation);
                }
                if (options.IncludeSharedEntityCompanions)
                {
                    foreach (var logical in package.LogicalAssets)
                    {
                        if (logical.Identifier == primary.Identifier || !(logical.AboutEntityIdentifiers ?? Array.Empty<string>()).Any(about.Contains)) continue;
                        if (!HasPresentationRole(logical.Roles) || HasSuppressedSharedRole(logical.Roles)) continue;
                        Add(candidates, logical.Identifier, 50, null);
                    }
                }
            }

            // An explicitly linked animation may target a model through a second declared relation.
            foreach (var first in candidates.Values.ToArray())
                foreach (var relation in package.FindRelationsFrom(first.LogicalAssetIdentifier).Where(item => LocalName(item.Predicate) == "targetsanimation"))
                    if (package.FindLogicalAsset(relation.ObjectIdentifier) != null) Add(candidates, relation.ObjectIdentifier, first.Score - 1, relation);

            candidates.Remove(primary.Identifier);
            var bundle = new VaoPresentationBundle { PrimaryLogicalAssetIdentifier = primary.Identifier, Label = DisplayLabel(primary) };
            bundle.Items.Add(BuildItem(package, primary, VaoPresentationRole.Primary, Array.Empty<VaoRelationRecord>()));
            foreach (var candidate in candidates.Values.OrderByDescending(item => item.Score).ThenBy(item => item.LogicalAssetIdentifier, StringComparer.Ordinal).Take(Math.Max(1, options.MaximumCompanions)))
            {
                var logical = package.FindLogicalAsset(candidate.LogicalAssetIdentifier);
                if (logical == null) continue;
                var item = BuildItem(package, logical, Classify(logical, package.FindRealizationsForLogicalAsset(logical.Identifier)), candidate.Relations);
                if (options.IncludeUnmaterialized || item.IsMaterialized) bundle.Items.Add(item);
            }
            return bundle;
        }

        private static VaoPresentationItem BuildItem(VaoPackageAsset package, VaoLogicalAssetRecord logical, VaoPresentationRole role, IEnumerable<VaoRelationRecord> relations)
        {
            var realizations = package.FindRealizationsForLogicalAsset(logical.Identifier);
            var best = realizations.OrderByDescending(RealizationScore).ThenBy(item => item.Identifier, StringComparer.Ordinal).FirstOrDefault();
            var rights = best == null ? new List<VaoRightsRecord>() : package.FindRightsForRealization(best.Identifier);
            var relationArray = relations.Where(item => item != null).GroupBy(item => item.Identifier).Select(group => group.First()).ToArray();
            return new VaoPresentationItem
            {
                LogicalAssetIdentifier = logical.Identifier, Label = DisplayLabel(logical), Role = role,
                DeclaredRoles = logical.Roles ?? Array.Empty<string>(), AboutEntityIdentifiers = logical.AboutEntityIdentifiers ?? Array.Empty<string>(),
                RelationIdentifiers = relationArray.Select(item => item.Identifier).ToArray(), RelationPredicates = relationArray.Select(item => item.Predicate).ToArray(),
                RealizationIdentifier = best?.Identifier, MediaType = best?.MediaType, RuntimeUri = best?.RuntimeUri,
                IsMaterialized = best?.IsMaterialized == true, ImportedObject = best?.ImportedObject,
                RightsStatement = string.Join("\n", rights.Select(item => item.Statement).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct()),
                Attribution = string.Join("; ", rights.Select(item => item.Attribution).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct()),
                Access = rights.Select(item => item.Access).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))
            };
        }

        private static int RealizationScore(VaoRealizationRecord realization)
        {
            var score = realization.IsMaterialized ? 100 : 0;
            if (realization.ImportedObject != null) score += 60;
            if (!string.IsNullOrEmpty(realization.RuntimeUri)) score += 20;
            score += realization.QualityTier switch { "presentation" => 12, "access" => 10, "preservation" => 8, _ => 0 };
            return score;
        }

        private static VaoPresentationRole Classify(VaoLogicalAssetRecord logical, IReadOnlyList<VaoRealizationRecord> realizations)
        {
            var roles = string.Join(" ", logical.Roles ?? Array.Empty<string>()).ToLowerInvariant();
            if (ContainsAny(roles, "thumbnail", "poster", "artwork", "image", "photograph", "cover")) return VaoPresentationRole.Artwork;
            if (ContainsAny(roles, "caption", "subtitle", "transcript")) return VaoPresentationRole.Caption;
            if (ContainsAny(roles, "annotation", "paradata")) return VaoPresentationRole.Annotation;
            if (ContainsAny(roles, "three-dimensional-model", "spatial-model", "3d-model")) return VaoPresentationRole.Model;
            if (roles.Contains("animation", StringComparison.Ordinal)) return VaoPresentationRole.Animation;
            if (ContainsAny(roles, "explanation", "spoken", "description")) return VaoPresentationRole.Explanation;
            if (roles.Contains("video", StringComparison.Ordinal)) return VaoPresentationRole.Video;
            if (ContainsAny(roles, "recording", "performance", "audio-program")) return VaoPresentationRole.Audio;
            if (ContainsAny(roles, "document", "readme", "documentation")) return VaoPresentationRole.Document;
            var media = string.Join(" ", realizations.Select(item => item.MediaType)).ToLowerInvariant();
            if (media.Contains("image/", StringComparison.Ordinal)) return VaoPresentationRole.Artwork;
            if (media.Contains("model/", StringComparison.Ordinal)) return VaoPresentationRole.Model;
            if (media.Contains("video/", StringComparison.Ordinal)) return VaoPresentationRole.Video;
            if (media.Contains("audio/", StringComparison.Ordinal)) return VaoPresentationRole.Audio;
            if (media.Contains("text/", StringComparison.Ordinal) || media.Contains("application/pdf", StringComparison.Ordinal)) return VaoPresentationRole.Document;
            return VaoPresentationRole.Other;
        }

        private static void Add(IDictionary<string, Candidate> candidates, string id, int score, VaoRelationRecord relation)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (!candidates.TryGetValue(id, out var candidate)) candidates[id] = candidate = new Candidate { LogicalAssetIdentifier = id };
            candidate.Score = Math.Max(candidate.Score, score);
            if (relation != null && candidate.Relations.All(item => item.Identifier != relation.Identifier)) candidate.Relations.Add(relation);
        }

        private static bool HasPresentationRole(IEnumerable<string> roles) => roles != null && roles.Any(role => PresentationRoleTokens.Any(token => LocalName(role).Contains(token, StringComparison.Ordinal)));
        private static bool HasSuppressedSharedRole(IEnumerable<string> roles) => roles != null && roles.Any(role => SuppressedSharedRoleTokens.Any(token => LocalName(role).Contains(token, StringComparison.Ordinal)));
        private static bool IsPresentationPredicate(string local) => PresentationPredicates.Any(token => local == token || local.EndsWith(token, StringComparison.Ordinal));
        private static bool ContainsAny(string value, params string[] tokens) => tokens.Any(value.Contains);
        private static string DisplayLabel(VaoLogicalAssetRecord logical) => string.IsNullOrWhiteSpace(logical.Label) ? LocalName(logical.Identifier) : logical.Label;

        internal static string LocalName(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var index = Math.Max(value.LastIndexOf('#'), Math.Max(value.LastIndexOf('/'), value.LastIndexOf(':')));
            return (index >= 0 && index + 1 < value.Length ? value[(index + 1)..] : value).ToLowerInvariant();
        }

        private sealed class Candidate
        {
            public string LogicalAssetIdentifier;
            public int Score;
            public List<VaoRelationRecord> Relations = new();
        }
    }

    [DisallowMultipleComponent]
    public sealed partial class VaoPresentationSelector : MonoBehaviour, IVaoPackageConsumer
    {
        [SerializeField] private VaoPackageAsset package;
        [SerializeField] private VaoMediaPlayer mediaPlayer;
        [SerializeField] private VaoRuntimeMaterializer materializer;
        [SerializeField] private VaoPresentationResolveOptions options = new();
        [SerializeField] private bool selectMatchingMedia = true;
        [SerializeField] private bool followMediaSelection = true;
        [SerializeField] private VaoStringEvent selectionChanged = new();
        [SerializeField] private VaoPresentationBundleEvent bundleChanged = new();
        private VaoPresentationBundle current;
        private VaoRuntimeMaterializer subscribedMaterializer;
        private bool synchronizing;

        public VaoPackageAsset Package { get => package; set { package = value; if (current != null) SelectLogicalAsset(current.PrimaryLogicalAssetIdentifier); } }
        public VaoPresentationBundle Current => current;
        public VaoPresentationResolveOptions Options => options;
        public VaoStringEvent SelectionChanged => selectionChanged;
        public VaoPresentationBundleEvent BundleChanged => bundleChanged;
        public event Action<VaoPresentationBundle> BundleResolved;

        public void SetPackage(VaoPackageAsset value) => Package = value;
        private void Awake() => ResolveDependencies();
        private void OnEnable() { ResolveDependencies(); Subscribe(); }
        private void OnDisable() => Unsubscribe();
        private void LateUpdate()
        {
            if (!followMediaSelection || synchronizing || mediaPlayer?.SelectedEntry == null) return;
            var identifier = mediaPlayer.SelectedEntry.LogicalAssetIdentifier;
            if (current?.PrimaryLogicalAssetIdentifier != identifier) SelectLogicalAsset(identifier);
        }

        public bool SelectLogicalAsset(string identifier)
        {
            if (package == null) return false;
            var next = VaoPresentationResolver.Resolve(package, identifier, options);
            if (next == null) return false;
            current = next;
            ResolveDependencies();
            synchronizing = true;
            try { if (selectMatchingMedia && mediaPlayer != null && mediaPlayer.SelectedEntry?.LogicalAssetIdentifier != identifier) mediaPlayer.SelectLogicalAsset(identifier); }
            finally { synchronizing = false; }
            selectionChanged.Invoke(identifier);
            bundleChanged.Invoke(current);
            BundleResolved?.Invoke(current);
            return true;
        }

        public bool SelectMediaIndex(int index)
        {
            ResolveDependencies();
            if (mediaPlayer == null || !mediaPlayer.Select(index)) return false;
            return SelectLogicalAsset(mediaPlayer.SelectedEntry.LogicalAssetIdentifier);
        }

        public bool RequestCompanion(int itemIndex)
        {
            if (current == null || itemIndex < 0 || itemIndex >= current.Items.Count) return false;
            var realization = current.Items[itemIndex].RealizationIdentifier;
            if (string.IsNullOrEmpty(realization) || materializer == null) return false;
            var plan = materializer.CreatePlan(realization);
            if (!plan.CanAcquire) return false;
            materializer.RequestAcquisition(realization);
            return true;
        }

        public void Refresh()
        {
            if (current != null) SelectLogicalAsset(current.PrimaryLogicalAssetIdentifier);
        }

        private void ResolveDependencies()
        {
            if (mediaPlayer == null) mediaPlayer = GetComponent<VaoMediaPlayer>();
            if (materializer == null) materializer = GetComponent<VaoRuntimeMaterializer>();
            if (package == null) package = GetComponent<VaoRuntimeObject>()?.Package;
            Subscribe();
        }

        private void Subscribe()
        {
            if (subscribedMaterializer == materializer) return;
            Unsubscribe();
            subscribedMaterializer = materializer;
            if (subscribedMaterializer != null) subscribedMaterializer.Materialized += OnMaterialized;
        }

        private void Unsubscribe()
        {
            if (subscribedMaterializer != null) subscribedMaterializer.Materialized -= OnMaterialized;
            subscribedMaterializer = null;
        }

        private void OnMaterialized(VaoMaterializationResult result)
        {
            if (result.Succeeded && current?.Items.Any(item => item.RealizationIdentifier == result.RealizationIdentifier) == true) Refresh();
        }
    }

    /// <summary>Optional ready-made UI/model binding for a VaoPresentationSelector.</summary>
    [DisallowMultipleComponent]
    public sealed partial class VaoPresentationView : MonoBehaviour
    {
        [SerializeField] private VaoPresentationSelector selector;
        [SerializeField] private Component titleText;
        [SerializeField] private Component captionText;
        [SerializeField] private Component artworkImage;
        [SerializeField] private Renderer artworkRenderer;
        [SerializeField] private Transform modelRoot;
        [SerializeField] private bool instantiateModel = true;
        private GameObject modelInstance;

        private void Awake() { if (selector == null) selector = GetComponent<VaoPresentationSelector>(); }
        private void OnEnable() { if (selector != null) { selector.BundleResolved += Apply; if (selector.Current != null) Apply(selector.Current); } }
        private void OnDisable() { if (selector != null) selector.BundleResolved -= Apply; }

        public void Apply(VaoPresentationBundle bundle)
        {
            if (bundle == null) return;
            SetMember(titleText, "text", bundle.Label);
            var caption = bundle.First(VaoPresentationRole.Caption) ?? bundle.First(VaoPresentationRole.Explanation) ?? bundle.First(VaoPresentationRole.Annotation) ?? bundle.First(VaoPresentationRole.Document);
            SetMember(captionText, "text", caption?.ImportedObject is TextAsset text ? text.text : caption?.Label ?? string.Empty);
            var artwork = bundle.First(VaoPresentationRole.Artwork)?.ImportedObject;
            var texture = artwork switch { Texture value => value, Sprite sprite => sprite.texture, _ => null };
            SetMember(artworkImage, "texture", texture);
            if (artworkRenderer != null && texture != null) artworkRenderer.material.mainTexture = texture;
            if (!instantiateModel || modelRoot == null) return;
            if (modelInstance != null) Destroy(modelInstance);
            var model = bundle.First(VaoPresentationRole.Model);
            if (model?.ImportedObject is GameObject prefab)
            {
                modelInstance = Instantiate(prefab, modelRoot, false);
                modelInstance.name = prefab.name;
            }
            else if (model != null && !string.IsNullOrEmpty(model.RuntimeUri))
            {
                modelInstance = new GameObject(string.IsNullOrWhiteSpace(model.Label) ? "VAO presentation model" : model.Label);
                modelInstance.transform.SetParent(modelRoot, false);
                var loader = modelInstance.AddComponent<VaoGltfRuntimeLoader>();
                loader.RealizationIdentifier = model.RealizationIdentifier;
                loader.RuntimeUri = model.RuntimeUri;
                _ = loader.LoadAsync();
            }
        }

        private static void SetMember(Component target, string name, object value)
        {
            if (target == null) return;
            var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property?.CanWrite == true && (value == null || property.PropertyType.IsInstanceOfType(value))) property.SetValue(target, value);
        }

        private void OnDestroy() { if (modelInstance != null) Destroy(modelInstance); }
    }
}
