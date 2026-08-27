# Contributing

Contributions are welcome through the repository's issue and pull-request workflow.

Before submitting a change:

1. Keep the runtime independent of any particular tracker, MIDI SDK, glTF loader, or proprietary audio renderer unless the integration remains optional.
2. Do not weaken pre-extraction validation, fixity checks, path safety, resource limits, or offline behavior.
3. Do not infer semantic behavior from package filenames or identifiers.
4. Add or update tests for behavior changes and run `./Scripts/run-unity-tests.sh` plus `./Scripts/run-unity-playmode-tests.sh`. Changes affecting conformance should be checked against the published [VAO 0.4.0 schemas and reference fixtures](https://github.com/modavis-project/vao-standard).
5. Run `./Scripts/verify-release.sh` for package or metadata changes.
6. Update `CHANGELOG.md` for user-visible changes.

Do not commit restricted VAO carriers, extracted payloads, Unity `Library` output, credentials, Unity licenses, tracking databases, or third-party application assets. Preserve the attribution and CC BY 4.0 license notice when updating vendored standard artifacts. By contributing, you agree that your original contribution is licensed under the repository's MIT license.
