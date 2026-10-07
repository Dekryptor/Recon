namespace Recon.Cli;

public sealed record CommandSpec(string Name, string Usage, string Summary, string[] Options);

/// <summary>
/// The single source of truth for the command surface: <c>--help</c> and the generated command
/// reference (docs/cli.md) are both produced from this table, so they cannot drift apart.
/// </summary>
public static class CommandSpecs
{
    public static readonly CommandSpec[] All =
    [
        new("init", "recon init [dir] [--name=NAME] [--binary=PATH] [--no-profiles] [--force] [--check-schema] [--json]",
            "Create project.toml, local.toml and the profile directory, optionally hashing a binary.",
            ["--name=NAME", "--binary=PATH", "--no-profiles", "--force", "--check-schema", "--json"]),

        new("verify", "recon verify [--json] [--no-toolchains]",
            "Check that every input exists and matches its recorded SHA-256, and that debug info matches the binary.",
            ["--json", "--no-toolchains"]),

        new("validate", "recon validate [--inventory=PATH] [--json]",
            "Validate project.toml, local.toml and profiles; optionally validate an inventory against its schema.",
            ["--inventory=PATH", "--json"]),

        new("doctor", "recon doctor [--json]",
            "Diagnose the environment: inputs, hashes, binary readability, debug info identity, toolchain installs.",
            ["--json"]),

        new("inspect", "recon inspect <sections|imports|exports|relocs|data|functions|xrefs|producers|debug|tls|stats> [--limit=N] [--filter=TEXT] [--all] [--json]",
            "Human-readable views over the same inventory the other commands use.",
            ["--limit=N", "--filter=TEXT", "--all", "--json"]),

        new("inventory", "recon inventory [--project=PATH] [<input>] [-o PATH] [--check-schema] [--no-xrefs] [--signatures=PATH] [--timestamp] [--json]",
            "Build the inventory JSON (the contract later milestones compare against). One inventory shape for every instruction set: a Visual Basic 6 program compiled to p-code is inventoried from its own method tables — one function per procedure, with the extent its descriptor states — and its instructions are counted against the runtime that interprets them when one can be found (named by --runtime, beside the program, or in the project's input directory); without a runtime the procedures are still listed and the gap is reported rather than filled in.",
            ["-o PATH", "--check-schema", "--no-xrefs", "--signatures=PATH", "--timestamp", "--json"]),

        new("sigs", "recon sigs <build|apply> [-o PATH] [--signatures=PATH] [--library=NAME] [--unit=TEXT] [--length=N] [--min-fixed=N] [--json]",
            "Name functions from patterns: build them from a binary the tool can read, then apply them to one it cannot.",
            ["-o PATH", "--signatures=PATH", "--library=NAME", "--unit=TEXT", "--length=N", "--min-fixed=N", "--json"]),

        new("disasm", "recon disasm <rva|name|substring> [--count=N] [--json]",
            "Disassemble at an RVA, or at the start of the function with the given name. A listing needs three things from an inventory — where the functions are, what the imports are called, what the data is called — and reads them from the inventory on disk when it is this build's reading of this binary, which is what makes listing instructions in a session fast; otherwise it runs the analysis.",
            ["--count=N", "--json"]),

        new("toolchain", "recon toolchain <list|show|detect|check|builtins> [id] [--raw] [--install-dir=PATH] [--json]",
            "Inspect toolchain profiles, resolve inheritance, check installs, and see detection evidence.",
            ["--raw", "--install-dir=PATH", "--json"]),

        new("build", "recon build [--unit=NAME] [--jobs=N] [--dry-run] [--force] [--keep-going] [--no-ninja] [--check-schema] [--json]",
            "Run the reconstruction's own build: compile each unit with its toolchain, link, and report the result.",
            ["--unit=NAME", "--jobs=N", "--dry-run", "--force", "--keep-going", "--no-ninja", "--check-schema", "--json"]),

        new("permute", "recon permute [--function=NAME] [--unit=NAME] [--budget=N] [--flags] [--no-stop-on-exact] [--verbose] [-o DIR] [--check-schema] [--json]",
            "Search for the source that reproduces a function: rebuild equivalent variants and score each against the original.",
            ["--function=NAME", "--unit=NAME", "--budget=N", "--flags", "--no-stop-on-exact", "--verbose", "-o DIR", "--check-schema", "--json"]),

        new("diff", "recon diff [left] [right] [-o PATH] [--threshold=F] [--min-score=F] [--function=NAME] [--summary] [--check-schema] [--json]",
            "Compare two builds: pair functions, normalize relocations, score and report differences. An address that moved is not a difference — an operand is compared as what it points at, a branch that stays inside a function by its offset from that function, and an immediate that lands on something the symbol table names as an address whether or not the linker relocated it — so two builds of one source score 1 even when the linker moved every function. --verbose reports what each stage cost; the function bodies are made when asked for and not kept, which is what lets a comparison of an 11.8 MB program finish in a gigabyte. A side that `recon inventory` has already written is read back rather than analysed again when the document is this build's reading of that binary, and each side's `inventory_source` in the report says which of the two happened.",
            ["-o PATH", "--threshold=F", "--min-score=F", "--function=NAME", "--summary", "--check-schema", "--json"]),

        new("report", "recon report [left] [right] [--comparison=PATH] [-o DIR] [--aligned] [--no-history] [--check-schema] [--json]",
            "Measure how far the reconstruction is: per-unit progress, a history snapshot, and the progress site.",
            ["--comparison=PATH", "-o DIR", "--aligned", "--no-history", "--check-schema", "--json"]),

        new("serve", "recon serve [left] [right] [--comparison=PATH] [--port=N]",
            "Serve the diff viewer locally: browse the comparison function by function while you work.",
            ["--comparison=PATH", "--port=N"]),

        new("delink", "recon delink [-o DIR] [--check-schema] [--json]",
            "Cut the original into pieces a linker can put back at the same addresses, with a provider for each.",
            ["-o DIR", "--check-schema", "--json"]),

        new("link", "recon link [-o FILE] [--dry-run] [--json]",
            "Relink the delinked pieces at their original addresses: a half-reconstructed image that still links. A piece a unit claims with `provider = \"rebuilt\"` is filled from that unit's own compiled object — its bytes at the piece's address, its addresses written as references to the names the image defines — and a claim that cannot be placed keeps the original's bytes and says why.",
            ["-o FILE", "--dry-run", "--json"]),

        new("pcode", "recon pcode <program.exe> [--runtime=PATH] [--object=NAME] [--procedure=N] [--json] [--check-schema]",
            "Read a Visual Basic 6 program compiled to p-code: every procedure, and the instruction stream the interpreter runs. Selecting one lists it the way a disassembler lists machine code — the bytes as they lie in the stream, the operand as the runtime's own handler reads it (none, data, a frame slot, or a branch that goes to the procedure's code address plus the word), where a branch goes and whether that is an instruction start, and the frame slot a For/Next counts.",
            ["--runtime=PATH", "--object=NAME", "--procedure=N", "--json", "--check-schema"]),

        new("opcodes", "recon opcodes <msvbvm60.dll> [--opcode <n>] [--lead <n>] [--json] [--check-schema]",
            "Read a Visual Basic runtime's p-code opcode tables: the handler each opcode dispatches to, what that handler calls, how many slots of the operand stack it moves, whether it can raise, and a name when the evidence gives exactly one. A runtime with lead bytes has six tables — the primary one and one per lead byte, indexed by the byte after it — and all of them are published. The raiser every `raises` is measured against is named in the report by its rva.",
            ["--opcode=N", "--lead=N", "--json", "--check-schema"]),

        new("vb6", "recon vb6 <file.exe> [--json] [--check-schema]",
            "Read a Visual Basic 5/6 program's structure: the VB5! header, the project data behind it, and the objects (forms, classes, modules) the project is made of.",
            ["--json", "--check-schema"]),

        new("lib", "recon lib <file.lib> [--json] [--check-schema]",
            "List the members of a COFF archive (a .lib): the objects a link can pull in, the imports an import library describes, and the compiler each object was built with.",
            ["--json", "--check-schema"]),

        new("hash", "recon hash <file> [...]",
            "Print SHA-256 digests, ready to paste into project.toml.",
            []),

        new("migrate", "recon migrate [--check] [--write] [--check-schema] [--json]",
            "Report (or apply) schema_version upgrades for older configuration files.",
            ["--check", "--write", "--check-schema", "--json"]),

        new("schema", "recon schema <list|show|export> [name] [--dir=PATH]",
            "Show or export the JSON Schemas this build validates against.",
            ["--dir=PATH"]),

        new("gen-docs", "recon gen-docs [-o docs/cli.md]",
            "Write the generated command reference.",
            ["-o PATH"]),
    ];

    public static CommandSpec? Find(string name) => All.FirstOrDefault(s => s.Name == name);
}
