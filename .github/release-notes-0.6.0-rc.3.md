# MODAVIS VAO Importer for Unity 0.6.0-rc.3

This release candidate fixes scientific observation import for valid VAO results whose `value` is an interval rather than a scalar. Unity keeps the complete structured result in `ResultJson`, exposes `NumericValue` only for scalar numbers, and no longer rejects complete pipe-measurement datasets such as the Cuntz Positiv VAO.

The release retains the pinned VAO 0.5.0 candidate contract and the published VAO 0.4.0 reader introduced in the preceding candidates.

Install in Unity Package Manager with:

```text
https://github.com/modavis-project/vao-unity.git#v0.6.0-rc.3
```

The reserved Zenodo DOI for this software release is [10.5281/zenodo.22134391](https://doi.org/10.5281/zenodo.22134391). The standard itself is published as [DOI 10.5281/zenodo.22122774](https://doi.org/10.5281/zenodo.22122774).
