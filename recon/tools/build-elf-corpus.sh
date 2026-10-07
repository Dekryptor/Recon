#!/usr/bin/env bash
# Builds the ELF test corpus with the host compiler. The corpus is generated, never committed:
# the tool's own repository stores no binaries.
#
#   bash tools/build-elf-corpus.sh [output-directory]
#
# Everything here is built from the same sources as the PE corpus (tests/corpus/sample.c and
# sample.cpp), so a function that the analyser finds in one format is the same function it has to
# find in the other. x86-64 is the default because it is what a Linux box has; the 32-bit variants
# are built too when the compiler can link them (Debian: apt install gcc-multilib g++-multilib).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${1:-$HERE/tests/corpus/elf}"
CC="${CC:-gcc}"
CXX="${CXX:-g++}"

if ! command -v "$CC" >/dev/null 2>&1; then
  echo "error: $CC not found." >&2
  exit 1
fi

mkdir -p "$OUT"

# Reproducible output: without this the build id and the ordering change on every run, which would
# invalidate any hash or golden checked in against the corpus.
export SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-1600000000}"
export LC_ALL=C
CFLAGS_REPRO=(-frandom-seed=recon-corpus)
LDFLAGS_REPRO=(-Wl,--build-id=sha1)

built=0

# $1 = output name, $2 = compiler, rest = flags and sources.
build() {
  local name="$1"; shift
  local compiler="$1"; shift
  echo "building $name"
  if "$compiler" "$@" "${CFLAGS_REPRO[@]}" "${LDFLAGS_REPRO[@]}" \
      -o "$OUT/$name" -Wl,-Map="$OUT/$name.map" >/dev/null 2>&1; then
    built=$((built + 1))
  else
    echo "  skipped: this compiler cannot build $name here"
    rm -f "$OUT/$name" "$OUT/$name.map"
  fi
}

# Debug builds: symbols and no optimisation — the case where debug info is the oracle.
build sample-elf64-debug   "$CC"  -O0 -g     "$HERE/tests/corpus/sample.c"
# Release builds: optimised, with debug info kept.
build sample-elf64-release "$CC"  -O2 -g     "$HERE/tests/corpus/sample.c"
# The worst case: no debug info and no symbol table. The map file, written at link time, is the
# only ground truth left.
build sample-elf64-stripped "$CC" -O2 -s     "$HERE/tests/corpus/sample.c"
# C++: mangled names, a vtable and a jump table.
build shapes-elf64-debug   "$CXX" -O0 -g     "$HERE/tests/corpus/sample.cpp"
build shapes-elf64-release "$CXX" -O2 -g     "$HERE/tests/corpus/sample.cpp"
# A shared object: exports, PIC and relocations that are not against the executable's own code.
build shapes-elf64.so      "$CXX" -O2 -g -shared -fPIC -Wl,-soname,shapes-elf64.so "$HERE/tests/corpus/sample.cpp"

# Clang, when it is installed: a second producer over the same source.
if command -v clang >/dev/null 2>&1; then
  build sample-elf64-clang clang -O2 -g "$HERE/tests/corpus/sample.c"
else
  echo "building sample-elf64-clang"
  echo "  skipped: clang is not installed"
fi

# 32-bit, when the multilib runtimes are installed.
if printf 'int main(void){return 0;}\n' > "$OUT/.m32-probe.c" &&
   "$CC" -m32 "$OUT/.m32-probe.c" -o "$OUT/.m32-probe" >/dev/null 2>&1; then
  rm -f "$OUT/.m32-probe" "$OUT/.m32-probe.c"
  build sample-elf32-release "$CC" -m32 -O2 -g "$HERE/tests/corpus/sample.c"
  build shapes-elf32.so      "$CXX" -m32 -O2 -g -shared -fPIC -Wl,-soname,shapes-elf32.so "$HERE/tests/corpus/sample.cpp"
else
  rm -f "$OUT/.m32-probe" "$OUT/.m32-probe.c"
  echo "building the 32-bit variants"
  echo "  skipped: $CC -m32 cannot link here (Debian: apt install gcc-multilib g++-multilib)"
fi

# A copy of the stripped binary with no sidecar at all: every function boundary has to come from
# the code itself.
mkdir -p "$OUT/blind"
if [ -f "$OUT/sample-elf64-stripped" ]; then
  cp "$OUT/sample-elf64-stripped" "$OUT/blind/sample"
fi

echo
echo "corpus written to $OUT ($built binary/binaries)"
ls -l "$OUT"
