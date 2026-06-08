#!/usr/bin/env bash
set -euo pipefail

REPOSITORY="${STAFFMESSENGER_REPOSITORY:-droid-darkangel/StaffMessenger}"
VERSION="${STAFFMESSENGER_VERSION:-latest}"
ASSET_NAME="StaffMessenger-Server-astralinux-x64.tar.gz"
DOWNLOAD_URL="${STAFFMESSENGER_DOWNLOAD_URL:-}"

if [[ "${EUID:-$(id -u)}" -ne 0 ]]; then
    echo "Run this installer through sudo." >&2
    exit 1
fi

if [[ -z "$DOWNLOAD_URL" ]]; then
    if [[ -z "$REPOSITORY" ]]; then
        echo "Set STAFFMESSENGER_REPOSITORY or STAFFMESSENGER_DOWNLOAD_URL." >&2
        exit 1
    fi

    if [[ "$VERSION" == "latest" ]]; then
        DOWNLOAD_URL="https://github.com/$REPOSITORY/releases/latest/download/$ASSET_NAME"
    else
        DOWNLOAD_URL="https://github.com/$REPOSITORY/releases/download/$VERSION/$ASSET_NAME"
    fi
fi

TEMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TEMP_DIR"' EXIT

echo "Downloading $DOWNLOAD_URL"
curl --fail --location --retry 3 --show-error \
    "$DOWNLOAD_URL" \
    --output "$TEMP_DIR/$ASSET_NAME"

tar -xzf "$TEMP_DIR/$ASSET_NAME" -C "$TEMP_DIR"
"$TEMP_DIR/install.sh"
