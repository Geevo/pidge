#!/usr/bin/env bash
# Populates an AppDir on the Ubuntu 22.04 release builder. Photino's native
# library is embedded, so its dlopen dependencies must be supplied explicitly.
set -euo pipefail

appdir=$(realpath "$1")
root=$(cd "$(dirname "$0")/.." && pwd)
tools=$(mkdir -p "$2" && cd "$2" && pwd)
libdir=$(pkg-config --variable=libdir webkit2gtk-4.1)
webkit="$libdir/webkit2gtk-4.1"

download() {
    curl -fsSL --retry 3 "$1" -o "$tools/$2"
    echo "$3  $tools/$2" | sha256sum --check --quiet
    chmod 755 "$tools/$2"
}

# The Tauri fork handles WebKit's compiled-in resource paths. These are build
# tools only; the app has no Rust or Tauri runtime dependency.
download "https://github.com/tauri-apps/binary-releases/releases/download/linuxdeploy-07333c6/linuxdeploy-x86_64.AppImage" \
    linuxdeploy-x86_64.AppImage 36a2d7e274d12e1050d0e9ecfe11d339ed54720b2bec464c286d53f8b07f5c62
plugin_base="https://raw.githubusercontent.com/tauri-apps/tauri/a225a18e601c1d8c3c24536a2137bff263ea74bf/crates/tauri-bundler/src/bundle/linux/appimage"
download "$plugin_base/linuxdeploy-plugin-gtk.sh" linuxdeploy-plugin-gtk.sh \
    ef6b9a980417243bc62e0241b51dc49876032afd1bab9b4762389f961b406d9b
download "$plugin_base/linuxdeploy-plugin-gstreamer.sh" linuxdeploy-plugin-gstreamer.sh \
    2a15ce9da8de6e20159e1ab27861a7a5ef8758c81a6278ba4ab30cefa1d74c9f
download "https://raw.githubusercontent.com/tauri-apps/tauri/a225a18e601c1d8c3c24536a2137bff263ea74bf/LICENSE-MIT" \
    tauri-LICENSE-MIT 9dd42ea92cff2ede5cd477cbfcce051b2d0115c0ac7f368ee88cb545055dff1d

# Production WebKit builds ignore WEBKIT_EXEC_PATH. Carry its helpers at the
# compiled-in paths, relocated by the GTK plugin, and fail if any are absent.
for file in WebKitNetworkProcess WebKitWebProcess injected-bundle/libwebkit2gtkinjectedbundle.so; do
    install -Dm755 "$webkit/$file" "$appdir$webkit/$file"
done
if [ -x "$webkit/WebKitGPUProcess" ]; then
    install -Dm755 "$webkit/WebKitGPUProcess" "$appdir$webkit/WebKitGPUProcess"
fi
if [ -d /usr/share/webkitgtk-4.1 ]; then
    cp -a /usr/share/webkitgtk-4.1 "$appdir/usr/share/"
fi
# GdkPixbuf uses MIME data when identifying images, including the window icon.
cp -a /usr/share/mime "$appdir/usr/share/"

libraries=()
for name in libgtk-3.so.0 libwebkit2gtk-4.1.so.0 libnotify.so.4 \
    libssl.so.3 libcrypto.so.3 libicuuc.so libicui18n.so \
    libfontconfig.so.1 libfreetype.so.6 libharfbuzz.so.0 libfribidi.so.0; do
    libraries+=(--library "$libdir/$name")
done

# GIO loads its TLS backend dynamically, outside the ELF dependency graph.
mkdir -p "$appdir/usr/lib/gio/modules"
cp -a "$libdir/gio/modules/"*.so "$appdir/usr/lib/gio/modules/"

# Text-rendering libraries are excluded by linuxdeploy's default list, so the
# roots above force them into the image. Give them relocatable configuration
# and a fallback font as well, for hosts without a fontconfig installation.
mkdir -p "$appdir/etc/fonts/conf.d" "$appdir/usr/share/fonts/truetype"
cp -aL /etc/fonts/conf.d/. "$appdir/etc/fonts/conf.d/"
install -m644 /usr/share/fonts/truetype/dejavu/DejaVuSans.ttf "$appdir/usr/share/fonts/truetype/"
cat > "$appdir/etc/fonts/fonts.conf" <<'EOF'
<?xml version="1.0"?>
<fontconfig>
  <dir prefix="relative">../../usr/share/fonts</dir>
  <dir>/usr/local/share/fonts</dir>
  <dir>/usr/share/fonts</dir>
  <dir prefix="xdg">fonts</dir>
  <cachedir prefix="xdg">fontconfig</cachedir>
  <include ignore_missing="yes">conf.d</include>
</fontconfig>
EOF

# Never strip the NativeAOT executable: it contains the UI and window host.
# Leave graphics drivers and Wayland client libraries to the host so newer
# Mesa drivers can use the matching system versions.
export NO_STRIP=1 APPIMAGE_EXTRACT_AND_RUN=1
"$tools/linuxdeploy-x86_64.AppImage" --appdir "$appdir" \
    "${libraries[@]}" --exclude-library 'libwayland-client.so*' \
    --exclude-library 'libwayland-server.so*' \
    --plugin gtk --plugin gstreamer

# Plugins invoke linuxdeploy again; keep their dependency scans from undoing
# these final runtime choices or leaving an extra launcher around the app.
rm -f "$appdir"/usr/lib/libwayland-client.so* "$appdir"/usr/lib/libwayland-server.so* "$appdir/AppRun.wrapped"
install -m755 "$root/scripts/appimage/AppRun" "$appdir/AppRun"
find "$appdir/usr/lib" -name 'libwebkit*' -type f -exec sed -i 's|/usr|././|g' {} +

for file in libwebkit2gtk-4.1.so.0 libjavascriptcoregtk-4.1.so.0 \
    libgtk-3.so.0 libssl.so.3; do
    test -f "$appdir/usr/lib/$file"
done
# ICU's soname carries its version, which follows the builder's release.
compgen -G "$appdir/usr/lib/libicuuc.so.*" > /dev/null
test -f "$appdir/usr/lib/gstreamer-1.0/libgstapp.so"
test -x "$appdir/usr/lib/gstreamer1.0/gstreamer-1.0/gst-plugin-scanner"
test -f "$appdir/usr/share/glib-2.0/schemas/gschemas.compiled"

# Record which of the builder's packages supplied the AppDir's files, with
# their versions and copyright notices, so the sources can be identified.
# linuxdeploy flattens libraries into usr/lib, so look for those under the
# builder's library directories as well.
notices="$appdir/usr/share/doc/pidge/bundled"
mkdir -p "$notices"
multiarch=$(basename "$libdir")
(cd "$appdir" && find . ! -type d -printf '%P\n') | while IFS= read -r file; do
    case $file in
        usr/lib/*) set -- "/$file" "$libdir/${file#usr/lib/}" "/lib/$multiarch/${file#usr/lib/}" ;;
        *) set -- "/$file" ;;
    esac
    for source; do
        if [ -e "$source" ]; then
            printf '%s\0' "$source"
        fi
    done
done > "$tools/bundled-files"
# dpkg -S fails for files no package owns, such as generated caches.
{ xargs -0 dpkg -S < "$tools/bundled-files" 2>/dev/null || true; } |
    grep -Ev '^(local )?diversion ' | sed 's/: .*//' | tr ',' '\n' | sed 's/^ *//' |
    sort -u > "$tools/bundled-packages"
test -s "$tools/bundled-packages"
while IFS= read -r package; do
    dpkg-query -W -f='${binary:Package}\t${Version}\n' "$package"
    cp -L "/usr/share/doc/${package%%:*}/copyright" "$notices/${package%%:*}.copyright"
done < "$tools/bundled-packages" > "$notices/builder-packages.tsv"
install -m644 "$tools/tauri-LICENSE-MIT" "$notices/tauri-plugins-LICENSE-MIT"
