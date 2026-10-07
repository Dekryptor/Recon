# The Rich header

An undocumented block the Microsoft linker writes between the DOS stub and the PE header: a bill of
materials of the object files that went into the image. It is evidence of which tool builds produced
those objects — nothing more — and `recon inspect producers` prints it as `rich_header` rows.

## Layout

```
DanS            magic, XORed with the key
00 00 00 00     three padding dwords, XORed with the key
(id<<16|build)  one 8-byte record per tool build, XORed with the key
count           ... and how many object files that build contributed
Rich            magic, plain
key             the XOR key, plain
```

Everything from `DanS` up to `Rich` is XORed with the key, **the records included**. This is the one
thing every description of the structure agrees on, and a real file settles it beyond argument: read
the records without the key, the counts come out in the billions and not one product id is known. The
tool got this wrong once — it decrypted the marker block and read the records as plain text — and the
first real file it met showed it.

`build` is the low 16 bits of that tool's build number, so a record identifies a tool release, not a
version: `0x0004` at build 8447 is the Visual Studio 98 linker at build 8447.

## What real files look like

VISDATA.EXE, the data-manager sample from Visual Basic 6 (1998), XOR key `0x8917A385`:

| product id | tool | build | count | |
| --- | --- | --- | --- | --- |
| `0x000E` | MASM 6.13 | 7299 | 1 | one assembler object |
| `0x0009` | the VB6 Basic compiler | 8041 | 36 | a program's worth of forms and modules |
| `0x000D` | Visual Basic 6 | 8167 | 1 | the link step, and the last record |

LINK.EXE from Visual Studio 98 (1999), XOR key `0x5C8C6416`: nine records, among them
`0x000A` (C) at build 8447 with 11 objects, `0x000B` (C++) at build 8447 with 63,
`0x0004` (**the linker**) at build 8447 with 5, `0x0001` (imported symbols) with 168, and — last —
`0x0006`, the resource converter, with 1.

Those two are why the tool does not treat the last record as the linker. It usually is, and in these
files it never is: a Visual Studio 98 binary ends with `cvtres`, a Visual Basic 6 program ends with
the Basic compiler and carries no linker record at all. Which record is the linker is read from the
product id (`RichHeader.LinkerProdIds`), and a header without one reports `LinkerEntry` as null.

## Product ids

The ids this tool names, and where the names come from, are listed in
[`toolchains.md`](toolchains.md#the-product-id-table). Anything outside that table is reported as
`tool_0xNNNN` rather than guessed at: `0x0000` (objects carrying no `@comp.id` at all) and `0x000C`
(alias objects) both turn up in real Visual Studio 98 files and are named only because they were
measured there.

## Reading it

```
recon inspect producers --project <project>          # the rows, with the product ids and counts
recon inspect producers --project <project> --json   # the same, with the fingerprint and XOR key
```

The `rich_header_summary` row carries the XOR key, the number of records and a SHA-256 fingerprint of
the decrypted header — a stable handle for asking whether two binaries came out of the same build
environment.
