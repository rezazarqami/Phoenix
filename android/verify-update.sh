#!/usr/bin/env bash
set -euo pipefail
package=ir.marketsignalist.phoenix.standalone.capture
adb install previous-phoenix.apk
adb shell run-as "$package" mkdir -p files
adb shell run-as "$package" touch files/update-preserved
adb install -r android/app/build/outputs/apk/phoenix/debug/app-phoenix-debug.apk
adb shell run-as "$package" test -f files/update-preserved
adb shell pm path "$package"
