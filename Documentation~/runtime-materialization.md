# Runtime materialization

Repository acquisition is opt-in. Imported identifiers are inert metadata: the plugin never turns an identifier into a URL, follows a package-supplied URL, or downloads anything until the host enables acquisition, supplies a resolver, and receives explicit user approval for the current verified plan.

## Host setup

Add `VaoExplicitRepositoryResolver` beside the imported prefab's `VaoRuntimeMaterializer`, then map each permitted distribution identifier to a host-controlled HTTPS URL. `AllowedRedirectPrefix` is optional; without it, redirects are restricted to the exact configured URL. With it, the scheme, canonical host, port, and path boundary must remain inside that scope.

```csharp
var resolver = gameObject.AddComponent<Modavis.Vao.VaoExplicitRepositoryResolver>();
resolver.Mappings.Add(new Modavis.Vao.VaoRepositoryUriMapping
{
    DistributionIdentifier = "urn:example:distribution:recording",
    DownloadUri = signedDownloadUrl,
    AllowedRedirectPrefix = "https://repository.example/files"
});

var materializer = GetComponent<Modavis.Vao.VaoRuntimeMaterializer>();
materializer.ResolverBehaviour = resolver;
materializer.EnableRemoteAcquisition = true;
materializer.MaximumCacheBytes = 2L * 1024 * 1024 * 1024;
```

A custom `IVaoRepositoryResolver` can exchange application credentials for a short-lived signed URL. Credentials should remain in the host; do not put them in a VAO, `VaoRepositoryUriMapping`, or a URI user-info field. HTTPS is required by default. File URLs and insecure HTTP have separate host-only switches intended for local tests and controlled development.

## Consent and acquisition

`CreatePlan` resolves the declared byte size, SHA-256, distribution choices, access class, rights statement, license, and attribution without contacting a repository. Present those fields before creating an authorization. Missing, restricted, or unknown rights require the additional confirmation flag.

```csharp
var plan = materializer.CreatePlan(realizationId);
if (!plan.CanAcquire)
    throw new InvalidOperationException(plan.Error);

// Present plan rights, attribution, access, and byte size to the user first.
var consent = await ShowAcquisitionConsent(plan);
if (!consent.Approved)
    return;
var authorization = Modavis.Vao.VaoAcquisitionAuthorization.Approve(
    plan,
    restrictedAccessConfirmed: consent.RestrictedAccessConfirmed);
var result = await materializer.AcquireAsync(realizationId, authorization, cancellationToken);
```

The authorization token binds the approval to the source archive, realization digest and size, access/rights presentation, and candidate distributions. Changing any of those requires a new plan and approval. `RequestAcquisition`, `ApprovePending`, `ApproveRestrictedPending`, and `DenyPending` provide a UnityEvent-friendly flow; the optional generated runtime control surface implements it directly.

## Verification and cache behavior

Responses are capped at the declared byte size while streaming. The final byte count, realization SHA-256, and any distribution transport SHA-256 must match before an atomic cache commit. Redirects are checked again at the final URI. Cancellation and failed transfers remove partial files.

Cached bytes are rehashed on every access. Same-length tampering is evicted. The cache enforces the host quota using declared asset-group pinning, priority, and least-recently-used order among eligible items. Interrupted partials are removed during recovery, and the JSON index uses a backup during replacement. `TryGetCachedPath`, `Evict`, and `ClearEvictableCache` expose explicit host controls.
