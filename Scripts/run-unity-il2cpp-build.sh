#!/bin/sh
set -eu

UNITY_EDITOR="${UNITY_EDITOR:-/Applications/Unity/Hub/Editor/6000.5.9f1/Unity.app/Contents/MacOS/Unity}"
PROJECT="$(CDPATH= cd -- "$(dirname -- "$0")/../TestProject~" && pwd)"
OUTPUT="${VAO_IL2CPP_BUILD_PATH:-/tmp/vao-unity-il2cpp/VAOVerification.app}"
LOG="${VAO_IL2CPP_LOG:-/tmp/vao-unity-il2cpp-build.log}"

VAO_IL2CPP_BUILD_PATH="$OUTPUT" "$UNITY_EDITOR" -batchmode -nographics -projectPath "$PROJECT" -executeMethod VaoPlayerBuildVerification.BuildMacIl2Cpp -logFile "$LOG" -quit
