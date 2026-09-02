#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "$0")/../.." && pwd)
output_directory=${1:-artifacts/macos}
version=${PACKAGE_VERSION:-0.1.0-rc.1}
configuration=${CONFIGURATION:-Release}

if [[ "$output_directory" != /* ]]; then
  output_directory="$repo_root/$output_directory"
fi
if [[ ! "$version" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)([-+].*)?$ ]]; then
  echo "Version must be a semantic version with three numeric components." >&2
  exit 2
fi
bundle_version="${BASH_REMATCH[1]}.${BASH_REMATCH[2]}.${BASH_REMATCH[3]}"
project="$repo_root/src/DropShelf.App/DropShelf.App.csproj"

rm -rf "$output_directory"
mkdir -p "$output_directory"

dotnet restore "$repo_root/DropShelf.sln"
python3 "$repo_root/scripts/ci/generate-license-inventory.py" \
  --root "$repo_root" \
  --output "$output_directory/DropShelf-macos-third-party-licenses.spdx.json"

host_machine=$(uname -m)
case "$host_machine" in
  arm64) host_runtime=osx-arm64 ;;
  x86_64) host_runtime=osx-x64 ;;
  *) host_runtime=unsupported ;;
esac
host_smoke_ran=false

for runtime in osx-x64 osx-arm64; do
  publish_directory="$output_directory/.staging/$runtime/publish"
  app_name="DropShelf-macos-${runtime#osx-}.app"
  app_directory="$output_directory/$app_name"
  contents="$app_directory/Contents"

  dotnet restore "$project" --runtime "$runtime"
  dotnet publish "$project" \
    --configuration "$configuration" \
    --runtime "$runtime" \
    --self-contained true \
    --no-restore \
    -p:Version="$version" \
    -p:PublishSingleFile=false \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -p:PublishDir="$publish_directory/"

  mkdir -p "$contents/MacOS" "$contents/Resources"
  cp -R "$publish_directory/." "$contents/MacOS/"
  chmod +x "$contents/MacOS/DropShelf.App"

  cat > "$contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDisplayName</key><string>Drop Shelf</string>
  <key>CFBundleExecutable</key><string>DropShelf.App</string>
  <key>CFBundleIdentifier</key><string>com.rwrife.dropshelf</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>Drop Shelf</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$bundle_version</string>
  <key>CFBundleVersion</key><string>$bundle_version</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

  iconset="$output_directory/.staging/$runtime/AppIcon.iconset"
  mkdir -p "$iconset"
  while read -r filename size; do
    python3 "$repo_root/scripts/ci/generate-package-assets.py" \
      --output "$iconset/$filename" --width "$size"
  done <<'ICONS'
icon_16x16.png 16
icon_16x16@2x.png 32
icon_32x32.png 32
icon_32x32@2x.png 64
icon_128x128.png 128
icon_128x128@2x.png 256
icon_256x256.png 256
icon_256x256@2x.png 512
icon_512x512.png 512
icon_512x512@2x.png 1024
ICONS
  iconutil --convert icns --output "$contents/Resources/AppIcon.icns" "$iconset"

  cat > "$contents/Resources/PACKAGING-STATUS.txt" <<'STATUS'
Drop Shelf macOS packaging status

- This app uses ad-hoc local signing only; it is not Developer ID signed.
- The app and DMG are not notarized and are not claimed as production-distributable packages.
- See docs/install-and-uninstall.md and docs/release-checklist.md.
STATUS

  plutil -lint "$contents/Info.plist"
  codesign --force --deep --sign - "$app_directory"
  codesign --verify --deep --strict "$app_directory"

  zip_path="$output_directory/${app_name}.zip"
  python3 "$repo_root/scripts/ci/create-deterministic-zip.py" \
    --source "$app_directory" --output "$zip_path" --prefix "$app_name"

  dmg_path="$output_directory/DropShelf-macos-${runtime#osx-}.dmg"
  hdiutil create -quiet -ov -format UDZO -volname "Drop Shelf" -srcfolder "$app_directory" "$dmg_path"

  if [[ "$runtime" == "$host_runtime" ]]; then
    "$contents/MacOS/DropShelf.App" --package-smoke-test
    host_smoke_ran=true
  fi
done

if [[ "$host_smoke_ran" != true ]]; then
  echo "No package matched the native runner architecture; packaged smoke test did not run." >&2
  exit 3
fi

python3 "$repo_root/scripts/ci/generate-checksums.py" \
  --output "$output_directory/DropShelf-macos-SHA256SUMS.txt" \
  "$output_directory/DropShelf-macos-x64.app.zip" \
  "$output_directory/DropShelf-macos-x64.dmg" \
  "$output_directory/DropShelf-macos-arm64.app.zip" \
  "$output_directory/DropShelf-macos-arm64.dmg" \
  "$output_directory/DropShelf-macos-third-party-licenses.spdx.json"
rm -rf "$output_directory/.staging"

echo "macOS packages created; the native-architecture app passed its packaged smoke test."
