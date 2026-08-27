#!/bin/sh
set -eu

UNITY_EDITOR="${UNITY_EDITOR:-/Applications/Unity/Hub/Editor/6000.5.9f1/Unity.app/Contents/MacOS/Unity}"
PROJECT="$(CDPATH= cd -- "$(dirname -- "$0")/../TestProject~" && pwd)"
RESULTS="${VAO_TEST_RESULTS:-$PROJECT/TestResults.xml}"
LOG="${VAO_TEST_LOG:-$PROJECT/unity-tests.log}"

"$UNITY_EDITOR" -batchmode -nographics -projectPath "$PROJECT" -runTests -testPlatform EditMode -testResults "$RESULTS" -logFile "$LOG"
