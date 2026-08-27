# Position-aware acoustic rendering

An imported prefab uses `VaoAcousticEnvironment` as its stable renderer boundary. The environment selects the highest-priority compatible `IVaoAcousticRenderer`, prepares the selected VAO audio scene, attaches every sampled voice, and can switch scenes or renderers while the application is running.

## Built-in renderer

`VaoConvolutionRenderer` provides partitioned, multichannel FIR convolution for:

- Unity-decodable PCM room impulse responses;
- measurement-mapped response points with declared source and receiver poses, channel selection, and fractional sample delay;
- AES69-SOFA `FIR` realizations decoded by the managed PureHDF reader during import or after verified runtime acquisition;
- nearest-response selection and local inverse-distance response interpolation;
- tracked emitter/listener direction for SOFA HRTF selection;
- resampling to Unity's output rate and cross-faded kernel changes.

The SOFA decoder accepts floating-point `Data.IR`, `Data.SamplingRate`, `SourcePosition`, and optional `Data.Delay`. Cartesian and spherical source positions are converted from the SOFA coordinate convention to Unity's left-handed Y-up coordinates. A single emitter is supported, while all receiver channels are retained. Non-FIR SOFA data and multi-emitter arrays are rejected explicitly rather than being rendered with changed meaning. Use a stereo Unity output layout for binaural two-receiver HRTFs.

The built-in renderer accepts acoustic runtime features declared as `disabled`, `metadata`, or `response-field`. Geometry-driven, learned, MPEG-I, or other specialized scenes remain available through the renderer interface; they are not mislabeled as built-in convolution.

## Spatial context and switching

Assign tracked objects through `EmitterAnchor` and `ReceiverAnchor`. For a head-tracked HRTF, the receiver is normally the XR camera or audio listener. For measurement response fields, positions are evaluated in the imported VAO root's Unity coordinate space.

```csharp
var environment = GetComponent<Modavis.Vao.VaoAcousticEnvironment>();
environment.EmitterAnchor = instrumentEmitter;
environment.ReceiverAnchor = xrCamera;
environment.SelectScene(0);
environment.SelectRenderer("VAO position-aware RIR/SOFA convolution");
// Or cycle through every compatible renderer installed on the prefab.
environment.SelectNextRenderer();
```

An external renderer implements `IVaoAcousticRenderer`. Implement `IVaoAcousticRendererCapabilities` to advertise priority, reject incompatible scenes, and receive spatial anchors. Implement `IVaoSwitchableAcousticRenderer` to release per-voice resources cleanly during a live switch. `RendererChanged` and `SceneChanged` expose the active choice to the host UI.

The scene's interpolation method, domain identifier, outside-domain policy, fallback identifiers, runtime features, and transition time remain typed on `VaoAcousticSceneRecord`. The built-in discrete response selector always has a nearest materialized response when one exists. A renderer that interprets a declared continuous domain is responsible for its exact clamp, reject, fade, or fallback boundary behavior.

## Runtime acquisition

Verified runtime acquisition can bind a previously unavailable PCM RIR or SOFA FIR without reimporting the prefab. The acquired bytes pass the existing authorization, rights, byte-count, SHA-256, redirect, and cache checks before decoding. The environment is then prepared again and new voices use the materialized response.
