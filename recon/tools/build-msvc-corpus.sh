#!/usr/bin/env bash
# Builds the MSVC-ABI part of the test corpus: clause "MSVC first" in the plan is only proven by a
# real PDB, so the corpus needs binaries whose debug info was written by Microsoft's format rather
# than by GNU tools.
#
# clang with --target=i686-pc-windows-msvc and lld-link produce genuine MSVC-ABI objects, a genuine
# PDB and a genuine map file, with no Windows and no Visual Studio install involved. That makes the
# whole path testable on Linux, which is what section 7.6 of the plan asks for.
#
# Usage: tools/build-msvc-corpus.sh [output-directory]
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${1:-$HERE/tests/corpus/msvc}"

# Versioned names first, then the unversioned ones, so either packaging style works.
find_tool() {
  for candidate in "$@"; do
    if command -v "$candidate" >/dev/null 2>&1; then
      command -v "$candidate"
      return 0
    fi
  done

  return 1
}

CC="$(find_tool "${CC:-}" clang-19 clang-18 clang)" || {
  echo "error: no clang found. Install LLVM (Debian: apt install llvm-19 clang-19 lld-19)." >&2
  exit 1
}
LD="$(find_tool "${LD:-}" lld-link-19 lld-link-18 lld-link lld-link.exe)" || {
  echo "error: no lld-link found. Install lld (Debian: apt install lld-19)." >&2
  exit 1
}

if ! command -v "$LD" >/dev/null 2>&1; then
  echo "error: lld-link not found" >&2
  exit 1
fi

mkdir -p "$OUT"

# Reproducible as far as a debug format allows. A PDB stores the path of every object file it was
# linked from, so the objects are built inside the output directory and the compilation directory is
# pinned; SOURCE_DATE_EPOCH and /Brepro keep the time stamps and the PDB GUID content-derived.
export SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-1600000000}"
export LC_ALL=C

compile() {
  local source="$1" object="$2"
  echo "compiling $(basename "$source") with $(basename "$CC")"
  "$CC" --target=i686-pc-windows-msvc -O2 -g -gcodeview \
    -fdebug-compilation-dir=corpus-msvc \
    -c "$source" -o "$object"
}

link() {
  local name="$1"; shift
  echo "linking $name.exe with $(basename "$LD")"
  "$LD" /out:"$OUT/$name.exe" /entry:mainCRTStartup /subsystem:console /nodefaultlib \
    /debug /pdb:"$OUT/$name.pdb" /map:"$OUT/$name.map" /machine:x86 /brepro /timestamp:0 "$@"
}

# C first: the smallest case, and the one the record-length conventions are checked against.
compile "$HERE/tests/corpus/sample-msvc.c" "$OUT/sample.obj"
link sample "$OUT/sample.obj"

# C++: vtables, RTTI records, member and static functions, and PDB names in their decorated form,
# so the name decoder is exercised through the whole pipeline rather than in unit tests alone.
#
# The stubs object supplies the three symbols the Microsoft runtime normally provides; type_info's
# vtable is referenced by every RTTI record and clang emits its self-reference as a `.1` COMDAT
# alias, which the linker is asked to fold onto the definition we ship.
compile "$HERE/tests/corpus/msvc-crt-stubs.cpp" "$OUT/stubs.obj"
compile "$HERE/tests/corpus/sample-msvc.cpp" "$OUT/sample-cxx.obj"
link sample-cxx "/alternatename:??_7type_info@@6B@.1=??_7type_info@@6B@" "$OUT/sample-cxx.obj" "$OUT/stubs.obj"

echo
echo "corpus written to $OUT"
ls -l "$OUT"
# A record of what this toolchain produced, so a drift shows up as a diff. It is not a lock: the PDB
# stores the absolute path of the object file it was linked from, so a different checkout produces
# different bytes from the same source.
echo
echo "sha256 (recorded in tests/tools/msvc-corpus.sha256):"
(cd "$OUT" && sha256sum sample.exe sample.pdb sample.map sample-cxx.exe sample-cxx.pdb sample-cxx.map) > "$HERE/tests/tools/msvc-corpus.sha256"
cat "$HERE/tests/tools/msvc-corpus.sha256"
