#!/usr/bin/env bash
# Runs the analyzer over every binary in the corpus and verifies each inventory against the
# independent oracle (DWARF DIEs, the COFF symbol table, the linker map file, the PDB read by
# llvm-pdbutil, structural invariants). This is the acceptance harness for milestone 1: it says, per
# binary, whether the function set, the sizes, the relocations and the jump tables can be defended.
#
# Two corpora are checked: the MinGW one in tests/corpus/mingw (tools/build-corpus.sh) and the
# MSVC-ABI one in tests/corpus/msvc (tools/build-msvc-corpus.sh). The MSVC corpus needs clang and
# lld; when it is missing or llvm-pdbutil is not installed, that check is reported as a skip.
#
# Usage: tools/validate-corpus.sh [--verbose]
set -uo pipefail

cd "$(dirname "$0")/.." || exit 2
# shellcheck disable=SC1091
. tools/env.sh || exit 2

VERBOSE=${1:-}
RECON="dotnet out/bin/Recon.Cli/debug/recon.dll"
CORPUS="tests/corpus/mingw"
MSVC_CORPUS="tests/corpus/msvc"
ORACLE="tests/tools/validate_inventory.py"
PDB_ORACLE="tests/tools/validate_pdb.py"

if [ ! -d "$CORPUS" ]; then
    echo "error: $CORPUS is missing; run tools/build-corpus.sh first" >&2
    exit 2
fi

dotnet build src/Recon.Cli/Recon.Cli.csproj -v q --nologo || exit 2

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

failures=0
pdb_checked=0

# Which llvm-pdbutil the PDB oracle will find, so the summary can say whether that oracle ran
# instead of claiming it did. The candidate list is validate_pdb.py's own.
PDBUTIL="${LLVM_PDBUTIL:-}"
if [ -z "$PDBUTIL" ]; then
    for candidate in llvm-pdbutil-19 llvm-pdbutil-18 llvm-pdbutil; do
        if command -v "$candidate" > /dev/null 2>&1; then
            PDBUTIL="$candidate"
            break
        fi
    done
fi

directories=("$CORPUS")
if [ -d "$MSVC_CORPUS" ]; then
    directories+=("$MSVC_CORPUS")
else
    echo "note: $MSVC_CORPUS is not built; run tools/build-msvc-corpus.sh (needs clang and lld) to"
    echo "      check the MSVC debug path against llvm-pdbutil as well."
    echo
fi

# The ELF corpus (M6): built with the host compiler, and checked today by the loader tests rather
# than by the inventory oracle, which is still PE-only.
ELF_CORPUS="tests/corpus/elf"
if command -v gcc > /dev/null 2>&1; then
    if [ ! -d "$ELF_CORPUS" ]; then
        echo "note: $ELF_CORPUS is missing; building it with tools/build-elf-corpus.sh"
        echo
        bash tools/build-elf-corpus.sh > /dev/null || true
    fi
    echo "note: $ELF_CORPUS holds $(find "$ELF_CORPUS" -maxdepth 1 -type f ! -name '*.map' | wc -l) binary/binaries;"
    echo "      the ELF loader is checked by tests/Recon.Tests/ElfLoaderTests.cs."
    echo
else
    echo "note: gcc is not installed, so the ELF corpus (tools/build-elf-corpus.sh) is not built."
    echo
fi

# The ELF corpus is validated the same way as the PE ones: an inventory, then the map file as the
# oracle. Its binaries have no extension, so they are matched by the names the corpus builder gives
# them.
if [ -d "$ELF_CORPUS" ]; then
    directories+=("$ELF_CORPUS")
fi

# The Mach-O corpus (M6): built with clang and ld64.lld, which is how a Linux machine produces an
# Apple-targeted image at all. Skipped, not failed, when neither is installed.
MACHO_CORPUS="tests/corpus/macho"
if command -v clang-19 > /dev/null 2>&1 || command -v clang > /dev/null 2>&1; then
    if [ ! -d "$MACHO_CORPUS" ]; then
        echo "note: $MACHO_CORPUS is missing; building it with tools/build-macho-corpus.sh"
        echo
        bash tools/build-macho-corpus.sh > /dev/null || true
    fi
else
    echo "note: clang is not installed, so the Mach-O corpus (tools/build-macho-corpus.sh) is not built."
    echo
fi

if [ -d "$MACHO_CORPUS" ]; then
    echo "note: $MACHO_CORPUS holds $(find "$MACHO_CORPUS" -maxdepth 1 -type f ! -name '*.map' | wc -l) image(s)."
    echo
    directories+=("$MACHO_CORPUS")
fi

mapfile -t binaries < <(find "${directories[@]}" -maxdepth 2 -type f ! -name '*.map' \
    \( -name '*.exe' -o -name '*.dll' -o -name 'sample-elf*' -o -name 'shapes-elf*' -o -name 'sample' \
       -o -name 'sample-macho*' -o -name 'libsample-macho*' \) | sort)
for binary in "${binaries[@]}"; do
    [ -e "$binary" ] || continue
    name=$(basename "$binary")
    tag=$(basename "$binary" .exe | tr '/' '_')
    dir="$work/$name"
    mkdir -p "$dir"

    sha=$($RECON hash "$binary" | awk '{print $1}')
    read -r target_format target_arch < <(python3 tests/tools/image_format.py "$binary")

cat > "$dir/project.toml" <<EOF
schema_version = 1

[project]
name = "${name%.*}-${tag%.*}"

[target]
format = "$target_format"
arch = "$target_arch"

[[input]]
id = "main"
role = "original"
file = "$name"
sha256 = "$sha"

[paths]
profiles = ["$(pwd)/src/Recon.Core/Toolchains/profiles"]
EOF
    pdb="${binary%.*}.pdb"
    if [ -f "$pdb" ]; then
        pdb_sha=$($RECON hash "$pdb" | awk '{print $1}')
        cat >> "$dir/project.toml" <<EOF

[[input]]
id = "debug"
role = "debug"
file = "$(basename "$pdb")"
sha256 = "$pdb_sha"
EOF
    fi

    binary_dir=$(cd "$(dirname "$binary")" && pwd)
    cat > "$dir/local.toml" <<EOF
schema_version = 1

[inputs]
dir = "$binary_dir"
EOF

    echo "=============================================================== ${tag}"
    # --check-schema is not optional here: the inventory contract is a published JSON schema, and an
    # enum that is missing a value the tool actually emits (an ELF symbol source, say) is a defect
    # that only shows up under a schema check. The oracle checks the analysis; this checks the shape.
    if ! $RECON inventory --project "$dir" --quiet --check-schema; then
        echo "FAIL: inventory failed for $name"
        failures=$((failures + 1))
        continue
    fi

    map="${binary%.*}.map"
    oracle_args=("$dir/build/inventory.json" "$binary")
    [ -f "$map" ] && oracle_args+=(--map "$map")
    [ "$VERBOSE" = "--verbose" ] || oracle_args+=(--quiet)

    if ! python3 "$ORACLE" "${oracle_args[@]}"; then
        echo "FAIL: the oracle rejected the inventory of $name"
        failures=$((failures + 1))
    fi

    if [ -f "$pdb" ]; then
        pdb_checked=$((pdb_checked + 1))
        pdb_args=("$dir/build/inventory.json" "$binary" "$pdb")
        [ -n "${LLVM_PDBUTIL:-}" ] && pdb_args+=(--pdbutil "$LLVM_PDBUTIL")
        [ "$VERBOSE" = "--verbose" ] || pdb_args+=(--quiet)

        if ! python3 "$PDB_ORACLE" "${pdb_args[@]}"; then
            echo "FAIL: llvm-pdbutil disagrees with the inventory of $name"
            failures=$((failures + 1))
        fi
    fi
done

# The M5 gate: every corpus binary cut into pieces and put back at the same addresses. Skipped, not
# failed, when this machine has no cross compiler — the inventory checks above never needed one.
if command -v i686-w64-mingw32-gcc > /dev/null 2>&1; then
    echo
    echo "=============================================================== relink"
    if ! bash tools/relink-corpus.sh; then
        echo "FAIL: a corpus binary did not relink into the same image"
        failures=$((failures + 1))
    fi
else
    echo
    echo "note: no i686-w64-mingw32-gcc; the relink sweep (tools/relink-corpus.sh) is skipped."
fi

# What the summary says about the PDB oracle has to be what happened: it is skipped, not run, when
# llvm-pdbutil is not installed, and a run that was skipped must not be counted as a check.
if [ "$pdb_checked" -eq 0 ]; then
    pdb_note="no PDB in the corpus"
elif [ -n "$PDBUTIL" ]; then
    pdb_note="PDB oracle on ($pdb_checked binary/binaries)"
else
    pdb_note="PDB oracle skipped: llvm-pdbutil is not installed"
fi

echo
if [ "$failures" -eq 0 ]; then
    echo "corpus validation: ok (${#binaries[@]} binaries, $pdb_note)"
else
    echo "corpus validation: $failures failure(s)"
fi
exit $((failures > 0))
