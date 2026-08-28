# MODAVIS VAO Importer for Unity 0.6.0-rc.2

This release candidate adds the pinned VAO 0.5.0 candidate contract and Kinoorgel scientific/physical-signal inspection while retaining the published [Virtual Acoustic Object Standard 0.4.0](https://github.com/modavis-project/vao-standard) reader.

Highlights:

- version-dispatched offline validation of VAO 0.4.0 and pinned 0.5.0 manifests, carriers, and materialization receipts before extraction;
- typed carrier-member distributions, scientific observations, physical components and state bindings, protocol evidence, and signal-transfer functions;
- full-metadata, runtime-required, selected-group, and metadata-only import modes;
- transactional reimport with preview, stable Unity identities, and rollback;
- sampled instruments, MIDI 1.0/2.0, SMF playback, and linked Playables animation;
- deterministic events, processes, routing, rendering choices, random sources, and multimodal clocks;
- media presentations, tracker-neutral XR placement, and optional host SDK adapters;
- position-aware PCM RIR and supported AES69-SOFA FIR convolution;
- consent-gated, rights-aware runtime acquisition with verified caching.

Install in Unity Package Manager with:

```text
https://github.com/modavis-project/vao-unity.git#v0.6.0-rc.2
```

The attached `.tgz` is the verified UPM package; the adjacent `.sha256` file records its digest. The reserved Zenodo DOI for this software release is [10.5281/zenodo.22134391](https://doi.org/10.5281/zenodo.22134391). The standard itself is published as [DOI 10.5281/zenodo.22122774](https://doi.org/10.5281/zenodo.22122774).

Please report reproducible defects through GitHub Issues and follow `SECURITY.md` for vulnerabilities.
