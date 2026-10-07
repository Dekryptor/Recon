#!/usr/bin/env bash
# Builds the Mach-O corpus: the same C source the PE and ELF corpora are built from, compiled by
# clang for an Apple target and linked by ld64.lld, so that the inventory has a real Mach-O image to
# be checked against the linker's own map file.
#
# Two things make this possible on a Linux machine with no SDK: clang can *compile* for an Apple
# target without one, and ld64.lld can *link* the result, as long as the C library the source uses is
# declared by hand (tests/corpus/macho-stubs/) and left for the dynamic linker to find at run time.
# None of these binaries are executed — the corpus is read, not run.
#
# Skipped, not failed, when clang or ld64.lld is missing.
#
# Usage: tools/build-macho-corpus.sh
set -uo pipefail

cd "$(dirname "$0")/.." || exit 2

OUT="tests/corpus/macho"
SRC="tests/corpus/sample.c"
STUBS="tests/corpus/macho-stubs"
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

CLANG="${CLANG:-}"
LD64="${LD64:-}"
for candidate in clang-19 clang; do
    [ -z "$CLANG" ] && command -v "$candidate" > /dev/null 2>&1 && CLANG="$candidate"
done
for candidate in ld64.lld-19 ld64.lld; do
    [ -z "$LD64" ] && command -v "$candidate" > /dev/null 2>&1 && LD64="$candidate"
done

if [ -z "$CLANG" ] || [ -z "$LD64" ]; then
    echo "note: the Mach-O corpus needs clang and ld64.lld (apt: clang-19 lld-19); skipping."
    echo "      nothing in tests/corpus/macho will be built."
    exit 0
fi

if [ ! -f "$SRC" ]; then
    echo "error: $SRC is missing" >&2
    exit 2
fi

mkdir -p "$OUT"

# The compiler wants the headers the source includes; these say what the source actually uses and
# nothing more, because there is no SDK here to say it for us.
if [ ! -f "$STUBS/stdio.h" ]; then
    echo "error: $STUBS is missing the stub headers the corpus source is compiled against" >&2
    exit 2
fi

PLATFORM="macos 12.0 12.0"

compile() {
    # compile <output object> <arch> <optimisation>
    "$CLANG" -target "$2-apple-macos12.0" "$3" -g \
        -nostdlibinc -I "$STUBS" -Wno-ignored-attributes \
        -c "$SRC" -o "$1" || return 1
}

link() {
    # link <output> <arch> <object> <kind: execute|dylib> <map>
    local kind_flags=""
    [ "$4" = "dylib" ] && kind_flags="-dylib"
    [ "$4" = "execute" ] && kind_flags="-e _main"

    "$LD64" -arch "$2" -platform_version $PLATFORM \
        $kind_flags -undefined dynamic_lookup \
        -map "$5" -o "$1" "$3" || return 1
}

# The DWARF of a Mach-O build is not in the executable: clang leaves a debug map in it and dsymutil
# collects the real thing into a .dSYM bundle beside it. The bundle can only be built while the
# object files still exist, which is why this runs here rather than in a second pass.
dsym() {
    # dsym <binary>
    local name
    name=$(basename "$1")

    command -v dsymutil-19 > /dev/null 2>&1 || return 0

    rm -rf "$1.dSYM"
    if ! dsymutil-19 "$1" -o "$1.dSYM" 2> "$WORK/dsymutil.log"; then
        rm -rf "$1.dSYM"
        echo "  $name: dsymutil failed, so no .dSYM bundle" >&2
        return 0
    fi

    # An empty bundle is worse than none: it looks like debug information that is not there. (The
    # check writes to a file rather than piping to grep -q, because grep quitting early closes the
    # pipe, which under pipefail reads as a failure of the whole command.)
    llvm-dwarfdump-19 "$1.dSYM"/Contents/Resources/DWARF/* > "$WORK/dwarf.txt" 2> /dev/null
    if ! grep -q DW_TAG_subprogram "$WORK/dwarf.txt"; then
        rm -rf "$1.dSYM"
        echo "  $name: dsymutil produced no DWARF, so no .dSYM bundle" >&2
        return 0
    fi

    echo "  $name.dSYM (the DWARF, which a Mach-O build keeps outside the image)"
}

built=0
failed=0

build() {
    # build <name> <arch> <optimisation> <kind>
    local name="$1" arch="$2" opt="$3" kind="$4"
    local object="$WORK/$name.o"

    if compile "$object" "$arch" "$opt" && link "$OUT/$name" "$arch" "$object" "$kind" "$OUT/$name.map"; then
        built=$((built + 1))
        echo "  $name ($arch, $opt, $kind)"
        dsym "$OUT/$name"
    else
        echo "  $name: FAILED to build"
        failed=$((failed + 1))
    fi
}

echo "building the Mach-O corpus with $CLANG and $LD64"

build sample-macho64 x86_64 -O2 execute
build sample-macho64-debug x86_64 -O0 execute
build libsample-macho64.dylib x86_64 -O2 dylib
build sample-macho-arm64 arm64 -O2 execute

# The stripped copy: the same image with its symbol table taken out, which is what a released
# Mach-O binary looks like.
if [ -f "$OUT/sample-macho64" ] && command -v llvm-strip-19 > /dev/null 2>&1; then
    cp "$OUT/sample-macho64" "$OUT/sample-macho64-stripped"
    llvm-strip-19 "$OUT/sample-macho64-stripped"
    cp "$OUT/sample-macho64.map" "$OUT/sample-macho64-stripped.map"
    built=$((built + 1))
    echo "  sample-macho64-stripped (stripped with llvm-strip-19)"
fi

echo
if [ "$failed" -gt 0 ]; then
    echo "macho corpus: $built built, $failed failed"
    exit 1
fi

echo "macho corpus: $built image(s) in $OUT"
exit 0
