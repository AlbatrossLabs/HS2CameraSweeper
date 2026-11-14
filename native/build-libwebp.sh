#!/bin/bash
# Builds libwebp_cs.dll (64-bit Windows) = libwebp's encoder + cs_webp.c, cross-compiled with Zig.
# Run on Linux or WSL:  bash native/build-libwebp.sh
# Needs: python3 + pip (for Zig), git.
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="${WORK:-/tmp/libwebp-build}"
mkdir -p "$WORK" && cd "$WORK"

python3 -m ziglang version >/dev/null 2>&1 || pip install ziglang==0.13.0
[ -d libwebp ] || git clone --depth 1 --branch v1.4.0 https://github.com/webmproject/libwebp.git
W="$WORK/libwebp"

rm -rf obj && mkdir obj
for f in "$W"/src/enc/*.c "$W"/src/dsp/*.c "$W"/src/utils/*.c "$W"/sharpyuv/*.c "$HERE/cs_webp.c"; do
  extra=""
  case "$f" in *_sse41.c) extra="-msse4.1 -DWEBP_HAVE_SSE41";; esac
  name="$(basename "$(dirname "$f")")_$(basename "$f" .c)"
  [ "$f" = "$HERE/cs_webp.c" ] && name="shim_cs_webp"
  python3 -m ziglang cc -target x86_64-windows-gnu -O2 -DNDEBUG -DWEBP_USE_THREAD -DWEBP_HAVE_SSE2 $extra \
    -I"$W" -I"$W/src" -c "$f" -o "obj/$name.o"
done
python3 -m ziglang cc -target x86_64-windows-gnu -shared -O2 -s -o "$HERE/libwebp_cs.dll" obj/*.o
cp "$W/COPYING" "$HERE/LICENSE-libwebp.txt"
cp "$W/PATENTS" "$HERE/PATENTS-libwebp.txt"
echo "Built $HERE/libwebp_cs.dll"
