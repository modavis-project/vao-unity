# Publication identifiers

The release candidate records two distinct identifiers:

| Work | Identifier | State |
| --- | --- | --- |
| MODAVIS VAO Importer for Unity `0.6.0-rc.2` | `10.5281/zenodo.22134391` | Reserved; publish only after the GitHub release artifact has been verified |
| Virtual Acoustic Object Standard `0.4.0` | `10.5281/zenodo.22122774` | Published |
| Virtual Acoustic Object Standard `0.5.0` candidate | commit `d17b3f188fdf7fadd01ba025383e4feca8def935` | Pinned source snapshot; no publication DOI is claimed |

The software release DOI appears in `CITATION.cff`, `codemeta.json`, `PUBLICATION_IDENTIFIERS.json`, and the README. Zenodo assigns the DOI to its record; `.zenodo.json` therefore describes the deposit and its relationships without inventing a second identifier. The VAO Standard is related as the work on which the plugin is based.

`PUBLICATION_IDENTIFIERS.json` is the repository's explicit status record. Change `releaseDoiStatus` and the matching Zenodo archive-record status from `reserved` to `published` only after the record resolves publicly. Add a software concept DOI only if Zenodo assigns one; do not substitute the version DOI.

For a later software release, update the package version, release date, Git tag URL, version-specific DOI, citation metadata, CodeMeta, changelog, release notes, and status record together. `./Scripts/verify-release.sh` rejects inconsistent versions, dates, DOI values, and standard identifiers.
