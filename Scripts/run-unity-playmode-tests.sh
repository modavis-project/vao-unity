#!/bin/sh
set -eu

UNITY_EDITOR="${UNITY_EDITOR:-/Applications/Unity/Hub/Editor/6000.5.9f1/Unity.app/Contents/MacOS/Unity}"
PROJECT="$(CDPATH= cd -- "$(dirname -- "$0")/../TestProject~" && pwd)"
RESULTS="${VAO_PLAYMODE_RESULTS:-$PROJECT/PlayModeResults.xml}"
LOG="${VAO_PLAYMODE_LOG:-$PROJECT/unity-playmode-tests.log}"

"$UNITY_EDITOR" -batchmode -nographics -projectPath "$PROJECT" -runTests -testPlatform PlayMode -testResults "$RESULTS" -logFile "$LOG"
