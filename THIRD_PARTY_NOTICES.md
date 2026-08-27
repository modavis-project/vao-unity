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
