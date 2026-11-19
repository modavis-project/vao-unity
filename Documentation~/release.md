# Release procedure

Version 0.6.0-rc.4 is a Unity Package Manager prerelease.

1. Run the Editor and Play Mode tests. Run the standalone and IL2CPP checks
   for the target player platform.
2. Run `./Scripts/verify-release.sh`. Check the package inventory and SHA-256
   under `dist/`.
3. Check the version and date in the package, changelog, citation metadata,
   CodeMeta, publication identifiers, and GitHub release notes.
4. Push the reviewed commit and its annotated version tag. The tag workflow
   builds the package and attaches it to a draft GitHub prerelease.
5. Download the draft package and compare its digest with the inspected build
   before publishing. Check both public downloads after publication.

Use a separate workflow for an archival deposit. Keep test results,
development logs, and Unity project caches out of the UPM package.
