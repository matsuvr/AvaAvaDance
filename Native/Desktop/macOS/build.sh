#!/bin/bash
set -euo pipefail
source_dir=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$source_dir/../../.." && pwd)
cd "$root"
[[ $(uname -s) == Darwin ]] || { echo "The SwiftUI host requires macOS and Xcode." >&2; exit 1; }
version=$(awk '/^m_EditorVersion:/{print $2}' ProjectSettings/ProjectVersion.txt | tr -d '\r')
editor=${UNITY_EDITOR:-/Applications/Unity/Hub/Editor/$version/Unity.app/Contents/MacOS/Unity}
output="$root/Builds/macOS"
mkdir -p "$output"
stage=$(mktemp -d "$output/.build.XXXXXX")
trap 'rm -rf "$stage"' EXIT
app="$stage/AvaAvaDance.app"

python3 Tools/restore_inputs.py
AVA_MACOS_BUILD_PATH="$app" "$editor" -batchmode -quit -buildTarget osxuniversal \
    -projectPath "$root" -executeMethod AvaAvaDance.Editor.BuildDesktopMac.Build -logFile "$output/player-build.log"

library="$app/Contents/Frameworks/UnityPlayer.dylib"
executable=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$app/Contents/Info.plist")
signing=(--force --sign -)
if codesign -d --entitlements :- "$app" > "$stage/player.entitlements" 2>/dev/null && [[ -s "$stage/player.entitlements" ]]; then
    plutil -lint "$stage/player.entitlements"
    signing+=(--entitlements "$stage/player.entitlements")
fi
sdk=$(xcrun --sdk macosx --show-sdk-path)
launchers=()
for arch in $(xcrun lipo -archs "$library"); do
    target="$arch-apple-macos12.0"
    xcrun clang++ -target "$target" -isysroot "$sdk" -O2 -c "$source_dir/PlayerEntry.cpp" -o "$stage/entry.$arch.o"
    xcrun swiftc -swift-version 5 -target "$target" -sdk "$sdk" -O -parse-as-library \
        "$source_dir"/*.swift "$stage/entry.$arch.o" "$library" -lc++ \
        -Xlinker -rpath -Xlinker '@executable_path/../Frameworks' -o "$stage/launcher.$arch"
    launchers+=("$stage/launcher.$arch")
done
xcrun lipo -create "${launchers[@]}" -output "$app/Contents/MacOS/$executable"
chmod 755 "$app/Contents/MacOS/$executable"
plutil -replace LSMinimumSystemVersion -string 12.0 "$app/Contents/Info.plist"
codesign "${signing[@]}" "$app"
codesign --verify --strict --deep "$app"

# An installed bundle can contain user-installed extensions. After the atomic
# exchange, keep it beside the new build. If moving it fails, retain the stage.
trap - EXIT
echo "Publishing native app from $stage"
python3 - "$app" "$output/AvaAvaDance.app" <<'PY'
import ctypes
import os
import sys
import uuid

source, destination = sys.argv[1:]
if os.path.lexists(destination):
    if os.path.islink(destination) or not os.path.isfile(os.path.join(destination, "Contents", "Frameworks", "UnityPlayer.dylib")):
        raise ValueError(f"Unexpected destination: {destination}. New build retained at {source}")
    libc = ctypes.CDLL(None, use_errno=True)
    swap = libc.renamex_np
    swap.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_uint]
    if swap(os.fsencode(source), os.fsencode(destination), 2):  # RENAME_SWAP
        raise OSError(ctypes.get_errno(), os.strerror(ctypes.get_errno()), destination)
    previous = os.path.join(os.path.dirname(destination), f"AvaAvaDance.previous-{uuid.uuid4().hex}.app")
    os.rename(source, previous)
    print(f"Previous application preserved at {previous}")
else:
    os.rename(source, destination)
PY
rm -rf "$stage"
echo "$output/AvaAvaDance.app"
