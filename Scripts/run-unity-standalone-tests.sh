#!/bin/sh
set -eu

UNITY_EDITOR="${UNITY_EDITOR:-/Applications/Unity/Hub/Editor/6000.5.9f1/Unity.app/Contents/MacOS/Unity}"
PROJECT="$(CDPATH= cd -- "$(dirname -- "$0")/../TestProject~" && pwd)"
RESULTS="${VAO_STANDALONE_RESULTS:-/tmp/vao-unity-standalone.xml}"
LOG="${VAO_STANDALONE_LOG:-/tmp/vao-unity-standalone.log}"

"$UNITY_EDITOR" -batchmode -nographics -projectPath "$PROJECT" -runTests -testPlatform StandaloneOSX -testResults "$RESULTS" -logFile "$LOG"
