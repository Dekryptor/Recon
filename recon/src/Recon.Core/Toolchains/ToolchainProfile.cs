using Recon.Config;
using Recon.Toml;

namespace Recon.Toolchains;

/// <summary>A rule that matches one entry of a PE Rich header.</summary>
public sealed class RichHeaderRule
{
    /// <summary>Product id as it appears in the Rich header (low 16 bits of the entry id).</summary>
    public uint Id { get; set; }

    /// <summary>Label used in reports, for example <c>cl</c>, <c>link</c>, <c>masm</c>.</summary>
    public string Tool { get; set; } = string.Empty;

    /// <summary>Optional: <c>linker</c> matches only the final entry, <c>compiler</c> only the others.</summary>
    public string? Role { get; set; }

    public int BuildMin { get; set; }

    public int BuildMax { get; set; } = int.MaxValue;
}

/// <summary>Matches the optional header's linker version, for example <c>9.0</c> for Visual C++ 2008.</summary>
public sealed class LinkerVersionRule
{
    public string? Min { get; set; }

    public string? Max { get; set; }
}

/// <summary>Matches the name of an imported DLL, for example <c>msvcr90</c>.</summary>
public sealed class ImportDllRule
{
    public string Contains { get; set; } = string.Empty;
}

/// <summary>
/// Matches a section by name. Presence of <c>.eh_frame</c> (GCC-style unwinding) or absence of
/// <c>.eh_frame</c> in a PE32 image says a lot about the toolchain that produced it.
/// </summary>
public sealed class SectionRule
{
    public string Name { get; set; } = string.Empty;

    /// <summary>When true the rule matches only if the section is absent.</summary>
    public bool Absent { get; set; }
}

/// <summary>
/// Matches what a Visual Basic 5/6 program's own header says it is. This is not one signal among
/// several: a VB6 program compiles either to native x86 or to p-code an interpreter runs, the header
/// states which, and a profile that describes one of them must not be suggested for the other — a
/// p-code program has no compiled functions for a native profile's padding or alignment rules to
/// describe, and a native one is not a stream of tokens. A profile that declares this rule is
/// therefore only ever suggested for the kind it names.
/// </summary>
public sealed class Vb6HeaderRule
{
    /// <summary><c>pcode</c> or <c>native</c>.</summary>
    public string Kind { get; set; } = string.Empty;
}

public sealed class CompilandRule
{
    /// <summary>Substring matched against the PDB compiland producer string.</summary>
    public string ProducerContains { get; set; } = string.Empty;
}

public sealed class CommentRule
{
    /// <summary>Substring matched against a <c>.comment</c> (or equivalent) string.</summary>
    public string Contains { get; set; } = string.Empty;
}

public sealed class DwarfRule
{
    /// <summary>Substring matched against a DWARF <c>DW_AT_producer</c> value.</summary>
    public string ProducerContains { get; set; } = string.Empty;
}

public sealed class DetectSpec
{
    public List<RichHeaderRule> RichHeader { get; set; } = [];

    public List<Vb6HeaderRule> Vb6Header { get; set; } = [];

    public List<CompilandRule> PdbCompiland { get; set; } = [];

    public List<CommentRule> CommentSection { get; set; } = [];

    public List<DwarfRule> DwarfProducer { get; set; } = [];

    public List<LinkerVersionRule> LinkerVersion { get; set; } = [];

    public List<ImportDllRule> ImportDll { get; set; } = [];

    public List<SectionRule> Sections { get; set; } = [];
}

public sealed class AbiSpec
{
    public string Mangling { get; set; } = "none";

    public string DefaultCc { get; set; } = "cdecl";

    public string MemberCc { get; set; } = "thiscall";

    public int PointerSize { get; set; } = 4;
}

public sealed class CodegenSpec
{
    public int FunctionAlign { get; set; } = 16;

    public List<byte> PaddingBytes { get; set; } = [0xCC];

    public List<string> PrologueHints { get; set; } = [];
}

public sealed class LinkerSpec
{
    public string IdenticalCodeFolding { get; set; } = "never";

    public bool Comdat { get; set; } = true;
}

public sealed class EhSpec
{
    public string Model { get; set; } = "none";
}

/// <summary>
/// How the compare engine should read a binary built by this toolchain, so the noise a toolchain
/// produces is data rather than a special case in code (plan section 3.1). Anything left unset here
/// uses the engine's default.
/// </summary>
public sealed class CompareSpec
{
    /// <summary>Two bodies scoring below this are not paired by similarity.</summary>
    public double? SimilarityThreshold { get; set; }

    /// <summary>Whether the toolchain's alignment padding is ignored at function boundaries.</summary>
    public bool? IgnorePadding { get; set; }

    /// <summary>Differences reported per function in the comparison document.</summary>
    public int? DifferenceLimit { get; set; }
}

public sealed class DebugSpec
{
    /// <summary>One of <c>pdb</c>, <c>dwarf</c>, <c>map</c>, <c>none</c>, or a comma separated list.</summary>
    public string Format { get; set; } = "none";

    public bool Has(string format) => Format
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Contains(format, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The optimization levels this toolchain's compiler can be asked for, spelled the way it spells
/// them — <c>-O2</c> for GCC and Clang, <c>/O2</c> for MSVC. Which build made a binary is a question
/// about flags as much as about source, and the answer to "try them all" is only checkable if the
/// levels come from the profile rather than from a list in the code that guesses which compiler it
/// is looking at.
/// </summary>
public sealed class OptimizationSpec
{
    /// <summary>Every level, in the order a search should try them: least to most optimized.</summary>
    public List<string> Levels { get; set; } = [];
}

public sealed class CompileSpec
{
    public string Exe { get; set; } = string.Empty;

    public List<string> DefaultFlags { get; set; } = [];

    public string IncludeFlag { get; set; } = string.Empty;

    public string DefineFlag { get; set; } = string.Empty;

    public string OutputFlag { get; set; } = string.Empty;

    /// <summary>
    /// How to ask for a dependency file, for example <c>-MMD -MF {dep}</c>. A toolchain without one
    /// still builds; its units then fall back to hashing the include directories for the cache.
    /// </summary>
    public string DepfileFlag { get; set; } = string.Empty;

    /// <summary>How this compiler spells each optimization level. Empty when the profile says nothing about it.</summary>
    public OptimizationSpec Optimization { get; set; } = new();

    public Dictionary<string, string> Env { get; set; } = [];
}

public sealed class LinkSpec
{
    public string Exe { get; set; } = string.Empty;

    public List<string> DefaultFlags { get; set; } = [];

    /// <summary>How to name the output, for example <c>-o {exe}</c> or <c>/OUT:{exe}</c>.</summary>
    public string OutputFlag { get; set; } = string.Empty;
}

public sealed class InstallFileSpec
{
    /// <summary>Path relative to the install root.</summary>
    public string Path { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;
}

public sealed class InstallSpec
{
    public List<InstallFileSpec> Files { get; set; } = [];
}

/// <summary>
/// A toolchain profile: a data file describing one compiler/runtime/linker combination, read by the
/// core rather than compiled into it. Profiles may extend another profile of the same family
/// (single inheritance, tables merge key by key, arrays replace).
/// </summary>
public sealed class ToolchainProfile
{
    /// <summary>Key paths this profile set itself, which decides what inheritance overrides.</summary>
    private HashSet<string> _explicit = new(StringComparer.Ordinal);

    public int SchemaVersion { get; set; }

    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Family { get; set; } = string.Empty;

    public string? Extends { get; set; }

    public bool IsAbstract { get; set; }

    public List<TargetSpec> Targets { get; set; } = [];

    public AbiSpec Abi { get; set; } = new();

    public CodegenSpec Codegen { get; set; } = new();

    public LinkerSpec Linker { get; set; } = new();

    public EhSpec Eh { get; set; } = new();

    public CompareSpec Compare { get; set; } = new();

    public DebugSpec Debug { get; set; } = new();

    public DetectSpec Detect { get; set; } = new();

    public CompileSpec? Compile { get; set; }

    public LinkSpec? Link { get; set; }

    public InstallSpec? Install { get; set; }

    public string FilePath { get; set; } = string.Empty;

    /// <summary>Ids from the base profile down to this one, after inheritance has been resolved.</summary>
    public List<string> InheritanceChain { get; set; } = [];

    /// <summary>
    /// <c>sysv</c> is the x86-64 System V convention: arguments in registers, caller cleans up
    /// nothing. It is not one of the 32-bit names, and calling it cdecl would be wrong.
    /// </summary>
    public static readonly string[] KnownCallingConventions =
        ["cdecl", "stdcall", "fastcall", "thiscall", "vectorcall", "sysv", "unknown"];

    public static readonly string[] KnownManglings = ["msvc", "itanium", "watcom", "borland", "none"];

    /// <summary>
    /// The kind of Visual Basic program this profile describes — <c>pcode</c> or <c>native</c>, from
    /// its <see cref="DetectSpec.Vb6Header"/> rule — or null when the profile is not about a Visual
    /// Basic program at all.
    /// </summary>
    public string? Vb6CodeKind => Detect.Vb6Header
        .Select(rule => rule.Kind)
        .FirstOrDefault(kind => kind.Length > 0);

    public bool IsExplicit(string keyPath) => _explicit.Contains(keyPath);

    public IReadOnlyCollection<string> ExplicitKeys => _explicit;

    /// <summary>
    /// The optimization levels this profile's compiler can be asked for. Empty for a profile that
    /// says nothing about them — a Visual Basic 6 profile has no [compile] section at all, and "no
    /// levels" is the honest answer rather than a guess at what its compiler might accept.
    /// </summary>
    public IReadOnlyList<string> OptimizationLevels => Compile?.Optimization?.Levels ?? [];

    /// <summary>
    /// Which of this profile's levels the given compile flags ask for, or null when the profile
    /// declares none. When a list carries more than one — the profile's <c>-O2</c> and the project's
    /// <c>-O0</c>, say — the last one is the answer, because that is the one a compiler acts on.
    /// Reporting the first would describe a build that is not the build these flags produce.
    /// </summary>
    public string? OptimizationLevelOf(IEnumerable<string> flags)
    {
        var levels = OptimizationLevels;
        return levels.Count == 0
            ? null
            : flags.LastOrDefault(flag => levels.Contains(flag, StringComparer.Ordinal));
    }

    /// <summary>
    /// The given compile flags asking for <paramref name="level"/> instead of whatever level they ask
    /// for now. The substitution happens in place of the level that was in force, so a list that
    /// carries two keeps the one it was built with and changes the one the compiler would have acted
    /// on; a list with no level at all gets one appended, which is what the compiler would do anyway.
    /// A profile that declares no levels is left alone: appending a flag this toolchain has never
    /// claimed to understand would be a guess with a compiler invocation wrapped around it.
    /// </summary>
    public List<string> WithOptimizationLevel(IEnumerable<string> flags, string level)
    {
        var result = flags.ToList();
        if (OptimizationLevels.Count == 0)
        {
            return result;
        }

        if (OptimizationLevelOf(result) is { } current)
        {
            result[result.LastIndexOf(current)] = level;
            return result;
        }

        result.Add(level);
        return result;
    }

    public bool AppliesTo(string format, string arch)
        => Targets.Count == 0
           || Targets.Any(t => string.Equals(t.Format, format, StringComparison.OrdinalIgnoreCase)
                               && string.Equals(t.Arch, arch, StringComparison.OrdinalIgnoreCase));

    public ToolchainProfile Clone()
    {
        var clone = (ToolchainProfile)MemberwiseClone();
        clone._explicit = new HashSet<string>(_explicit, StringComparer.Ordinal);
        clone.Abi = new AbiSpec { Mangling = Abi.Mangling, DefaultCc = Abi.DefaultCc, MemberCc = Abi.MemberCc, PointerSize = Abi.PointerSize };
        clone.Codegen = new CodegenSpec
        {
            FunctionAlign = Codegen.FunctionAlign,
            PaddingBytes = [.. Codegen.PaddingBytes],
            PrologueHints = [.. Codegen.PrologueHints],
        };
        clone.Linker = new LinkerSpec { IdenticalCodeFolding = Linker.IdenticalCodeFolding, Comdat = Linker.Comdat };
        clone.Eh = new EhSpec { Model = Eh.Model };
        clone.Compare = new CompareSpec
        {
            SimilarityThreshold = Compare.SimilarityThreshold,
            IgnorePadding = Compare.IgnorePadding,
            DifferenceLimit = Compare.DifferenceLimit,
        };
        clone.Debug = new DebugSpec { Format = Debug.Format };
        clone.Detect = new DetectSpec
        {
            RichHeader = [.. Detect.RichHeader],
            Vb6Header = [.. Detect.Vb6Header],
            PdbCompiland = [.. Detect.PdbCompiland],
            CommentSection = [.. Detect.CommentSection],
            DwarfProducer = [.. Detect.DwarfProducer],
            LinkerVersion = [.. Detect.LinkerVersion],
            ImportDll = [.. Detect.ImportDll],
            Sections = [.. Detect.Sections],
        };
        clone.Targets = [.. Targets];
        clone.InheritanceChain = [.. InheritanceChain];
        clone.Install = Install is null ? null : new InstallSpec { Files = [.. Install.Files] };
        clone.Compile = Clone(Compile);
        clone.Link = Clone(Link);
        return clone;
    }

    private static CompileSpec? Clone(CompileSpec? spec)
        => spec is null
            ? null
            : new CompileSpec
            {
                Exe = spec.Exe,
                DefaultFlags = [.. spec.DefaultFlags],
                IncludeFlag = spec.IncludeFlag,
                DefineFlag = spec.DefineFlag,
                OutputFlag = spec.OutputFlag,
                DepfileFlag = spec.DepfileFlag,
                Optimization = new OptimizationSpec { Levels = [.. spec.Optimization.Levels] },
                Env = new Dictionary<string, string>(spec.Env, StringComparer.Ordinal),
            };

    private static LinkSpec? Clone(LinkSpec? spec)
        => spec is null
            ? null
            : new LinkSpec
            {
                Exe = spec.Exe,
                DefaultFlags = [.. spec.DefaultFlags],
                OutputFlag = spec.OutputFlag,
            };

    /// <summary>Copies everything the child set explicitly on top of <paramref name="result"/>.</summary>
    internal void ApplyExplicitOverridesTo(ToolchainProfile result)
    {
        result._explicit.UnionWith(_explicit);
        foreach (var key in _explicit)
        {
            switch (key)
            {
                case "targets":
                    result.Targets = [.. Targets];
                    break;
                case "abi.mangling":
                    result.Abi.Mangling = Abi.Mangling;
                    break;
                case "abi.default_cc":
                    result.Abi.DefaultCc = Abi.DefaultCc;
                    break;
                case "abi.member_cc":
                    result.Abi.MemberCc = Abi.MemberCc;
                    break;
                case "abi.pointer_size":
                    result.Abi.PointerSize = Abi.PointerSize;
                    break;
                case "codegen.function_align":
                    result.Codegen.FunctionAlign = Codegen.FunctionAlign;
                    break;
                case "codegen.padding_bytes":
                    result.Codegen.PaddingBytes = [.. Codegen.PaddingBytes];
                    break;
                case "codegen.prologue_hints":
                    result.Codegen.PrologueHints = [.. Codegen.PrologueHints];
                    break;
                case "linker.identical_code_folding":
                    result.Linker.IdenticalCodeFolding = Linker.IdenticalCodeFolding;
                    break;
                case "linker.comdat":
                    result.Linker.Comdat = Linker.Comdat;
                    break;
                case "eh.model":
                    result.Eh.Model = Eh.Model;
                    break;
                case "compare.similarity_threshold":
                    result.Compare.SimilarityThreshold = Compare.SimilarityThreshold;
                    break;
                case "compare.ignore_padding":
                    result.Compare.IgnorePadding = Compare.IgnorePadding;
                    break;
                case "compare.difference_limit":
                    result.Compare.DifferenceLimit = Compare.DifferenceLimit;
                    break;
                case "debug.format":
                    result.Debug.Format = Debug.Format;
                    break;
                case "detect.rich_header":
                    result.Detect.RichHeader = [.. Detect.RichHeader];
                    break;
                case "detect.vb6_header":
                    result.Detect.Vb6Header = [.. Detect.Vb6Header];
                    break;
                case "detect.pdb_compiland":
                    result.Detect.PdbCompiland = [.. Detect.PdbCompiland];
                    break;
                case "detect.comment_section":
                    result.Detect.CommentSection = [.. Detect.CommentSection];
                    break;
                case "detect.dwarf_producer":
                    result.Detect.DwarfProducer = [.. Detect.DwarfProducer];
                    break;
                case "detect.linker_version":
                    result.Detect.LinkerVersion = [.. Detect.LinkerVersion];
                    break;
                case "detect.import_dll":
                    result.Detect.ImportDll = [.. Detect.ImportDll];
                    break;
                case "detect.section":
                    result.Detect.Sections = [.. Detect.Sections];
                    break;
                // Tables merge key by key, so a child that changes one compile key keeps the rest of
                // its parent's compile table rather than replacing it wholesale.
                case "compile":
                    result.Compile ??= new CompileSpec();
                    break;
                case "compile.exe":
                    result.Compile ??= new CompileSpec();
                    result.Compile.Exe = Compile!.Exe;
                    break;
                case "compile.default_flags":
                    result.Compile ??= new CompileSpec();
                    result.Compile.DefaultFlags = [.. DefaultFlagsIf(Compile)];
                    break;
                case "compile.include_flag":
                    result.Compile ??= new CompileSpec();
                    result.Compile.IncludeFlag = Compile!.IncludeFlag;
                    break;
                case "compile.define_flag":
                    result.Compile ??= new CompileSpec();
                    result.Compile.DefineFlag = Compile!.DefineFlag;
                    break;
                case "compile.output_flag":
                    result.Compile ??= new CompileSpec();
                    result.Compile.OutputFlag = Compile!.OutputFlag;
                    break;
                case "compile.depfile_flag":
                    result.Compile ??= new CompileSpec();
                    result.Compile.DepfileFlag = Compile!.DepfileFlag;
                    break;
                case "compile.optimization":
                    result.Compile ??= new CompileSpec();
                    break;
                case "compile.optimization.levels":
                    result.Compile ??= new CompileSpec();
                    result.Compile.Optimization = new OptimizationSpec { Levels = [.. LevelsIf(Compile)] };
                    break;
                case "compile.env":
                    result.Compile ??= new CompileSpec();
                    foreach (var (name, setting) in Compile!.Env)
                    {
                        result.Compile.Env[name] = setting;
                    }

                    break;
                case "link":
                    result.Link ??= new LinkSpec();
                    break;
                case "link.exe":
                    result.Link ??= new LinkSpec();
                    result.Link.Exe = Link!.Exe;
                    break;
                case "link.default_flags":
                    result.Link ??= new LinkSpec();
                    result.Link.DefaultFlags = [.. Link!.DefaultFlags];
                    break;
                case "link.output_flag":
                    result.Link ??= new LinkSpec();
                    result.Link.OutputFlag = Link!.OutputFlag;
                    break;
                case "install.files":
                    result.Install = Install is null ? null : new InstallSpec { Files = [.. Install.Files] };
                    break;
            }
        }
    }

    private static List<string> DefaultFlagsIf(CompileSpec? spec) => spec?.DefaultFlags ?? [];

    private static List<string> LevelsIf(CompileSpec? spec) => spec?.Optimization?.Levels ?? [];

    public static ToolchainProfile Load(string path, Diagnostics diagnostics)
        => Load(TomlLoader.LoadDocument(path, diagnostics), path, diagnostics);

    public static ToolchainProfile Load(TomlDocument document, string path, Diagnostics diagnostics)
    {
        var root = TableScope.Root(document, diagnostics);
        var profile = new ToolchainProfile { FilePath = path };
        var targetPath = string.IsNullOrEmpty(root.Path) ? string.Empty : root.Path + ".";

        profile.SchemaVersion = (int)root.RequireInteger("schema_version");
        SchemaVersions.CheckSchemaVersion(profile.SchemaVersion, path, root.Line, targetPath + "schema_version", diagnostics);

        profile.Id = root.RequireString("id");
        profile.DisplayName = root.RequireString("display_name");
        profile.Family = root.RequireString("family");
        profile.Extends = root.String("extends");
        profile.IsAbstract = root.Bool("abstract", false) ?? false;
        if (root.Has("extends"))
        {
            profile._explicit.Add("extends");
        }

        foreach (var scope in root.Tables("targets"))
        {
            profile.Targets.Add(new TargetSpec
            {
                Format = scope.Enum("format", KnownValues.Formats, string.Empty) ?? string.Empty,
                Arch = scope.Enum("arch", KnownValues.Architectures, string.Empty) ?? string.Empty,
                Isa = scope.Enum("isa", KnownValues.InstructionSets, scope.String("arch", string.Empty) ?? string.Empty) ?? string.Empty,
            });
            scope.RejectUnknownKeys();
        }

        if (profile.Targets.Count > 0)
        {
            profile._explicit.Add("targets");
        }

        var abi = root.Child("abi");
        if (abi.Present)
        {
            if (abi.Has("mangling"))
            {
                profile.Abi.Mangling = abi.Enum("mangling", KnownManglings, "none") ?? "none";
                profile._explicit.Add("abi.mangling");
            }

            if (abi.Has("default_cc"))
            {
                profile.Abi.DefaultCc = abi.Enum("default_cc", KnownCallingConventions, "cdecl") ?? "cdecl";
                profile._explicit.Add("abi.default_cc");
            }

            if (abi.Has("member_cc"))
            {
                profile.Abi.MemberCc = abi.Enum("member_cc", KnownCallingConventions, "thiscall") ?? "thiscall";
                profile._explicit.Add("abi.member_cc");
            }

            if (abi.Has("pointer_size"))
            {
                profile.Abi.PointerSize = (int)(abi.Integer("pointer_size", 4) ?? 4);
                profile._explicit.Add("abi.pointer_size");
            }

            abi.RejectUnknownKeys();
        }

        var codegen = root.Child("codegen");
        if (codegen.Present)
        {
            if (codegen.Has("function_align"))
            {
                profile.Codegen.FunctionAlign = (int)(codegen.Integer("function_align", 16) ?? 16);
                profile._explicit.Add("codegen.function_align");
            }

            if (codegen.Has("padding_bytes"))
            {
                profile.Codegen.PaddingBytes = ParseByteArray(codegen, "padding_bytes");
                profile._explicit.Add("codegen.padding_bytes");
            }

            if (codegen.Has("prologue_hints"))
            {
                profile.Codegen.PrologueHints = codegen.StringArray("prologue_hints");
                profile._explicit.Add("codegen.prologue_hints");
            }

            codegen.RejectUnknownKeys();
        }

        var compare = root.Child("compare");
        if (compare.Present)
        {
            if (compare.Has("similarity_threshold"))
            {
                profile.Compare.SimilarityThreshold = compare.Double("similarity_threshold");
                profile._explicit.Add("compare.similarity_threshold");
            }

            if (compare.Has("ignore_padding"))
            {
                profile.Compare.IgnorePadding = compare.Bool("ignore_padding");
                profile._explicit.Add("compare.ignore_padding");
            }

            if (compare.Has("difference_limit"))
            {
                profile.Compare.DifferenceLimit = (int)(compare.Integer("difference_limit", 24) ?? 24);
                profile._explicit.Add("compare.difference_limit");
            }

            compare.RejectUnknownKeys();
        }

        var linker = root.Child("linker");
        if (linker.Present)
        {
            if (linker.Has("identical_code_folding"))
            {
                profile.Linker.IdenticalCodeFolding = linker.Enum("identical_code_folding", ["never", "possible", "always"], "never") ?? "never";
                profile._explicit.Add("linker.identical_code_folding");
            }

            if (linker.Has("comdat"))
            {
                profile.Linker.Comdat = linker.Bool("comdat", true) ?? true;
                profile._explicit.Add("linker.comdat");
            }

            linker.RejectUnknownKeys();
        }

        var eh = root.Child("eh");
        if (eh.Present)
        {
            if (eh.Has("model"))
            {
                profile.Eh.Model = eh.Enum("model", ["msvc-seh", "msvc-cxx", "dwarf2", "sjlj", "none"], "none") ?? "none";
                profile._explicit.Add("eh.model");
            }

            eh.RejectUnknownKeys();
        }

        var debug = root.Child("debug");
        if (debug.Present)
        {
            if (debug.Has("format"))
            {
                profile.Debug.Format = debug.String("format", "none") ?? "none";
                profile._explicit.Add("debug.format");
            }

            debug.RejectUnknownKeys();
        }

        var detect = root.Child("detect");
        if (detect.Present)
        {
            foreach (var rule in detect.Tables("rich_header"))
            {
                profile.Detect.RichHeader.Add(new RichHeaderRule
                {
                    Id = rule.UInt32("id", rule.UInt32("product_id", 0)) ?? 0,
                    Tool = rule.String("tool", rule.String("product", "unknown")) ?? "unknown",
                    Role = rule.Enum("role", ["linker", "compiler"], null),
                    BuildMin = (int)(rule.Integer("build_min", 0) ?? 0),
                    BuildMax = (int)(rule.Integer("build_max", int.MaxValue) ?? int.MaxValue),
                });
                rule.RejectUnknownKeys();
            }

            foreach (var rule in detect.Tables("vb6_header"))
            {
                profile.Detect.Vb6Header.Add(new Vb6HeaderRule
                {
                    Kind = rule.Enum("kind", ["pcode", "native"], string.Empty) ?? string.Empty,
                });
                rule.RejectUnknownKeys();
            }

            foreach (var rule in detect.Tables("linker_version"))
            {
                profile.Detect.LinkerVersion.Add(new LinkerVersionRule
                {
                    Min = rule.String("min"),
                    Max = rule.String("max"),
                });
                rule.RejectUnknownKeys();
            }

            foreach (var rule in detect.Tables("import_dll"))
            {
                profile.Detect.ImportDll.Add(new ImportDllRule
                {
                    Contains = rule.String("contains", string.Empty) ?? string.Empty,
                });
                rule.RejectUnknownKeys();
            }

            foreach (var rule in detect.Tables("section"))
            {
                profile.Detect.Sections.Add(new SectionRule
                {
                    Name = rule.String("name", string.Empty) ?? string.Empty,
                    Absent = rule.Bool("absent", false) ?? false,
                });
                rule.RejectUnknownKeys();
            }

            foreach (var rule in detect.Tables("pdb_compiland"))
            {
                profile.Detect.PdbCompiland.Add(new CompilandRule
                {
                    ProducerContains = rule.String("producer_contains", string.Empty) ?? string.Empty,
                });
                rule.RejectUnknownKeys();
            }

            foreach (var rule in detect.Tables("comment_section"))
            {
                profile.Detect.CommentSection.Add(new CommentRule
                {
                    Contains = rule.String("contains", string.Empty) ?? string.Empty,
                });
                rule.RejectUnknownKeys();
            }

            foreach (var rule in detect.Tables("dwarf_producer"))
            {
                profile.Detect.DwarfProducer.Add(new DwarfRule
                {
                    ProducerContains = rule.String("producer_contains", string.Empty) ?? string.Empty,
                });
                rule.RejectUnknownKeys();
            }

            if (profile.Detect.RichHeader.Count > 0)
            {
                profile._explicit.Add("detect.rich_header");
            }

            if (profile.Detect.Vb6Header.Count > 0)
            {
                profile._explicit.Add("detect.vb6_header");
            }

            if (profile.Detect.PdbCompiland.Count > 0)
            {
                profile._explicit.Add("detect.pdb_compiland");
            }

            if (profile.Detect.CommentSection.Count > 0)
            {
                profile._explicit.Add("detect.comment_section");
            }

            if (profile.Detect.DwarfProducer.Count > 0)
            {
                profile._explicit.Add("detect.dwarf_producer");
            }

            if (profile.Detect.LinkerVersion.Count > 0)
            {
                profile._explicit.Add("detect.linker_version");
            }

            if (profile.Detect.ImportDll.Count > 0)
            {
                profile._explicit.Add("detect.import_dll");
            }

            if (profile.Detect.Sections.Count > 0)
            {
                profile._explicit.Add("detect.section");
            }

            detect.RejectUnknownKeys();
        }

        var compile = root.Child("compile");
        if (compile.Present)
        {
            profile.Compile = new CompileSpec
            {
                Exe = compile.String("exe", string.Empty) ?? string.Empty,
                DefaultFlags = compile.StringArray("default_flags"),
                IncludeFlag = compile.String("include_flag", string.Empty) ?? string.Empty,
                DefineFlag = compile.String("define_flag", string.Empty) ?? string.Empty,
                OutputFlag = compile.String("output_flag", string.Empty) ?? string.Empty,
                DepfileFlag = compile.String("depfile_flag", string.Empty) ?? string.Empty,
            };
            profile._explicit.Add("compile");
            foreach (string key in new[] { "exe", "default_flags", "include_flag", "define_flag", "output_flag", "depfile_flag", "optimization" })
            {
                if (compile.Has(key))
                {
                    profile._explicit.Add("compile." + key);
                }
            }

            var optimization = compile.Child("optimization");
            if (optimization.Present)
            {
                profile.Compile.Optimization = new OptimizationSpec { Levels = optimization.StringArray("levels") };
                profile._explicit.Add("compile.optimization");
                if (optimization.Has("levels"))
                {
                    profile._explicit.Add("compile.optimization.levels");
                }

                optimization.RejectUnknownKeys();
            }

            var env = compile.Child("env");
            if (env.Present)
            {
                foreach (var (key, value) in env.Pairs())
                {
                    if (value is TomlString text)
                    {
                        profile.Compile.Env[key] = text.Value;
                    }
                    else
                    {
                        diagnostics.Error(path, value.Line, $"compile.env.{key}", $"expected a string, found {value.TypeName}");
                    }
                }

                profile._explicit.Add("compile.env");
                env.RejectUnknownKeys();
            }

            compile.RejectUnknownKeys();
        }

        var link = root.Child("link");
        if (link.Present)
        {
            profile.Link = new LinkSpec
            {
                Exe = link.String("exe", string.Empty) ?? string.Empty,
                DefaultFlags = link.StringArray("default_flags"),
                OutputFlag = link.String("output_flag", string.Empty) ?? string.Empty,
            };
            profile._explicit.Add("link");
            foreach (string key in new[] { "exe", "default_flags", "output_flag" })
            {
                if (link.Has(key))
                {
                    profile._explicit.Add("link." + key);
                }
            }

            link.RejectUnknownKeys();
        }

        var install = root.Child("install");
        if (install.Present)
        {
            profile.Install = new InstallSpec();
            foreach (var file in install.Tables("files"))
            {
                profile.Install.Files.Add(new InstallFileSpec
                {
                    Path = file.RequireString("path"),
                    Sha256 = file.RequireString("sha256").ToLowerInvariant(),
                });
                file.RejectUnknownKeys();
            }

            if (profile.Install.Files.Count > 0)
            {
                profile._explicit.Add("install.files");
            }

            install.RejectUnknownKeys();
        }

        root.RejectUnknownKeys();

        if (profile.Extends is not null && profile.Extends == profile.Id)
        {
            diagnostics.Error(path, root.Line, "extends", "a profile cannot extend itself");
        }

        // A profile describes one kind of Visual Basic program or it describes neither. A file that
        // claimed both would be chosen for a program whichever way it was compiled, which is exactly
        // the claim the rule exists to prevent.
        if (profile.Detect.Vb6Header.Select(r => r.Kind).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            diagnostics.Error(path, root.Line, "detect.vb6_header",
                "a profile describes one kind of Visual Basic program: pcode or native, not both");
        }

        // A profile may inherit its targets, so only a profile with no parent must declare them;
        // the registry re-checks the resolved profile, which covers inheritance.
        if (!profile.IsAbstract && profile.Extends is null && profile.Targets.Count == 0)
        {
            diagnostics.Error(path, root.Line, "targets", "a profile with no parent needs at least one [[targets]] entry");
        }

        return profile;
    }

    private static List<byte> ParseByteArray(TableScope scope, string key)
    {
        var result = new List<byte>();
        foreach (var value in scope.UInt32Array(key))
        {
            result.Add(value > 0xFF ? (byte)0xCC : (byte)value);
        }

        if (result.Count == 0)
        {
            result.Add(0xCC);
        }

        return result;
    }
}
