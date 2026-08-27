# Conformance roles and limits

This package is an implementation of VAO 0.4.0 for Unity, not the normative standard. The [VAO Standard repository](https://github.com/modavis-project/vao-standard) and its [versioned publication](https://doi.org/10.5281/zenodo.22122774) define conformance.

## Implemented roles

| Role | Behavior |
| --- | --- |
| Carrier validator and reader | Validates the three vendored final schemas used by the plugin, strict JSON/numeric rules, carrier structure, semantic references, profile truth, closure, paths, ZIP metadata, and fixity before extraction |
| Carrier extractor | Extracts only carrier-mapped, selected realizations into a bounded staging location after successful validation |
| Materializer/importer | Resolves selected groups, compiles typed Unity records, imports supported media, creates optional prefab assets, and writes a final-schema materialization receipt |
| Runtime repository materializer | Acquires declared distributions only through a host resolver and approval flow, then checks size and SHA-256 before committing to a bounded cache |
| Profile processor | Executes the built-in core, dynamic-delivery, playable, deterministic-runtime, multimodal, spatial, and acoustics capabilities described in the support matrix; other content remains inspectable for a host extension |

The schema evaluator is intentionally scoped to the keywords used by the vendored VAO manifest, carrier, and receipt schemas. It is not advertised as a general-purpose Draft 2020-12 JSON Schema library. Scientific truth, acoustic adequacy, rights validity, and historical interpretation are outside machine conformance.

## Default validation limits

| Limit | Default |
| --- | ---: |
| ZIP entries | 100,000 |
| Path segments per entry | 128 |
| JSON nesting depth | 128 |
| Manifest or carrier descriptor | 32 MiB |
| Expanded size per entry | 16 GiB |
| Total expanded carrier size | 64 GiB |
| Compression ratio per entry | 200:1 |
| Materialized bytes per import | 16 GiB |

`VaoValidationPolicy` exposes the validation limits to programmatic callers. `VaoImportOptions.MaximumMaterializedBytes` controls the import budget. Tighten these values for constrained build agents or applications. Increasing them may increase memory, storage, and processing exposure.

Only Stored and Deflate ZIP entries are accepted. Encryption, multi-disk archives, symbolic links and other special file types, invalid UTF-8 names, traversal/absolute paths, ASCII controls, duplicate names, NFC collisions, and portable case-fold collisions are rejected. JSON must use valid UTF-8, paired Unicode scalars, finite binary64 numbers, interoperable integers in `-(2^53-1)..2^53-1`, and non-underflowing nonzero numeric values.

## Deterministic execution boundary

The runtime implements the specified `pcg32` and `xoshiro256-star-star` initialization and word sequences, uniform and integer-weight categorical selection with high-tail rejection, RFC 8785 trace canonicalization, exact rational timebase retention, event ordering, snapshot transition evaluation, grouped action order, and declared microstep/voice limits. Host-defined render policies and operations that require a device, specialized renderer, or actuator are surfaced as typed events; they are not silently simulated.

Every generated receipt identifies this package version and hashes the importer source file used by the Editor assembly with identity scope `source-file`. Embedded payload bytes are recorded as source-carrier evidence rather than repository acquisitions.
