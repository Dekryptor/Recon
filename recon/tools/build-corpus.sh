#!/usr/bin/env bash
# Builds the PE32 test corpus with MinGW. The corpus is generated, never committed:
# the tool's own repository stores no binaries.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${1:-$HERE/tests/corpus/mingw}"
CC="${CC:-i686-w64-mingw32-gcc}"
CXX="${CXX:-i686-w64-mingw32-g++}"

if ! command -v "$CC" >/dev/null 2>&1; then
  echo "error: $CC not found. Install mingw-w64 (Debian: apt install mingw-w64)." >&2
  exit 1
fi

mkdir -p "$OUT"

# Reproducible output: the PE time stamp and the linker's ordering otherwise change on every run,
# which would invalidate the checked-in hashes and the golden inventory.
export SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-1600000000}"
export LC_ALL=C
CFLAGS_REPRO=(-frandom-seed=recon-corpus)
LDFLAGS_REPRO=(-Wl,--no-insert-timestamp)

build() {
  local name="$1"; shift
  echo "building $name"
  "$@" "${CFLAGS_REPRO[@]}" "${LDFLAGS_REPRO[@]}" -o "$OUT/$name.exe" -Wl,-Map="$OUT/$name.map" -Wl,--cref
}

# Debug builds: symbols, no optimisation.
build sample-debug "$CC" -O0 -g -Wall -Wextra "$HERE/tests/corpus/sample.c"
# Release builds: the interesting case for heuristics.
build sample-release "$CC" -O2 -g -Wall "$HERE/tests/corpus/sample.c"
build shapes-debug "$CXX" -O0 -g -Wall "$HERE/tests/corpus/sample.cpp"
build shapes-release "$CXX" -O2 -g -Wall "$HERE/tests/corpus/sample.cpp"
# A DLL with exports; the map file stays useful here because the exported names are the oracle.
echo "building shapes-dll"
"$CXX" "${CFLAGS_REPRO[@]}" "${LDFLAGS_REPRO[@]}" -O2 -g -shared -o "$OUT/shapes-dll.dll" -Wl,-Map="$OUT/shapes-dll.map" -Wl,--cref "$HERE/tests/corpus/sample.cpp" || true
# The worst case: no debug info and no symbol table. The map file is written at link time and kept,
# because it is the only ground truth left for a stripped binary.
echo "building sample-stripped (no debug info, no symbol table)"
"$CC" "${CFLAGS_REPRO[@]}" "${LDFLAGS_REPRO[@]}" -O2 -s -o "$OUT/sample-stripped.exe" -Wl,-Map="$OUT/sample-stripped.map" -Wl,--cref "$HERE/tests/corpus/sample.c"

# A copy of the stripped binary with no sidecar files at all: the detection-only worst case, where
# every function boundary has to come from the code itself.
mkdir -p "$OUT/blind"
cp "$OUT/sample-stripped.exe" "$OUT/blind/sample.exe"

echo
echo "corpus written to $OUT"
ls -l "$OUT"
