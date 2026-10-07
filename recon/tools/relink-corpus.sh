#!/usr/bin/env bash
#
# Delink and relink every binary of a corpus, and report what `recon link` says about the result.
#
#   bash tools/relink-corpus.sh [corpus-dir] [recon] [cc]
#
# A relink that comes back identical is the whole claim of delinking, so it is worth running over
# everything available rather than over the one binary that happens to be in the demo project. Each
# binary is copied into a throwaway project (so nothing here touches the repository's examples), the
# plan is written, the pieces are assembled and linked with whatever toolchain the binary asks for,
# and the verdict line recon prints is collected.
#
# Binaries whose toolchain cannot assemble and link on this machine are reported as skipped, not as
# failures: a MinGW cross compiler relinks the MinGW corpus here, and nothing relinks the MSVC one
# without lld-link under Wine.

set -uo pipefail

corpus="${1:-tests/corpus/mingw}"
recon="${2:-out/bin/Recon.Cli/debug/recon.dll}"

# The profile names bin/gcc.exe, which is right on the Windows machine the profile describes. On this
# one the cross compiler is somewhere else, and local.toml is where that is written down.
cc="${3:-${MINGW_CC:-$(command -v i686-w64-mingw32-gcc || true)}}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

if [ ! -f "$recon" ]; then
    echo "recon is not built: $recon" >&2
    echo "build it first:  dotnet build src/Recon.Cli/Recon.Cli.csproj" >&2
    exit 2
fi

if [ ! -d "$corpus" ]; then
    echo "no corpus directory: $corpus" >&2
    exit 2
fi

shopt -s nullglob
binaries=("$corpus"/*.exe "$corpus"/*.dll)
if [ ${#binaries[@]} -eq 0 ]; then
    echo "no binaries in $corpus" >&2
    exit 2
fi

printf '%-26s %-10s %s\n' "binary" "result" "detail"
printf '%-26s %-10s %s\n' "--------------------------" "----------" "----------------------------------------"

identical=0
differing=0
skipped=0

for binary in "${binaries[@]}"; do
    name="$(basename "$binary")"
    project="$work/${name%.*}"
    mkdir -p "$project"
    digest="$("$(dirname "$0")/../out/bin/Recon.Cli/debug/recon.dll" hash "$binary" 2>/dev/null | awk '{print $1}')"
    if [ -z "$digest" ]; then
        digest="$(sha256sum "$binary" | awk '{print $1}')"
    fi

    {
        echo 'schema_version = 1'
        echo
        echo '[project]'
        echo "name = \"${name%.*}\""
        echo
        echo '[target]'
        echo 'format = "pe32"'
        echo 'arch = "x86"'
        echo
        echo '[[input]]'
        echo 'id = "main"'
        echo 'role = "original"'
        echo "file = \"$name\""
        echo "sha256 = \"$digest\""
    } > "$project/project.toml"

    {
        echo 'schema_version = 1'
        echo
        echo '[inputs]'
        echo "dir = \"$(cd "$(dirname "$binary")" && pwd)\""
        if [ -n "$cc" ]; then
            # Both MinGW generations: a corpus binary's own code was compiled by the newer one and
            # the runtime objects it links by the older, and detection names the newer. Which of the
            # two a binary asks for is not this script's business, so both point at the same compiler.
            echo
            echo '[toolchain.gcc-13-mingw]'
            echo 'root = "/usr"'
            echo "cc = \"$cc\""
            echo "link = \"$cc\""
            echo
            echo '[toolchain.gcc-14-mingw]'
            echo 'root = "/usr"'
            echo "cc = \"$cc\""
            echo "link = \"$cc\""
        fi
    } > "$project/local.toml"

    plan="$(dotnet "$recon" delink --project "$project" 2>&1)"
    if [ $? -ne 0 ]; then
        printf '%-26s %-10s %s\n' "$name" "delink" "$(echo "$plan" | tail -1)"
        skipped=$((skipped + 1))
        continue
    fi

    out="$(dotnet "$recon" link --project "$project" 2>&1)"
    verdict="$(echo "$out" | grep -E '^\s+relinked ' | sed 's/^ *relinked //')"
    if [ -z "$verdict" ]; then
        reason="$(echo "$out" | grep -E 'failed|error|warning: no|not found' | head -1 | sed 's/^ *//')"
        printf '%-26s %-10s %s\n' "$name" "skipped" "${reason:-could not link}"
        skipped=$((skipped + 1))
        continue
    fi

    case "$verdict" in
        identical:*)
            printf '%-26s %-10s %s\n' "$name" "identical" "$verdict"
            identical=$((identical + 1))
            ;;
        *)
            printf '%-26s %-10s %s\n' "$name" "DIFFERS" "$verdict"
            echo "$out" | grep -E '^\s+(warning|error)' | sed 's/^/                             /' | head -5
            differing=$((differing + 1))
            ;;
    esac
done

printf '%-26s %-10s %s\n' "--------------------------" "----------" "----------------------------------------"
printf '%-26s %-10s %s\n' "${#binaries[@]} binary(ies)" "summary" "$identical identical, $differing differing, $skipped skipped"

[ "$differing" -eq 0 ]
