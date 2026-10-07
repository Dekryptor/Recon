# Real Visual Basic 6 programs, from the wild

Not part of the repository — it stores no binaries — and the tests that read them skip when this
directory is not there. Point `RECON_VB6_WILD` at it.

These are the programs the VB6 structure reader is checked against, because a structure read out of a
file built by VB6 in 1998 has the compiler's own opinion about where every field is, which no fixture
can have. All three are **native** compilations; no p-code program could be obtained, which is why the
p-code half of the reader is written but not measured.

| file | objects | what it is |
| --- | --- | --- |
| `ElementEvil.exe` | 73 — 39 forms, 9 classes, 25 modules | a VB6 game client; also the binary the decoding-memory work was measured on (11.2 MB `.text`, 4.4 M instructions) |
| `XiasporaServer.exe` | 10 — 1 form, 9 modules | a VB6 server, with `Winsock` and `DataBase` among its modules |
| `Basic Server.exe` | 2 — 1 form, 1 module | small enough to check by hand, which is what it is here for |

`ElementEvil.exe` and `XasporaServer.exe` were recovered from a VB6 sample upload; `Basic Server.exe`
comes with them. They are not run by anything in this repository — the tool reads binaries, never
executes them.
