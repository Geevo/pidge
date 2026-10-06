#!/usr/bin/env bash
# Wraps the published Linux executable in a .deb, an .rpm and an AppImage.
#
#   scripts/package-linux.sh <version> <executable> <out-dir>
#
# All three install the same files. GTK, WebKitGTK and the rest come from the
# system, so the packages declare them and the AppImage expects them; see
# docs/packaging.md. Needs dpkg-deb, rpmbuild, mksquashfs and curl.
set -euo pipefail

version=$1
exe=$2
out=$(mkdir -p "$3" && cd "$3" && pwd)
root=$(cd "$(dirname "$0")/.." && pwd)

summary="A local-first HTTP client."
description="Type a URL, press Send. No accounts, no cloud, no workspace."
homepage="https://github.com/Geevo/pidge"

# The AppImage runtime, pinned: the static one, which needs no FUSE library.
runtime_url="https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-x86_64"
runtime_sha256="2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# ---- the installed files, shared by all three ----
stage="$work/stage"
install -Dm755 "$exe" "$stage/usr/bin/pidge"
install -Dm644 "$root/src/Pidge.Desktop/icons/icon.png" \
  "$stage/usr/share/icons/hicolor/512x512/apps/pidge.png"
for license in LICENSE THIRD-PARTY-LICENSES.md \
  packages/ui/src/fonts/IBMPlexSans-LICENSE.txt \
  packages/ui/src/fonts/IBMPlexMono-LICENSE.txt; do
  install -Dm644 "$root/$license" "$stage/usr/share/doc/pidge/$(basename "$license")"
done

# StartupWMClass and Icon match the program name the app sets, which is how
# Wayland finds the window's icon.
mkdir -p "$stage/usr/share/applications"
cat > "$stage/usr/share/applications/pidge.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=pidge
Comment=$summary
Exec=pidge
Icon=pidge
Terminal=false
Categories=Development;
StartupWMClass=pidge
EOF

# ---- .deb ----
deb="$work/deb"
cp -a "$stage" "$deb"
mkdir -p "$deb/DEBIAN"
cat > "$deb/DEBIAN/control" <<EOF
Package: pidge
Version: $version
Architecture: amd64
Maintainer: Geevo <14214341+Geevo@users.noreply.github.com>
Installed-Size: $(du -sk --exclude=DEBIAN "$deb" | cut -f1)
Depends: libgtk-3-0, libwebkit2gtk-4.1-0, libnotify4, libssl3 | libssl3t64
Section: devel
Priority: optional
Homepage: $homepage
Description: $summary
 $description
EOF
# xz rather than Ubuntu's default zstd, which older dpkg can't read.
dpkg-deb -Zxz --root-owner-group --build "$deb" "$out/pidge_${version}_amd64.deb"

# ---- .rpm ----
# Requirements are listed by hand: the window host is inside the executable,
# where rpm's own dependency scan can't see it. Nothing is stripped, because
# the executable carries resources a strip pass has no business touching.
cat > "$work/pidge.spec" <<EOF
Name: pidge
Version: $version
Release: 1
Summary: $summary
License: MIT
URL: $homepage
Requires: gtk3, webkit2gtk4.1, libnotify, openssl-libs
AutoReqProv: no
%global debug_package %{nil}
%global __os_install_post %{nil}

%description
$description

%install
cp -a "$stage/." "%{buildroot}/"

%files
/usr/bin/pidge
/usr/share/applications/pidge.desktop
/usr/share/icons/hicolor/512x512/apps/pidge.png
%doc /usr/share/doc/pidge
EOF
rpmbuild -bb "$work/pidge.spec" --target x86_64 \
  --define "_topdir $work/rpm" \
  --define "_rpmdir $out" \
  --define "_build_name_fmt %%{NAME}-%%{VERSION}-%%{RELEASE}.%%{ARCH}.rpm"

# ---- AppImage ----
# The runtime, with a squashfs image of the app directory appended.
appdir="$work/AppDir"
cp -a "$stage" "$appdir"
ln -s usr/bin/pidge "$appdir/AppRun"
cp "$stage/usr/share/applications/pidge.desktop" "$appdir/pidge.desktop"
cp "$stage/usr/share/icons/hicolor/512x512/apps/pidge.png" "$appdir/pidge.png"
ln -s pidge.png "$appdir/.DirIcon"

curl -fsSL "$runtime_url" -o "$work/runtime"
echo "$runtime_sha256  $work/runtime" | sha256sum --check --quiet
mksquashfs "$appdir" "$work/app.squashfs" -root-owned -noappend -comp zstd -quiet -no-progress
appimage="$out/pidge-$version-x86_64.AppImage"
cat "$work/runtime" "$work/app.squashfs" > "$appimage"
chmod 755 "$appimage"

ls -l "$out"
