# Third-party notices

The package declares Unity modules and Unity's Newtonsoft JSON package as dependencies. Unity Package Manager resolves those packages separately; their own licenses and notices apply.

PureHDF 1.0.1 is vendored as `Runtime/ThirdParty/PureHDF.dll` under the MIT License to provide managed HDF5 decoding for supported AES69-SOFA FIR data in the Unity Editor and player. Its complete notice, including the C-Blosc notice carried by PureHDF, is included at `Editor/ThirdParty/PureHDF-LICENSE.md`.

The following files are copied from version 0.4.0 of the [Virtual Acoustic Object (VAO) Standard](https://github.com/modavis-project/vao-standard), edited by Dominik Ukolov and published as [DOI 10.5281/zenodo.22122774](https://doi.org/10.5281/zenodo.22122774):

- `Editor/Schemas/vao-manifest-0.4.0.schema.json`
- `Editor/Schemas/vao-carrier-0.4.0.schema.json`
- `Editor/Schemas/vao-materialization-receipt-0.4.0.schema.json`
- `Tests/Editor/Fixtures/VAO-Standard-Minimal-0.4.0.vao`
- `Tests/Editor/Fixtures/VAO-Standard-Cuntz-Positiv-0.4.0.json`
- `Tests/Editor/Fixtures/VAO-Standard-Kinoorgel-0.4.0.json`

Those standard artifacts are licensed under [Creative Commons Attribution 4.0 International](Editor/Schemas/VAO-STANDARD-CC-BY-4.0.txt). They are redistributed unmodified for offline validation and regression testing. The standard repository, not this plugin repository, is the authoritative source. Plugin code and original documentation remain under this repository's MIT License.

The following unmodified candidate artifacts are copied from
`modavis-project/vao-standard` commit
`d17b3f188fdf7fadd01ba025383e4feca8def935`:

- `Editor/Schemas/vao-manifest-0.5.0.schema.json`
- `Editor/Schemas/vao-carrier-0.5.0.schema.json`
- `Editor/Schemas/vao-materialization-receipt-0.5.0.schema.json`
- `Tests/Editor/Fixtures/VAO-Standard-Minimal-0.5.0.vao`

The snapshot is pinned by the normative-bundle SHA-256 recorded in
`Editor/Schemas/VAO-STANDARD-0.5.0-CANDIDATE.txt`. These files use the same CC
BY 4.0 license, but their presence does not imply that VAO 0.5.0 has already
been published as a final standard.

## VAO 0.6.0

The three 0.6.0 schemas in `Editor/Schemas/` come unchanged from
[VAO Standard v0.6.0](https://github.com/modavis-project/vao-standard/releases/tag/v0.6.0),
commit `9d477213eab90defa75bef0f44e0d292a83e68d9`. Copyright Dominik Ukolov
and contributors. The schemas are [CC BY 4.0](Editor/Schemas/VAO-STANDARD-CC-BY-4.0.txt).
Their hashes are recorded in `Editor/Schemas/VAO-STANDARD-0.6.0.txt`.

`Tests/Editor/Fixtures/VAO-Standard-Minimal-0.6.0.vao` is a derivative of
the released 0.5.0 minimal example, built with the upstream migrator and packer
under CC BY 4.0. The 69 payload bytes retain their original digest.
`VAO-MMIC-Design-1.0.0.vao` is the upstream design-only preservation carrier.
Its synthetic data are [CC0 1.0](Editor/Schemas/VAO-MMIC-CC0-1.0.txt), with
Apache-2.0 exceptions for the embedded reference code and lock file as recorded
in the carrier.
