using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Modavis.Vao
{
    public enum VaoMaterializationStatus { Succeeded, AlreadyAvailable, Denied, Unresolvable, Cancelled, Failed }

    [Serializable]
    public sealed class VaoRepositoryUriMapping
    {
        public string DistributionIdentifier;
        public string DownloadUri;
        public string AllowedRedirectPrefix;
    }

    public interface IVaoRepositoryResolver
    {
        bool TryResolve(VaoPackageAsset package, VaoRealizationRecord realization, VaoDistributionRecord distribution, VaoRepositoryBindingRecord binding, out Uri downloadUri, out string error);
        bool IsUriAllowed(Uri uri);
    }

    /// <summary>Explicit host-authored mapping. It never derives URLs from package identifiers.</summary>
    public sealed class VaoExplicitRepositoryResolver : MonoBehaviour, IVaoRepositoryResolver
    {
        [SerializeField] private List<VaoRepositoryUriMapping> mappings = new();
        public IList<VaoRepositoryUriMapping> Mappings => mappings;

        public bool TryResolve(VaoPackageAsset package, VaoRealizationRecord realization, VaoDistributionRecord distribution, VaoRepositoryBindingRecord binding, out Uri downloadUri, out string error)
        {
            downloadUri = null; error = null;
            var mapping = mappings.FirstOrDefault(item => string.Equals(item.DistributionIdentifier, distribution?.Identifier, StringComparison.Ordinal));
            if (mapping == null) { error = $"No explicit URI mapping exists for distribution {distribution?.Identifier}."; return false; }
            if (!Uri.TryCreate(mapping.DownloadUri, UriKind.Absolute, out downloadUri) || !IsUriAllowed(downloadUri)) { error = $"The configured URI for {distribution.Identifier} is invalid or outside its allowed prefix."; return false; }
            return true;
        }

        public bool IsUriAllowed(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo)) return false;
            foreach (var mapping in mappings)
            {
                var configured = string.IsNullOrWhiteSpace(mapping.AllowedRedirectPrefix) ? mapping.DownloadUri : mapping.AllowedRedirectPrefix;
                if (!Uri.TryCreate(configured, UriKind.Absolute, out var scope) || !string.IsNullOrEmpty(scope.UserInfo)) continue;
                if (string.IsNullOrWhiteSpace(mapping.AllowedRedirectPrefix))
                {
                    if (Uri.Compare(uri, scope, UriComponents.AbsoluteUri, UriFormat.UriEscaped, StringComparison.Ordinal) == 0) return true;
                    continue;
                }
                if (!string.Equals(uri.Scheme, scope.Scheme, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(uri.IdnHost, scope.IdnHost, StringComparison.OrdinalIgnoreCase) || uri.Port != scope.Port) continue;
                var candidatePath = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/');
                var scopePath = scope.GetComponents(UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/');
                if (candidatePath == scopePath || candidatePath.StartsWith(scopePath + "/", StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }

    public sealed class VaoMaterializationPlan
    {
        public string RealizationIdentifier;
        public string LogicalAssetIdentifier;
        public string MediaType;
        public long ByteSize;
        public string Sha256;
        public string Access;
        public string RightsStatement;
        public string Attribution;
        public string License;
        public bool RequiresRestrictedAccessConfirmation;
        public string[] CandidateDistributionIdentifiers = Array.Empty<string>();
        public string AuthorizationToken;
        public string Error;
        public bool CanAcquire => string.IsNullOrEmpty(Error) && CandidateDistributionIdentifiers.Length > 0;
    }

    public sealed class VaoAcquisitionAuthorization
    {
        public string AuthorizationToken;
        public bool UserApproved;
        public bool RestrictedAccessConfirmed;
        public string ApprovedAtUtc;

        public static VaoAcquisitionAuthorization Approve(VaoMaterializationPlan plan, bool restrictedAccessConfirmed = false) => new()
        {
            AuthorizationToken = plan?.AuthorizationToken, UserApproved = true, RestrictedAccessConfirmed = restrictedAccessConfirmed, ApprovedAtUtc = DateTimeOffset.UtcNow.ToString("O")
        };
    }

    public sealed class VaoMaterializationResult
    {
        public VaoMaterializationStatus Status;
        public string RealizationIdentifier;
        public string LocalPath;
        public string Error;
        public long ByteSize;
        public bool FromCache;
        public bool Succeeded => Status is VaoMaterializationStatus.Succeeded or VaoMaterializationStatus.AlreadyAvailable;
    }

    [DisallowMultipleComponent]
    public sealed class VaoRuntimeMaterializer : MonoBehaviour, IVaoPackageConsumer
    {
        [SerializeField] private VaoPackageAsset package;
        [SerializeField] private MonoBehaviour resolverBehaviour;
        [SerializeField] private bool enableRemoteAcquisition;
        [SerializeField] private bool allowFileUris;
        [SerializeField] private bool allowInsecureHttp;
        [SerializeField, Min(1)] private long maximumCacheBytes = 2L * 1024 * 1024 * 1024;
        [SerializeField, Min(1)] private int requestTimeoutSeconds = 120;
        [SerializeField] private string cacheSubdirectory = "VAO/cache/v1";
        [SerializeField] private VaoStringEvent consentRequested = new();
        [SerializeField] private VaoStringEvent acquisitionCompleted = new();
        [SerializeField] private VaoStringEvent acquisitionFailed = new();

        private VaoRuntimeCache cache;
        private readonly SemaphoreSlim acquisitionGate = new(1, 1);
        private string cacheRootOverride;
        private VaoMaterializationPlan pendingPlan;
        private CancellationTokenSource pendingCancellation;

        public VaoPackageAsset Package { get => package; set => package = value; }
        public IVaoRepositoryResolver Resolver => resolverBehaviour as IVaoRepositoryResolver;
        public MonoBehaviour ResolverBehaviour { get => resolverBehaviour; set => resolverBehaviour = value; }
        public bool EnableRemoteAcquisition { get => enableRemoteAcquisition; set => enableRemoteAcquisition = value; }
        public bool AllowFileUris { get => allowFileUris; set => allowFileUris = value; }
        public bool AllowInsecureHttp { get => allowInsecureHttp; set => allowInsecureHttp = value; }
        public int RequestTimeoutSeconds { get => requestTimeoutSeconds; set => requestTimeoutSeconds = Math.Max(1, value); }
        public long MaximumCacheBytes { get => maximumCacheBytes; set { maximumCacheBytes = Math.Max(1L, value); EnsureCache(); cache.MaximumBytes = maximumCacheBytes; } }
        public string CacheRoot { get { EnsureCache(); return cache.Root; } set { cacheRootOverride = value; cache = null; EnsureCache(); } }
        public long CacheBytes { get { EnsureCache(); return cache.TotalBytes; } }
        public VaoMaterializationPlan PendingPlan => pendingPlan;
        public VaoStringEvent ConsentRequested => consentRequested;
        public VaoStringEvent AcquisitionCompleted => acquisitionCompleted;
        public VaoStringEvent AcquisitionFailed => acquisitionFailed;
        public event Action<VaoMaterializationResult> Materialized;

        public void SetPackage(VaoPackageAsset value) => Package = value;
        private void Awake() => EnsureCache();
        private void OnDestroy() { pendingCancellation?.Cancel(); pendingCancellation?.Dispose(); pendingCancellation = null; }

        public VaoMaterializationPlan CreatePlan(string realizationIdentifier)
        {
            var plan = new VaoMaterializationPlan { RealizationIdentifier = realizationIdentifier };
            var realization = package?.FindRealization(realizationIdentifier);
            if (realization == null) { plan.Error = "The realization is not declared by the package."; return plan; }
            plan.LogicalAssetIdentifier = realization.LogicalAssetIdentifier; plan.MediaType = realization.MediaType; plan.ByteSize = realization.ByteSize; plan.Sha256 = realization.Sha256;
            var rights = package.FindRightsForRealization(realizationIdentifier);
            plan.RightsStatement = string.Join("\n\n", rights.Select(item => item.Statement).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct());
            plan.Attribution = string.Join("; ", rights.Select(item => item.Attribution).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct());
            plan.License = string.Join("; ", rights.Select(item => item.License).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct());
            plan.RequiresRestrictedAccessConfirmation = rights.Count == 0 || rights.Any(item => item.Access is "restricted" or "unknown");
            if (rights.Count == 0) plan.RightsStatement = "No rights record is available; treat access as unknown.";
            if (realization.ByteSize < 0 || realization.Sha256?.Length != 64 || realization.Sha256.Any(character => !Uri.IsHexDigit(character)))
                plan.Error = "The realization does not declare a valid byte size and SHA-256 digest.";
            var distributions = package.FindDistributionsForRealization(realizationIdentifier).Where(item => item.Kind == "repository").ToList();
            if (string.IsNullOrEmpty(plan.Error))
            {
                if (realization.IsMaterialized || !string.IsNullOrEmpty(realization.RuntimeUri)) plan.Access = "local";
                else if (distributions.Count == 0) plan.Error = "No repository distribution is declared for this non-materialized realization.";
                else
                {
                    var usable = distributions.Where(item => item.Access is "public" or "restricted").ToList();
                    if (usable.Count == 0) plan.Error = "All declared distributions are embargoed or metadata-only.";
                    else
                    {
                        plan.Access = usable.Any(item => item.Access == "public") ? "public" : "restricted";
                        plan.RequiresRestrictedAccessConfirmation |= usable.All(item => item.Access == "restricted");
                        plan.CandidateDistributionIdentifiers = usable.Select(item => item.Identifier).ToArray();
                    }
                }
            }
            plan.AuthorizationToken = AuthorizationToken(package, realization, plan);
            return plan;
        }

        public void RequestAcquisition(string realizationIdentifier)
        {
            pendingPlan = CreatePlan(realizationIdentifier);
            if (!pendingPlan.CanAcquire) { var error = pendingPlan.Error; pendingPlan = null; acquisitionFailed.Invoke(error); return; }
            consentRequested.Invoke(realizationIdentifier);
        }

        public async void ApprovePending()
        {
            if (pendingPlan != null) await CompletePending(VaoAcquisitionAuthorization.Approve(pendingPlan, false));
        }

        public async void ApproveRestrictedPending()
        {
            if (pendingPlan != null) await CompletePending(VaoAcquisitionAuthorization.Approve(pendingPlan, true));
        }

        public void DenyPending()
        {
            pendingCancellation?.Cancel(); pendingCancellation?.Dispose(); pendingCancellation = null; pendingPlan = null;
        }

        private async Task CompletePending(VaoAcquisitionAuthorization authorization)
        {
            var plan = pendingPlan;
            pendingCancellation?.Cancel(); pendingCancellation?.Dispose(); pendingCancellation = new CancellationTokenSource();
            var result = await AcquireAsync(plan.RealizationIdentifier, authorization, pendingCancellation.Token);
            if (result.Succeeded) acquisitionCompleted.Invoke(result.RealizationIdentifier); else acquisitionFailed.Invoke(result.Error ?? result.Status.ToString());
            pendingPlan = null;
        }

        public async Task<VaoMaterializationResult> AcquireAsync(string realizationIdentifier, VaoAcquisitionAuthorization authorization, CancellationToken cancellationToken = default)
        {
            var plan = CreatePlan(realizationIdentifier);
            var result = new VaoMaterializationResult { RealizationIdentifier = realizationIdentifier, ByteSize = plan.ByteSize };
            if (package?.FindRealization(realizationIdentifier) is not { } realization) return Failure(result, VaoMaterializationStatus.Unresolvable, plan.Error);
            try { await acquisitionGate.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { return Failure(result, VaoMaterializationStatus.Cancelled, "Acquisition was cancelled."); }
            try
            {
                EnsureCache();
                if (cache.TryGet(realization.Sha256, realization.ByteSize, out var cachedPath))
                {
                    await BindAcquiredContent(realization, cachedPath, cancellationToken);
                    result.Status = VaoMaterializationStatus.AlreadyAvailable; result.LocalPath = cachedPath; result.FromCache = true; Publish(result); return result;
                }
                if (!enableRemoteAcquisition) return Failure(result, VaoMaterializationStatus.Denied, "Runtime acquisition is disabled by the host.");
                if (!plan.CanAcquire) return Failure(result, VaoMaterializationStatus.Unresolvable, plan.Error);
                if (authorization?.UserApproved != true || authorization.AuthorizationToken != plan.AuthorizationToken) return Failure(result, VaoMaterializationStatus.Denied, "A matching explicit user authorization is required.");
                if (plan.RequiresRestrictedAccessConfirmation && !authorization.RestrictedAccessConfirmed) return Failure(result, VaoMaterializationStatus.Denied, "Restricted or unknown rights require explicit access confirmation.");
                if (Resolver == null) return Failure(result, VaoMaterializationStatus.Unresolvable, "No host repository resolver is configured.");

                VaoDistributionRecord distribution = null; Uri uri = null; string resolutionError = null;
                foreach (var id in plan.CandidateDistributionIdentifiers)
                {
                    var candidate = package.Distributions.Find(item => item.Identifier == id);
                    var binding = package.FindRepositoryBinding(candidate?.RepositoryBindingIdentifier);
                    if (candidate != null && binding != null && Resolver.TryResolve(package, realization, candidate, binding, out uri, out resolutionError)) { distribution = candidate; break; }
                }
                if (distribution == null || uri == null) return Failure(result, VaoMaterializationStatus.Unresolvable, resolutionError ?? "No declared distribution could be resolved by the host.");
                if (!Allowed(uri) || !Resolver.IsUriAllowed(uri)) return Failure(result, VaoMaterializationStatus.Denied, "The resolved URI violates the configured transport policy.");

                var evictable = IsEvictable(realizationIdentifier, out var priority);
                if (!cache.Reserve(realization.ByteSize, realization.Sha256, priority)) return Failure(result, VaoMaterializationStatus.Failed, "The verified cache quota cannot accommodate this realization without evicting pinned content.");
                var destination = cache.PathFor(realization.Sha256, realization.MediaType);
                var temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
                try
                {
                    var download = await Download(uri, temporary, realization.ByteSize, cancellationToken);
                    if (!Resolver.IsUriAllowed(download.FinalUri) || !Allowed(download.FinalUri)) throw new InvalidDataException("A redirect left the resolver's explicitly allowed URI scope.");
                    if (download.ByteSize != realization.ByteSize) throw new InvalidDataException($"Downloaded {download.ByteSize} bytes; the realization declares {realization.ByteSize}.");
                    if (!string.Equals(download.Sha256, realization.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Downloaded bytes fail the realization SHA-256.");
                    if (!string.IsNullOrEmpty(distribution.TransportSha256) && !string.Equals(download.Sha256, distribution.TransportSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Downloaded bytes fail the distribution transport SHA-256.");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? cache.Root);
                    if (File.Exists(destination)) File.Delete(destination);
                    File.Move(temporary, destination);
                    cache.Commit(realization.Sha256, destination, realization.ByteSize, evictable, priority);
                    await BindAcquiredContent(realization, destination, cancellationToken);
                    result.Status = VaoMaterializationStatus.Succeeded; result.LocalPath = destination; Publish(result); return result;
                }
                catch (OperationCanceledException) { return Failure(result, VaoMaterializationStatus.Cancelled, "Acquisition was cancelled."); }
                catch (Exception exception) { return Failure(result, VaoMaterializationStatus.Failed, exception.Message); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            catch (OperationCanceledException) { return Failure(result, VaoMaterializationStatus.Cancelled, "Acquisition was cancelled."); }
            catch (Exception exception) { return Failure(result, VaoMaterializationStatus.Failed, exception.Message); }
            finally { acquisitionGate.Release(); }
        }

        public bool TryGetCachedPath(string realizationIdentifier, out string path)
        {
            path = null; var realization = package?.FindRealization(realizationIdentifier); EnsureCache();
            return realization != null && cache.TryGet(realization.Sha256, realization.ByteSize, out path);
        }

        public bool Evict(string realizationIdentifier) { var realization = package?.FindRealization(realizationIdentifier); EnsureCache(); return realization != null && cache.Evict(realization.Sha256, false); }
        public long ClearEvictableCache() { EnsureCache(); return cache.ClearEvictable(); }

        private async Task BindAcquiredContent(VaoRealizationRecord realization, string path, CancellationToken cancellationToken)
        {
            var uri = new Uri(path).AbsoluteUri;
            if (IsSofa(realization))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sofa = VaoSofaDecoder.Decode(path, package.FindLogicalAsset(realization.LogicalAssetIdentifier)?.Label + " SOFA");
                realization.ImportedObject = sofa;
                realization.RuntimeUri = uri;
                realization.IsMaterialized = true;
                foreach (var scene in package.AcousticScenes)
                {
                    if (scene.ResponseRealizationIdentifier == realization.Identifier) { scene.Sofa = sofa; scene.RuntimeUri = uri; }
                    foreach (var point in scene.ResponsePoints.Where(item => item.RealizationIdentifier == realization.Identifier)) point.Sofa = sofa;
                }
                foreach (var environment in GetComponents<VaoAcousticEnvironment>()) environment.Apply();
            }
            else if (realization.MediaType.StartsWith("audio/", StringComparison.Ordinal) && realization.MediaType is not "audio/midi" and not "audio/x-midi")
            {
                using var request = UnityWebRequestMultimedia.GetAudioClip(uri, AudioTypeFor(realization.MediaType));
                await Send(request, cancellationToken);
                if (request.result != UnityWebRequest.Result.Success) throw new InvalidDataException("Unity could not decode the acquired audio: " + request.error);
                var clip = DownloadHandlerAudioClip.GetContent(request); clip.name = package.FindLogicalAsset(realization.LogicalAssetIdentifier)?.Label ?? realization.Identifier;
                realization.ImportedObject = clip; realization.RuntimeUri = uri; realization.IsMaterialized = true;
                foreach (var binding in package.SampleBindings.Where(item => item.RealizationIdentifier == realization.Identifier)) { binding.Clip = clip; binding.RuntimeUri = uri; }
                foreach (var scene in package.AcousticScenes.Where(item => item.ResponseRealizationIdentifier == realization.Identifier)) { scene.ImpulseResponse = clip; scene.RuntimeUri = uri; }
                foreach (var scene in package.AcousticScenes)
                    foreach (var point in scene.ResponsePoints.Where(item => item.RealizationIdentifier == realization.Identifier)) point.ImpulseResponse = clip;
                foreach (var environment in GetComponents<VaoAcousticEnvironment>()) environment.Apply();
                foreach (var media in GetComponents<VaoMediaPlayer>()) media.RebuildCatalog();
            }
            else { realization.RuntimeUri = uri; realization.IsMaterialized = true; }
            foreach (var loader in GetComponentsInChildren<VaoGltfRuntimeLoader>(true).Where(item => item.RealizationIdentifier == realization.Identifier)) { loader.RuntimeUri = uri; await loader.LoadAsync(); }
        }

        private bool IsSofa(VaoRealizationRecord realization) => realization != null &&
            (realization.MediaType?.IndexOf("sofa", StringComparison.OrdinalIgnoreCase) >= 0
             || package.AcousticScenes.Any(scene => scene.ResponseEncoding == "AES69-SOFA" && (scene.ResponseRealizationIdentifier == realization.Identifier || scene.ResponsePoints.Any(point => point.RealizationIdentifier == realization.Identifier))));

        private bool IsEvictable(string realizationIdentifier, out int priority)
        {
            var groups = package.AssetGroups.Where(item => Array.IndexOf(item.RealizationIdentifiers, realizationIdentifier) >= 0).ToList();
            priority = groups.Count == 0 ? 0 : groups.Max(item => item.CachePriority);
            return groups.Count == 0 || groups.All(item => item.Evictable);
        }

        private bool Allowed(Uri uri) => uri != null && uri.IsAbsoluteUri && string.IsNullOrEmpty(uri.UserInfo) && (uri.Scheme == Uri.UriSchemeHttps || allowFileUris && uri.Scheme == Uri.UriSchemeFile || allowInsecureHttp && uri.Scheme == Uri.UriSchemeHttp);

        private async Task<VaoDownloadResult> Download(Uri uri, string path, long expectedBytes, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? cache.Root);
            var handler = new VaoBoundedDownloadHandler(path, expectedBytes);
            using var request = new UnityWebRequest(uri, UnityWebRequest.kHttpVerbGET, handler, null) { timeout = requestTimeoutSeconds, redirectLimit = 4 };
            try
            {
                await Send(request, cancellationToken);
                if (request.result != UnityWebRequest.Result.Success) throw new IOException(request.error ?? "Repository download failed.");
                if (!handler.Completed) throw new IOException(handler.Error ?? "Repository download did not complete safely.");
                return new VaoDownloadResult { FinalUri = new Uri(request.url), ByteSize = handler.ByteSize, Sha256 = handler.Sha256 };
            }
            finally { handler.Close(); }
        }

        private static async Task Send(UnityWebRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<bool>(); var operation = request.SendWebRequest(); operation.completed += _ => completion.TrySetResult(true);
            using var registration = cancellationToken.Register(() => { request.Abort(); completion.TrySetCanceled(cancellationToken); });
            await completion.Task; cancellationToken.ThrowIfCancellationRequested();
        }

        private static AudioType AudioTypeFor(string mediaType) => mediaType switch { "audio/mpeg" or "audio/mp3" => AudioType.MPEG, "audio/ogg" => AudioType.OGGVORBIS, "audio/aiff" or "audio/x-aiff" => AudioType.AIFF, _ => AudioType.WAV };

        private static string AuthorizationToken(VaoPackageAsset package, VaoRealizationRecord realization, VaoMaterializationPlan plan)
        {
            var canonical = string.Join("\n", package.SourceArchiveSha256, realization.Identifier, realization.Sha256, realization.ByteSize,
                plan.Access, plan.RequiresRestrictedAccessConfirmation, plan.RightsStatement, plan.Attribution, plan.License, string.Join("|", plan.CandidateDistributionIdentifiers));
            using var sha = SHA256.Create(); return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)).Select(value => value.ToString("x2")));
        }

        private void Publish(VaoMaterializationResult result)
        {
            try { Materialized?.Invoke(result); }
            catch (Exception exception) { Debug.LogException(exception, this); }
        }

        private VaoMaterializationResult Failure(VaoMaterializationResult result, VaoMaterializationStatus status, string error) { result.Status = status; result.Error = error; Publish(result); return result; }

        private void EnsureCache()
        {
            if (cache != null) return;
            var root = string.IsNullOrWhiteSpace(cacheRootOverride) ? Path.Combine(Application.persistentDataPath, cacheSubdirectory) : cacheRootOverride;
            cache = new VaoRuntimeCache(root, maximumCacheBytes);
        }
    }

    internal sealed class VaoDownloadResult { public Uri FinalUri; public long ByteSize; public string Sha256; }

    internal sealed class VaoBoundedDownloadHandler : DownloadHandlerScript
    {
        private readonly FileStream stream;
        private readonly SHA256 hash = SHA256.Create();
        private readonly long limit;
        private bool finalized;
        private bool disposed;
        public long ByteSize { get; private set; }
        public string Sha256 { get; private set; }
        public bool Completed { get; private set; }
        public string Error { get; private set; }

        public VaoBoundedDownloadHandler(string path, long maximumBytes) : base(new byte[64 * 1024]) { limit = maximumBytes; stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        protected override void ReceiveContentLengthHeader(ulong contentLength) { if (contentLength > (ulong)limit) Error = "Repository response exceeds the declared byte size."; }
        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength <= 0) return true;
            if (Error != null || ByteSize + dataLength > limit) { Error ??= "Repository response exceeds the declared byte size."; return false; }
            stream.Write(data, 0, dataLength); hash.TransformBlock(data, 0, dataLength, null, 0); ByteSize += dataLength; return true;
        }
        protected override void CompleteContent()
        {
            if (finalized) return;
            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0); Sha256 = string.Concat(hash.Hash.Select(value => value.ToString("x2"))); stream.Flush(true); stream.Dispose(); finalized = true; Completed = Error == null;
        }
        public void Close()
        {
            if (disposed) return;
            if (!finalized) stream.Dispose();
            hash.Dispose();
            disposed = true;
        }
    }

    [Serializable] internal sealed class VaoCacheIndex { public List<VaoCacheEntry> Entries = new(); }
    [Serializable] internal sealed class VaoCacheEntry { public string Sha256; public string RelativePath; public long ByteSize; public long LastAccessUtcTicks; public bool Evictable; public int Priority; }

    internal sealed class VaoRuntimeCache
    {
        private readonly string indexPath;
        private readonly VaoCacheIndex index;
        public string Root { get; }
        public long MaximumBytes { get; set; }
        public long TotalBytes => index.Entries.Where(ValidFile).Sum(item => item.ByteSize);

        public VaoRuntimeCache(string root, long maximumBytes)
        {
            Root = Path.GetFullPath(root); MaximumBytes = Math.Max(1L, maximumBytes); Directory.CreateDirectory(Root); indexPath = Path.Combine(Root, "index.json");
            foreach (var partial in Directory.EnumerateFiles(Root, "*.partial-*", SearchOption.TopDirectoryOnly)) try { File.Delete(partial); } catch { }
            var primary = LoadIndex(indexPath);
            var backup = primary == null ? LoadIndex(indexPath + ".bak") : null;
            index = primary ?? backup ?? new VaoCacheIndex();
            var canPersist = true;
            if (primary == null && backup != null && File.Exists(indexPath)) try { File.Delete(indexPath); } catch { canPersist = false; }
            index.Entries.RemoveAll(item => !ValidFile(item));
            if (canPersist) Save();
        }

        public string PathFor(string sha256, string mediaType)
        {
            var extension = mediaType switch { "audio/wav" or "audio/x-wav" => ".wav", "audio/mpeg" or "audio/mp3" => ".mp3", "audio/ogg" => ".ogg", "model/gltf-binary" => ".glb", "application/zip" => ".zip", "application/sofa" or "application/x-sofa" or "application/vnd.sofa" => ".sofa", _ => ".bin" };
            return Path.Combine(Root, sha256.ToLowerInvariant() + extension);
        }

        public bool TryGet(string sha256, long byteSize, out string path)
        {
            path = null; var item = index.Entries.FirstOrDefault(value => value.Sha256 == sha256 && value.ByteSize == byteSize && ValidFile(value)); if (item == null) return false;
            path = FullPath(item); if (!Verify(path, byteSize, sha256)) { Evict(sha256, true); path = null; return false; } item.LastAccessUtcTicks = DateTime.UtcNow.Ticks; Save(); return true;
        }

        public bool Reserve(long bytes, string incomingSha, int priority)
        {
            if (bytes > MaximumBytes) return false;
            while (TotalBytes + bytes > MaximumBytes)
            {
                var candidate = index.Entries.Where(item => item.Evictable && item.Sha256 != incomingSha && item.Priority <= priority && ValidFile(item)).OrderBy(item => item.Priority).ThenBy(item => item.LastAccessUtcTicks).FirstOrDefault();
                if (candidate == null) return false; Evict(candidate.Sha256, true);
            }
            return true;
        }

        public void Commit(string sha256, string path, long bytes, bool evictable, int priority)
        {
            index.Entries.RemoveAll(item => item.Sha256 == sha256); index.Entries.Add(new VaoCacheEntry { Sha256 = sha256, RelativePath = Path.GetFileName(path), ByteSize = bytes, Evictable = evictable, Priority = priority, LastAccessUtcTicks = DateTime.UtcNow.Ticks }); Save();
        }

        public bool Evict(string sha256, bool force)
        {
            var entries = index.Entries.Where(item => item.Sha256 == sha256 && (force || item.Evictable)).ToList(); if (entries.Count == 0) return false;
            foreach (var item in entries) { var path = FullPath(item); if (File.Exists(path)) File.Delete(path); index.Entries.Remove(item); } Save(); return true;
        }

        public long ClearEvictable() { var before = TotalBytes; foreach (var sha in index.Entries.Where(item => item.Evictable).Select(item => item.Sha256).Distinct().ToArray()) Evict(sha, true); return before - TotalBytes; }
        private bool ValidFile(VaoCacheEntry item) { if (item == null || string.IsNullOrEmpty(item.RelativePath) || item.RelativePath != Path.GetFileName(item.RelativePath)) return false; var path = FullPath(item); return File.Exists(path) && new FileInfo(path).Length == item.ByteSize; }
        private string FullPath(VaoCacheEntry item) => Path.Combine(Root, item.RelativePath);
        private static bool Verify(string path, long bytes, string digest) { if (!File.Exists(path) || new FileInfo(path).Length != bytes) return false; using var input = File.OpenRead(path); using var sha = SHA256.Create(); return string.Equals(string.Concat(sha.ComputeHash(input).Select(value => value.ToString("x2"))), digest, StringComparison.OrdinalIgnoreCase); }
        private static VaoCacheIndex LoadIndex(string path) { try { return File.Exists(path) ? JsonUtility.FromJson<VaoCacheIndex>(File.ReadAllText(path)) : null; } catch { return null; } }
        private void Save()
        {
            var temporary = indexPath + ".tmp"; var backup = indexPath + ".bak";
            File.WriteAllText(temporary, JsonUtility.ToJson(index, true));
            if (!File.Exists(indexPath)) { File.Move(temporary, indexPath); return; }
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(indexPath, backup);
            try { File.Move(temporary, indexPath); File.Delete(backup); }
            catch { if (File.Exists(indexPath)) File.Delete(indexPath); if (File.Exists(backup)) File.Move(backup, indexPath); throw; }
        }
    }
}
