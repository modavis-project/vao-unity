# Kinoorgel VAO 0.5.0 acceptance procedure

The Leipzig museum cinema-organ release is the acceptance dataset for the
pinned VAO 0.5.0 carrier-member design. It has no 3D realization. A conforming
Unity result therefore validates and imports its semantic, scientific,
physical, and interaction data without inventing a model or spatial scene.

Set `VAO_ARCHIVE` to the bootstrap `.vao` and run the batch validator against a
Unity project that installs this package:

```sh
VAO_ARCHIVE=/path/to/Kinoorgel-4013925-bootstrap-0.5.0-rc.1.vao \
  /path/to/Unity -batchmode -nographics -quit \
  -projectPath /path/to/project \
  -executeMethod Modavis.Vao.Editor.VaoBatchImport.Validate \
  -logFile kinoorgel-validation.log
```

The DOI-bound release candidate is expected to report:

- format `0.5.0`, carrier mode `bootstrap`;
- 36 embedded realizations and 7,663,150 verified payload bytes;
- 1,462 logical assets and 4,584 exact realizations;
- 5,766 scientific observations;
- 543 protocol bindings and three signal-transfer functions;
- 110 physical components and 75 physical state bindings.

Exercise typed compilation without copying audio payloads:

```sh
VAO_ARCHIVE=/path/to/Kinoorgel-4013925-bootstrap-0.5.0-rc.1.vao \
VAO_DESTINATION='Assets/Kinoorgel VAO QA' \
VAO_MATERIALIZATION_MODE=MetadataOnly \
VAO_MAX_BYTES=0 \
VAO_CREATE_PREFAB=false \
  /path/to/Unity -batchmode -nographics -quit \
  -projectPath /path/to/project \
  -executeMethod Modavis.Vao.Editor.VaoBatchImport.Run \
  -logFile kinoorgel-import.log
```

The resulting `VaoPackageAsset` retains all logical assets and realizations,
compiles the counts above, keeps all twelve source profile/registry sections as
lossless JSON, writes a VAO 0.5.0 materialization receipt, materializes zero
payload bytes, and creates no prefab. Audio or selected asset groups can be
materialized separately when a Unity application actually needs them.

These commands validate a local carrier. Testing repository range delivery and
carrier-member retrieval requires a published or otherwise authenticated
repository endpoint and belongs to the CLI/repository acceptance gate.
