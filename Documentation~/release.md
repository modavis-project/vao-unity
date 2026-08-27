# Release procedure

This repository contains release candidate `0.6.0-rc.1`. Repository visibility and Zenodo publication are separate maintainer decisions: a pushed tag can be tested and released while the repository and reserved deposit remain private or unpublished.

## Local release gate

1. Confirm the working tree contains only intended source changes.
2. Run Editor and Play Mode tests. Run the standalone and IL2CPP checks for the target platform.
3. Run `./Scripts/verify-release.sh` and inspect the generated `.tgz` and `.sha256` files under `dist/`.
4. Inspect `npm pack --dry-run --json` and confirm that tests, development scripts, CI files, generated output, credentials, and machine-local files are absent from the UPM package.
5. Verify that `package.json`, `CHANGELOG.md`, `CITATION.cff`, `codemeta.json`, `.zenodo.json`, `PUBLICATION_IDENTIFIERS.json`, and `.github/release-notes-0.6.0-rc.1.md` describe the same version and date.

## Publication gate

After the responsible maintainer approves the candidate:

1. Push the reviewed commit while the GitHub repository remains private and confirm the repository-quality workflow passes from a clean runner. The Unity workflow runs when `UNITY_TESTS_ENABLED` is `true` and the GameCI license secrets documented in `CONTRIBUTING.md` are configured; otherwise retain the local Editor, Play Mode, standalone, and IL2CPP results as the release evidence.
2. Decide whether to publish the release candidate or replace it with a stable release commit and tag.
3. Change repository visibility only as a separate, deliberate maintainer action.
4. Push the matching annotated tag. The release workflow rebuilds the artifact, checks the tag against `package.json`, and creates a prerelease when the version contains a prerelease suffix.
5. Download the GitHub artifact, verify its SHA-256, and deposit those exact bytes in the reserved Zenodo record `10.5281/zenodo.22134391`.
6. Publish the Zenodo record only after its title, creator, version, date, license, related standard, GitHub relation, and file digest have been checked. Then update the local identifier status to `published` in a subsequent release-maintenance commit.

Do not attach a development checkout, Unity `Library` output, credentials, logs, test results, or extracted/restricted carriers to either release.
