# Real archives for `recon lib`

Three `!<arch>` archives, from three different producers, so that the reader is checked against files
rather than against itself. They are not part of the repository — the repository stores no binaries —
and the tests that read them skip when this directory is not there. Point `RECON_LIB_INPUTS` at it.

| file | producer | what it exercises |
| --- | --- | --- |
| `VBAEXE6.LIB` | Microsoft librarian, 1998 (ships with Visual Basic 6) | one COFF object, whose `@comp.id` reads `masm_6.13 build 7299` — the same tool the object's own string names, so the two records check each other |
| `windows.0.52.0.lib` | Microsoft librarian, from the `windows_x86_64_msvc` Rust crate | 20,551 short import records and 1,113 import-descriptor objects: the import path, and a member count that has to add up exactly |
| `libkernel32.a` | GNU ar (mingw-w64) | 1,716 COFF objects and a 37 KB long-name table: the `/N` name indirection GNU uses, and the ELF guard's counterpart |

A Unix `.a` (`/usr/lib/gcc/x86_64-linux-gnu/*/libgcc.a`) is what the ELF guard was written against;
any system GCC installation has one, so no copy is kept here.
