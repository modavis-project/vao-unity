# Declarative presentation bundles

`VaoPresentationResolver` turns one primary logical asset—normally a recording or program—into a `VaoPresentationBundle`. The bundle contains the primary item and its declared artwork, caption, model, annotation, explanation, animation, audio, video, or document companions. Every item carries logical/realization identifiers, roles, relation provenance, media type, imported object or runtime URI, availability, access, rights statement, and attribution.

Resolution follows declared presentation relations and, when enabled, compatible logical assets about the same entity. It never uses filenames. Broad shared instrument samples, masters, impulse responses, source evidence, and paradata are suppressed so choosing a program does not accidentally expand into a complete preservation graph.

Use `VaoPackageAsset.ResolvePresentation(logicalAssetIdentifier)` for direct queries. Imported prefabs also contain `VaoPresentationSelector`, which follows `VaoMediaPlayer`, emits `BundleChanged`/`BundleResolved`, and refreshes a bundle after successful runtime materialization. `RequestCompanion` starts the normal rights-aware acquisition workflow; it does not bypass host consent.

`VaoPresentationView` is an optional lightweight binder. It can set title/caption components that expose a public `text` property, artwork components that expose `texture`, a renderer material, and a model root. Native model prefabs are instantiated; GLB runtime URIs use `VaoGltfRuntimeLoader` and therefore require glTFast. Bespoke XR applications can ignore the view and bind directly to bundle events.

The VAO Content Browser's Presentations tab shows the resolved companions for each logical asset. The generated runtime control surface also shows companions, rights/access state, and an Acquire action where applicable.
