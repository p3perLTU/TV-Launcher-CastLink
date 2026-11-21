#!/bin/bash
# Builds CastLink for macOS:
#   build/CastLink.app   the app (double-click: the window)
#   build/CastLink.dmg   the app to hand out (drag it to Applications)
#   ./castlink-mac       the same program for the terminal (castlink-mac --help)
# Needs the Xcode Command Line Tools (xcode-select --install) and macOS 13 or newer.
# The output also goes to build.log.
set -o pipefail
cd "$(dirname "$0")"
exec > >(tee build.log) 2>&1

VERSION=1.0

# the iPad page is the Windows version's, built into the binary
PAGE=../pc/web/ipad.html
[ -f "$PAGE" ] || PAGE=./ipad.html
if [ ! -f "$PAGE" ]; then echo "ipad.html not found (cast/pc/web/ipad.html)"; exit 1; fi
printf 'let pageBase64 = "%s"\n' "$(base64 < "$PAGE" | tr -d '\n')" > Sources/CastLinkMac/Page.generated.swift

if ! command -v swift >/dev/null; then echo "swift not found: run xcode-select --install"; exit 1; fi
swift --version

echo "== tests"
swift run -c release CastLinkCoreTests || { echo "TESTS FAILED"; exit 1; }

echo "== build"
swift build -c release --product CastLinkMac || { echo "BUILD FAILED"; exit 1; }
BIN="$(swift build -c release --show-bin-path)/CastLinkMac"
cp "$BIN" ./castlink-mac

# A signature that stays the same from build to build: macOS keeps the privacy permissions
# (Screen Recording, Accessibility) of a signed app by its certificate, but those of an ad-hoc
# signed one only for that exact build. So the first build makes a self-signed certificate
# "CastLink Local Signing" in the login keychain (no Apple account needed; it is only good on
# this Mac). If that does not work, an ad-hoc signature is used and the permissions have to be
# given again after a new build.
IDENT="CastLink Local Signing"
KC="$HOME/Library/Keychains/login.keychain-db"
if ! security find-certificate -c "$IDENT" "$KC" >/dev/null 2>&1; then
  T=$(mktemp -d)
  cat > "$T/cfg" <<CFG
[req]
distinguished_name=dn
x509_extensions=ext
prompt=no
[dn]
CN=$IDENT
[ext]
basicConstraints=critical,CA:false
keyUsage=critical,digitalSignature
extendedKeyUsage=critical,codeSigning
CFG
  /usr/bin/openssl req -x509 -newkey rsa:2048 -nodes -keyout "$T/key.pem" -out "$T/cert.pem" -days 3650 -config "$T/cfg" 2>/dev/null \
    && /usr/bin/openssl pkcs12 -export -inkey "$T/key.pem" -in "$T/cert.pem" -out "$T/id.p12" -passout pass:castlink -name "$IDENT" 2>/dev/null \
    && security import "$T/id.p12" -k "$KC" -P castlink -T /usr/bin/codesign >/dev/null \
    && echo "made the signing certificate \"$IDENT\" (login keychain)"
  rm -rf "$T"
fi
SIGN_HASH=$(security find-certificate -c "$IDENT" -Z "$KC" 2>/dev/null | awk '/SHA-1/{print $3; exit}')
sign() {
  if [ -n "$SIGN_HASH" ] && codesign --force --deep --sign "$SIGN_HASH" "$1" 2>/dev/null; then
    echo "signed with \"$IDENT\": $1"
  else
    codesign --force --deep --sign - "$1"
    echo "signed ad-hoc (permissions must be given again after this build): $1"
  fi
}
sign ./castlink-mac

echo "== app"
APP=build/CastLink.app
rm -rf build
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/CastLink"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>CastLink</string>
  <key>CFBundleDisplayName</key><string>CastLink</string>
  <key>CFBundleIdentifier</key><string>lt.tvcast.castlink.mac</string>
  <key>CFBundleExecutable</key><string>CastLink</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleLocalizations</key><array><string>en</string><string>lt</string></array>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>MIT License</string>
  <key>NSLocalNetworkUsageDescription</key><string>CastLink shows this Mac's screen to an iPad, a phone or another computer on your network.</string>
</dict>
</plist>
PLIST
# the icon: docs/images/castlink.svg rendered to Resources/AppIcon.png, in every size macOS wants
ICONSET=build/AppIcon.iconset
mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  sips -z $s $s Resources/AppIcon.png --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -z $((s * 2)) $((s * 2)) Resources/AppIcon.png --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
rm -rf "$ICONSET"
# no Apple developer account: runs here; on another Mac the first start is right-click -> Open
sign "$APP"

echo "== dmg"
STAGE=build/dmg
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "CastLink" -srcfolder "$STAGE" -ov -format UDZO build/CastLink.dmg >/dev/null || { echo "DMG FAILED"; exit 1; }
rm -rf "$STAGE"

echo "BUILD OK:"
echo "  $(pwd)/build/CastLink.app"
echo "  $(pwd)/build/CastLink.dmg"
echo "  $(pwd)/castlink-mac"
