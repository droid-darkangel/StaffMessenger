#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUTPUT_DIR="${1:-"$ROOT_DIR/artifacts/release"}"
STAGING_DIR="$OUTPUT_DIR/.staging"
VERSION="${STAFFMESSENGER_VERSION:-0.1.0}"
VERSION="${VERSION#v}"
BUNDLE_VERSION="${VERSION%%-*}"
if [[ ! "$BUNDLE_VERSION" =~ ^[0-9]+([.][0-9]+){0,2}$ ]]; then
    BUNDLE_VERSION="0.1.0"
fi
TARGETS=",${STAFFMESSENGER_TARGETS:-server,windows,macos},"

SERVER_PROJECT="$ROOT_DIR/StaffMessenger.Server/StaffMessenger.Server.csproj"
DESKTOP_PROJECT="$ROOT_DIR/StaffMessenger.Desktop/StaffMessenger.Desktop.csproj"

rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR" "$STAGING_DIR"

publish_single_file() {
    local project="$1"
    local runtime="$2"
    local output="$3"

    dotnet publish "$project" \
        --configuration Release \
        --runtime "$runtime" \
        --self-contained true \
        --output "$output" \
        --nologo \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:EnableCompressionInSingleFile=true \
        -p:PublishTrimmed=false \
        -p:DebugType=None \
        -p:DebugSymbols=false
}

should_build() {
    [[ "$TARGETS" == *",$1,"* ]]
}

if should_build server; then
    echo "Publishing Astra Linux x64 server..."
    SERVER_STAGE="$STAGING_DIR/server-astralinux-x64"
    SERVER_PACKAGE="$STAGING_DIR/server-package"
    publish_single_file "$SERVER_PROJECT" "linux-x64" "$SERVER_STAGE"

    mkdir -p "$SERVER_PACKAGE"
    install -m 0755 "$SERVER_STAGE/StaffMessenger.Server" "$SERVER_PACKAGE/StaffMessenger.Server"
    install -m 0755 "$ROOT_DIR/deploy/astralinux/install.sh" "$SERVER_PACKAGE/install.sh"
    install -m 0644 "$ROOT_DIR/deploy/astralinux/staffmessenger.service" "$SERVER_PACKAGE/staffmessenger.service"
    install -m 0640 "$ROOT_DIR/deploy/astralinux/appsettings.Production.json" "$SERVER_PACKAGE/appsettings.Production.json"
    install -m 0644 "$ROOT_DIR/deploy/astralinux/README.txt" "$SERVER_PACKAGE/README.txt"
    tar -C "$SERVER_PACKAGE" -czf "$OUTPUT_DIR/StaffMessenger-Server-astralinux-x64.tar.gz" .
fi

if should_build windows; then
    echo "Publishing Windows x64 single-file app..."
    WINDOWS_STAGE="$STAGING_DIR/windows-x64"
    publish_single_file "$DESKTOP_PROJECT" "win-x64" "$WINDOWS_STAGE"
    install -m 0644 "$WINDOWS_STAGE/StaffMessenger.Desktop.exe" "$OUTPUT_DIR/StaffMessenger-Windows-x64.exe"
fi

if should_build macos && [[ "$(uname -s)" == "Darwin" ]]; then
    echo "Publishing macOS ARM64 app and DMG..."
    MAC_STAGE="$STAGING_DIR/macos-arm64"
    APP_DIR="$STAGING_DIR/StaffMessenger.app"
    CONTENTS_DIR="$APP_DIR/Contents"
    MACOS_DIR="$CONTENTS_DIR/MacOS"
    RESOURCES_DIR="$CONTENTS_DIR/Resources"
    ICONSET_DIR="$STAGING_DIR/StaffMessenger.iconset"

    publish_single_file "$DESKTOP_PROJECT" "osx-arm64" "$MAC_STAGE"

    mkdir -p "$MACOS_DIR" "$RESOURCES_DIR" "$ICONSET_DIR"
    install -m 0755 "$MAC_STAGE/StaffMessenger.Desktop" "$MACOS_DIR/StaffMessenger.Desktop"
    sed "s/__VERSION__/$BUNDLE_VERSION/g" "$ROOT_DIR/deploy/macos/Info.plist" > "$CONTENTS_DIR/Info.plist"

    SOURCE_ICON="$ROOT_DIR/StaffMessenger/Assets/staffmessenger-logo.png"
    for size in 16 32 128 256 512; do
        sips -z "$size" "$size" "$SOURCE_ICON" --out "$ICONSET_DIR/icon_${size}x${size}.png" >/dev/null
        double_size=$((size * 2))
        sips -z "$double_size" "$double_size" "$SOURCE_ICON" --out "$ICONSET_DIR/icon_${size}x${size}@2x.png" >/dev/null
    done
    iconutil -c icns "$ICONSET_DIR" -o "$RESOURCES_DIR/StaffMessenger.icns"

    codesign --force --deep --sign - "$APP_DIR"
    hdiutil create \
        -volname "StaffMessenger" \
        -srcfolder "$APP_DIR" \
        -ov \
        -format UDZO \
        "$OUTPUT_DIR/StaffMessenger-macOS-arm64.dmg" >/dev/null
elif should_build macos; then
    echo "Cannot build the DMG: this step requires macOS." >&2
    exit 1
fi

rm -rf "$STAGING_DIR"

echo
echo "Release artifacts:"
find "$OUTPUT_DIR" -maxdepth 1 -type f -print
