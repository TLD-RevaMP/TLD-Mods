#!/bin/bash
# Builds RayTraceNative.dll (64-bit Windows, mingw-w64) and compiles the HLSL shaders with Microsoft's DXC:
#   mods/RayTrace/Native/build.sh [dxc folder]
# The dxc folder holds DXC's Linux release (bin/dxc, lib/libdxcompiler.so); default ~/.local/opt/dxc-1.9.2609.
# The results go to Native/out/ and are embedded in RayTrace.dll by RayTrace.csproj.
set -euo pipefail
here=$(dirname "$(realpath "$0")"); out="$here/out"; dxc=$(realpath -m "${1:-$HOME/.local/opt/dxc-1.9.2609}")
mkdir -p "$out"
map="-ffile-prefix-map=$here=native"
x86_64-w64-mingw32-g++ -std=c++17 -O2 -DNDEBUG $map -shared -static -static-libgcc -static-libstdc++ \
  -Wall -Wno-unknown-pragmas -o "$out/RayTraceNative.dll" "$here/rt.cc" -Wl,--kill-at
x86_64-w64-mingw32-strip "$out/RayTraceNative.dll"
for s in "$here"/shaders/*.hlsl; do
  [ -e "$s" ] || continue
  name=$(basename "$s" .hlsl)
  profile=$(sed -n 's,^// profile: *,,p' "$s" | head -1)
  LD_LIBRARY_PATH="$dxc/lib" "$dxc/bin/dxc" -T "$profile" -O3 -Qstrip_debug -Qstrip_reflect -Fo "$out/$name.dxil" "$s" > /dev/null
done
leak=$(strings -a "$out"/* | grep -E "$HOME|$(whoami)|/tmp/" || true)
if [ -n "$leak" ]; then echo "$leak" | head -3; echo "a folder name of this machine in the output"; exit 1; fi
(cd "$out" && sha256sum *)
