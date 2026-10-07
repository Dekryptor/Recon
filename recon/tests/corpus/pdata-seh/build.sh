#!/usr/bin/env bash
# Builds the pair of 64-bit MinGW images the `.pdata` item is measured on, and prints what the
# exception directory holds. Nothing here is corpus material yet: the binaries are build outputs and
# go where RECON_ARTIFACTS says, not into the repository.
#
#   bash tests/corpus/pdata-seh/build.sh /tmp/pdata
#
# Then:  recon inventory --project /tmp/pdata/proj      (the same source with DWARF)
#        recon inventory --project /tmp/pdata/strip     (the same source stripped)
set -euo pipefail
out="${1:-/tmp/pdata}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mkdir -p "$out/proj/inputs" "$out/strip/inputs"
# Reproducible the way the rest of the corpus is: same seed, no timestamp, fixed clock.
flags=(-frandom-seed=recon-corpus -Wl,--no-insert-timestamp)
export SOURCE_DATE_EPOCH=1600000000
x86_64-w64-mingw32-gcc -O2 -g -Wall "${flags[@]}" -o "$out/proj/inputs/t64.exe"   "$here/t.c"
x86_64-w64-mingw32-gcc -O2 -s -Wall "${flags[@]}" -o "$out/strip/inputs/t64s.exe" "$here/t.c"
project() { # $1 dir, $2 name, $3 file
  printf 'schema_version = 1\n\n[project]\nname = "%s"\n\n[target]\nformat = "pe64"\narch = "x64"\n\n[[input]]\nid = "main"\nrole = "original"\nfile = "%s"\nsha256 = "%s"\n' \
    "$2" "$3" "$(sha256sum "$1/inputs/$3" | cut -d' ' -f1)" > "$1/project.toml"
}
project "$out/proj"  pdata          t64.exe
project "$out/strip" pdata-stripped t64s.exe
python3 - "$out/proj/inputs/t64.exe" <<'PY'
import struct, sys
data = open(sys.argv[1], 'rb').read()
pe = struct.unpack_from('<I', data, 0x3C)[0]
optsize = struct.unpack_from('<H', data, pe + 20)[0]
nsec = struct.unpack_from('<H', data, pe + 6)[0]
secs = {}
for i in range(nsec):
    off = pe + 24 + optsize + i * 40
    name = data[off:off+8].rstrip(b'\0').decode()
    vsize, vaddr, rawsize, rawoff = struct.unpack_from('<IIII', data, off + 8)
    secs[name] = (vaddr, vsize, rawoff, rawsize)
pv, pvs, po, _ = secs['.pdata']
entries = [struct.unpack_from('<III', data, po + i*12) for i in range(pvs // 12)]
tv, tvs = secs['.text'][:2]
xv, xvs = secs['.xdata'][:2]
print(f'.text  {tvs:#x} bytes at {tv:#x}')
print(f'.pdata {pvs:#x} bytes at {pv:#x} = {len(entries)} RUNTIME_FUNCTION entries (12 bytes each, PE32+)')
print('  inside .text        :', sum(1 for b, e, u in entries if tv <= b < tv + tvs), 'of', len(entries))
print('  16-byte aligned     :', sum(1 for b, e, u in entries if b % 16 == 0), 'of', len(entries))
print('  unwind info in .xdata:', sum(1 for b, e, u in entries if xv <= u < xv + xvs), 'of', len(entries))
sizes = sorted(e - b for b, e, u in entries)
print('  extents             : min', sizes[0], 'median', sizes[len(sizes)//2], 'max', sizes[-1],
      f'| sum {sum(sizes)} bytes = {100*sum(sizes)/tvs:.1f}% of .text')
PY
echo "wrote $out/proj and $out/strip"
