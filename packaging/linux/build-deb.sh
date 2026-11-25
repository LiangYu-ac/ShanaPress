#!/usr/bin/env bash
set -euo pipefail

PAYLOAD_DIR="${1:?Usage: build-deb.sh <payload_dir> <version> <deb_arch> <output_deb>}"
VERSION="${2:?Missing version}"
ARCH="${3:?Missing deb arch (amd64/arm64)}"
OUT_DEB="${4:?Missing output .deb path}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
STAGE_DIR="$(mktemp -d)"
trap 'rm -rf "$STAGE_DIR"' EXIT

mkdir -p "$STAGE_DIR/DEBIAN"
mkdir -p "$STAGE_DIR/opt/shanapress"
mkdir -p "$STAGE_DIR/usr/bin"
mkdir -p "$STAGE_DIR/usr/share/applications"
mkdir -p "$STAGE_DIR/usr/share/icons/hicolor/256x256/apps"

cp -a "$PAYLOAD_DIR/." "$STAGE_DIR/opt/shanapress/"
chmod +x "$STAGE_DIR/opt/shanapress/ShanaPress"
ln -s /opt/shanapress/ShanaPress "$STAGE_DIR/usr/bin/shanapress"

cp "$ROOT_DIR/packaging/linux/shanapress.desktop" "$STAGE_DIR/usr/share/applications/shanapress.desktop"
cp "$ROOT_DIR/docs/logo.png" "$STAGE_DIR/usr/share/icons/hicolor/256x256/apps/shanapress.png"

INSTALLED_KB="$(du -sk "$STAGE_DIR/opt/shanapress" | awk '{print $1}')"

cat > "$STAGE_DIR/DEBIAN/control" <<EOF
Package: shanapress
Version: $VERSION
Section: video
Priority: optional
Architecture: $ARCH
Installed-Size: $INSTALLED_KB
Depends: libfontconfig1, libx11-6, libice6, libsm6
Recommends: ffmpeg
Maintainer: ShanaPress <noreply@github.com>
Homepage: https://github.com/LiangYu-ac/ShanaPress
Description: Modern GPU-accelerated batch video compressor
 ShanaPress is a cross-platform batch video compression desktop tool
 built with .NET 8 and FFmpeg, supporting hardware acceleration,
 2-pass target size control, visual trim/crop, and audio/video filters.
EOF

mkdir -p "$(dirname "$OUT_DEB")"
dpkg-deb --root-owner-group --build "$STAGE_DIR" "$OUT_DEB"
