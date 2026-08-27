# MODAVIS VAO Importer for Unity

Import, inspect, and run [Virtual Acoustic Object (VAO) 0.4.0](https://github.com/modavis-project/vao-standard) carriers in Unity. The package validates a `.vao` before extraction, creates a typed `VaoPackageAsset`, imports supported media, builds linked MIDI animations, and can generate a runtime prefab for desktop, mobile, and XR applications.

This is release candidate `0.6.0-rc.1`. It targets Unity 6 (`6000.0` or newer) and is tested with Unity `6000.5.9f1`.

## Install

For a local checkout:

1. Open **Window > Package Management > Package Manager** in Unity.
2. Click **+**, choose **Install package from disk**, and select this repository's `package.json`.

Once the repository is public, a tagged version can be installed with **Add package from git URL**:

```text
https://github.com/modavis-project/vao-unity.git#v0.6.0-rc.1
```

Pin a tag in production projects; do not depend on the moving `main` branch.

## Import a VAO

1. Open **Tools > MODAVIS > Import VAO 0.4.0…**.
2. Select a `.vao` file. Review validation errors or warnings before continuing.
3. Choose a destination below `Assets/` and a materialization mode.
4. Click **Import**.
5. Drag the generated prefab into a scene and enter Play Mode with the Unity toolbar's **Play** button.

Dropping a `.vao` directly into `Assets/` creates a lightweight `VaoArchiveAsset`. Its Inspector shows validation results and offers the same verified import workflow.

The importer creates the source descriptors, a typed package asset, a standards-compliant materialization receipt, selected and digest-verified payload assets, and—when enabled—a prefab and runtime controls. Use **Window > MODAVIS > VAO Content Browser** or the package asset Inspector to explore logical assets, realizations, relationships, profiles, rights, controls, presentations, acoustics, and materialization state.

For large carriers, choose **Runtime Required**, **Selected Asset Groups**, or **Metadata Only**. Dependencies of selected groups are included transitively. Unmaterialized realizations remain queryable and can later be acquired through the opt-in runtime materializer.

## What the runtime supports

- Sampled instruments with key/velocity mappings, variants, state-dependent selection, note release, and declared voice limits.
- MIDI 1.0, MIDI 2.0 UMP, Standard MIDI File playback, and generated note-linked Unity animation clips.
- Deterministic event scheduling, transitions, processes, routing, render bindings, PCG32/xoshiro256** random sources, and multimodal clock mapping.
- Animator/PlayableGraph animation layers, masks, fades, sequences, additive/override blending, and independently rooted targets.
- Media/program transport and presentation bundles containing declared artwork, captions, models, annotations, explanations, and animations.
- Spatial anchors, coordinate conversion, tracker-neutral placement, and optional AR/XR/MIDI/glTF adapters without hard SDK dependencies.
- Position-aware partitioned convolution for Unity-decodable PCM impulse responses and supported AES69-SOFA FIR data.
- Explicit-consent runtime acquisition with host-controlled URL resolution, rights display, size and SHA-256 checks, cancellation, and a bounded verified cache.

The package preserves unsupported profile data as JSON and exposes host extension points. It does not reinterpret geometry acoustics, learned fields, MPEG-I scenes, non-FIR or multi-emitter SOFA data, specialized actuators, or application-specific hardware. See the [support matrix](Documentation~/VAO-0.4.0-support.md) and [conformance and limits](Documentation~/conformance-and-limits.md) for the precise boundary.

## Minimal runtime example

```csharp
using Modavis.Vao;
using UnityEngine;

public sealed class VaoExample : MonoBehaviour
{
    [SerializeField] private VaoRuntimeObject vao;

    public void PlayMiddleC()
    {
        vao.GetComponent<VaoSamplePlayer>().NoteOn(60, 100);
    }

    public void ReleaseMiddleC()
    {
        vao.GetComponent<VaoSamplePlayer>().NoteOff(60);
    }
}
```

For live MIDI, forward messages through `VaoMidiRouter`:

```csharp
var midi = GetComponent<Modavis.Vao.VaoMidiRouter>();
midi.ProcessMidi1(0x90, 60, 100);                 // MIDI 1.0 note-on
midi.ProcessMidi2Ump(0x40903c00u, 0xffff0000u); // MIDI 2.0 note-on
```

## Reimport safely

Select a `VaoPackageAsset` and choose **Preview and reimport**. The preview validates the replacement first and reports graph, rights, payload, and materialization changes. Applying it stages a complete import, keeps stable package/prefab GUIDs and compatible prefab object IDs, preserves scene overrides and unmanaged host files, and restores the prior generated tree if verification fails.

## Optional integrations

Open **Tools > MODAVIS > Optional Integrations…** to detect or explicitly install supported Unity packages. glTFast, AR Foundation, XR Interaction Toolkit, Vuforia, Minis, and MidiJack remain optional. Nothing is installed automatically, and the core assembly has no compile-time dependency on these SDKs.

## Security model

Validation and Editor import are offline. Package-supplied code is never executed and external identifiers are never dereferenced during import. Before extraction, the reader checks the final VAO 0.4.0 schemas and semantic/profile rules, exact manifest/carrier binding, preservation closure, hashes, deterministic trace digests, strict JSON and numeric constraints, ZIP metadata, UTF-8 paths, traversal and portable-name collisions, entry counts, expanded sizes, and compression ratios.

Runtime downloads are disabled until a host provides an `IVaoRepositoryResolver`, enables acquisition, presents rights and access information, and obtains approval bound to the exact plan. See [runtime materialization](Documentation~/runtime-materialization.md) and [SECURITY.md](SECURITY.md).

## Documentation

- [VAO 0.4.0 support matrix](Documentation~/VAO-0.4.0-support.md)
- [Conformance roles and resource limits](Documentation~/conformance-and-limits.md)
- [Deterministic execution](Documentation~/deterministic-execution.md)
- [Position-aware acoustic rendering](Documentation~/acoustic-rendering.md)
- [Linked animation with Playables](Documentation~/linked-animation-playables.md)
- [Presentation bundles](Documentation~/presentation-bundles.md)
- [XR host integration](Documentation~/XR-host-integration.md)
- [Optional integrations](Documentation~/optional-integrations.md)
- [Runtime materialization](Documentation~/runtime-materialization.md)

## Development and verification

```sh
./Scripts/run-unity-tests.sh
./Scripts/run-unity-playmode-tests.sh
./Scripts/run-unity-standalone-tests.sh
./Scripts/run-unity-il2cpp-build.sh
./Scripts/verify-release.sh
```

The test suite includes the published VAO 0.4.0 minimal carrier and profile descriptors, exact schema snapshot hashes, RFC 8785 trace canonicalization, specified PCG32/xoshiro sequences, archive-security cases, importer lifecycle tests, Play Mode behavior, and player-build checks.

## Standard, citation, and license

The normative format is maintained in the [VAO Standard repository](https://github.com/modavis-project/vao-standard), published as [VAO Standard 0.4.0 (DOI 10.5281/zenodo.22122774)](https://doi.org/10.5281/zenodo.22122774). This repository vendors the final 0.4.0 schemas and selected official fixtures for offline validation and regression testing under CC BY 4.0; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

The reserved DOI for this software release is [10.5281/zenodo.22134391](https://doi.org/10.5281/zenodo.22134391). Until the Zenodo record is published, use the repository and version when citing it. Citation metadata is provided in [CITATION.cff](CITATION.cff).

Plugin code is licensed under the [MIT License](LICENSE).
