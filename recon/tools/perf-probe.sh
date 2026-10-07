#!/usr/bin/env bash
# The large-input probe: one real 11.8 MB Visual Basic 6 program, analysed from scratch, with the
# phase report. Everything it writes goes to /tmp — an inventory of this program is 71 MB, which is
# more than half the workspace budget, and `/tmp` is throwaway by construction.
#
#   bash tools/perf-probe.sh              # set up, build the inventory, print the phases
#   bash tools/perf-probe.sh --listing    # time an interactive listing against that inventory
#   bash tools/perf-probe.sh --fresh      # delete the inventory first: analysis, not the cache
#   bash tools/perf-probe.sh --walk       # a listing per address, the shape of a reversing session
#
# The binary is not in the repository (it is a real program, not a fixture). Point RECON_VB6_WILD at
# a directory holding `ElementEvil.exe`, or put one there.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
INPUTS="${RECON_VB6_WILD:-/home/user/vb6-wild}"
BINARY="$INPUTS/ElementEvil.exe"
WORK="${RECON_PERF_DIR:-/tmp/big}"
DOTNET="${DOTNET_ROOT:-/opt/dotnet}/dotnet"
# Where the build output is: `tools/env.sh` moves it out of the workspace in this environment, so ask
# the same way it does rather than assuming `out/`.
CLI="${RECON_ARTIFACTS:-$HERE/out}/bin/Recon.Cli/debug/recon.dll"

[ -x "$DOTNET" ] || { echo "error: no dotnet at $DOTNET — run bootstrap.sh" >&2; exit 1; }
[ -f "$CLI" ] || { echo "error: the CLI is not built at $CLI — run bootstrap.sh" >&2; exit 1; }
[ -f "$BINARY" ] || { echo "error: no $BINARY — set RECON_VB6_WILD to a directory holding it" >&2; exit 1; }

mkdir -p "$WORK"
ln -sf "$BINARY" "$WORK/original.exe"

# A project over one binary, with the hash the tool will check. `local.toml` says where inputs are
# found; the input is a symlink so the workspace does not hold a second copy of 11.8 MB.
SHA="$(sha256sum "$BINARY" | cut -d' ' -f1)"
cat > "$WORK/project.toml" <<EOF
schema_version = 1

[project]
name = "perf-probe"
description = "The large-input probe: ElementEvil.exe"

[target]
format = "pe32"
arch = "x86"
isa = "x86"

[[input]]
id = "main"
role = "original"
file = "original.exe"
sha256 = "$SHA"

[paths]
source = "src"
include = ["include"]
build = "build"
profiles = ["profiles"]

[analysis]
min_function_confidence = "low"
EOF
cat > "$WORK/local.toml" <<'EOF'
schema_version = 1

[inputs]
dir = "."
EOF

RECON=("$DOTNET" "$CLI")

if [ "${1:-}" = "--fresh" ]; then
  rm -f "$WORK/build/inventory.json"
fi

echo "== inventory ($(stat -c%s "$BINARY") bytes, $(du -h "$BINARY" | cut -f1))"
mkdir -p "$WORK/build"
"${RECON[@]}" inventory --project "$WORK" --verbose -o "$WORK/build/inventory.json"

if [ "${1:-}" = "--listing" ] || [ "${1:-}" = "--walk" ]; then
  echo
  echo "== listings (the interactive path)"
  if [ "${1:-}" = "--walk" ]; then
    # Twenty addresses across the image, the way a session moves through it. The first listing is
    # the cold one; the rest is what a session costs. (`/usr/bin/time` is not in this container, so
    # the clock is the shell's.)
    for i in $(seq 0 19); do
      rva=$(printf '0x%x' $((0x401000 + i * 0x20000)))
      started=$(date +%s%N)
      "${RECON[@]}" disasm "$rva" --count 4 --project "$WORK" >/dev/null 2>&1
      printf '%-12s %d ms\n' "$rva" $(( ($(date +%s%N) - started) / 1000000 ))
    done
  else
    time "${RECON[@]}" disasm 0x471000 --count 4 --project "$WORK"
  fi
fi

# The comparison is the other thing a big binary is asked for, and it is the one that used to die:
# `recon diff` on this program was killed by the kernel with nothing printed. --diff measures it with
# the stage report, and gets out of the way of the kernel's patience by not running twice.
if [ "${1:-}" = "--diff" ]; then
  echo
  echo "== comparison (this program against itself: every function must match)"
  time "${RECON[@]}" diff --project "$WORK" "$BINARY" --summary --verbose
fi
