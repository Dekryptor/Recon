using Iced.Intel;
using Recon.Images;
using Recon.Inventory;
using Recon.Pe;

namespace Recon.Compare;

/// <summary>
/// How an address-bearing operand reaches its target. This is the abstract relocation model the plan
/// asks for in section 3.1: a PE <c>HIGHLOW</c> fixup, an ELF <c>R_386_32</c> and a Mach-O
/// <c>X86_64_RELOC_UNSIGNED</c> all land in <see cref="Absolute"/>, so the compare engine never has
/// to know which container it is looking at.
/// </summary>
public enum ReferenceClass
{
    /// <summary>The operand holds the target's address; a relocation fixes it up in every build.</summary>
    Absolute,

    /// <summary>The operand holds a displacement from the instruction, which moves with the code.</summary>
    Relative,

    /// <summary>The operand reaches another module through an import slot.</summary>
    Import,

    /// <summary>The operand selects a slot of a jump table, and the slot decides the real target.</summary>
    JumpTable,

    /// <summary>Address-shaped, but the container does not say how it is reached.</summary>
    Unknown,
}

/// <summary>
/// Addresses of one side, resolved to the things they point at. Everything the comparison
/// substitutes for an address comes from here, and nothing here depends on where an address is: a
/// symbol name, an import, a jump-table slot, or a place inside a section. Two builds of the same
/// source therefore normalize to the same text even though every address in them differs, which is
/// what makes the comparison meaningful at all.
/// </summary>
public sealed partial class SideIndex
{
    private readonly IBinaryImage _image;
    private readonly Dictionary<uint, FunctionInfo> _functionsByRva = [];
    private readonly List<FunctionInfo> _functionsSorted;
    private readonly Dictionary<uint, DataInfo> _dataByRva = [];
    private readonly List<DataInfo> _dataNamed = [];
    private readonly Dictionary<string, DataInfo> _dataByName = new(StringComparer.Ordinal);
    private readonly List<JumpTableInfo> _jumpTables;
    private readonly Dictionary<uint, ImportInfo> _importsByIat = [];
    private readonly Dictionary<uint, string> _importsByThunk = [];
    private readonly List<RelocationInfo> _relocationsSorted;
    private readonly Dictionary<ulong, string> _identity = [];
    private readonly Dictionary<uint, string> _strings = [];

    public SideIndex(IBinaryImage image, InventoryDocument inventory)
    {
        _image = image;

        _functionsSorted = [.. inventory.Functions
            .Where(f => f.Ranges.Count > 0)
            .OrderBy(f => f.Ranges[0].Rva)];
        foreach (var function in _functionsSorted)
        {
            _functionsByRva.TryAdd(function.Ranges[0].Rva, function);
        }

        var data = inventory.Data.OrderBy(d => d.Rva).ToList();
        foreach (var item in data)
        {
            _dataByRva.TryAdd(item.Rva, item);
            if (item.Name is null)
            {
                continue;
            }

            if (IsRealName(item.Name) && !item.Name.StartsWith('.'))
            {
                _dataNamed.Add(item);
            }

            _dataByName.TryAdd(item.Name, item);

            // Symbol spelling differs between toolchains: keep the undecorated spelling too, so a
            // reference to _g_message finds g_message's entry when the two sides spell it differently.
            string plain = NormalizeSymbol(item.Name);
            if (!string.Equals(plain, item.Name, StringComparison.Ordinal))
            {
                _dataByName.TryAdd(plain, item);
            }
        }

        _jumpTables = [.. inventory.JumpTables.OrderBy(t => t.Rva)];

        foreach (var import in inventory.Imports)
        {
            _importsByIat.TryAdd(import.IatRva, import);
        }

        // A call to an import thunk lands on the thunk rather than on the import slot, so the thunk's
        // address answers to the import it forwards to as well. The inventory already recorded that
        // identity, which keeps the two spellings of the same import from drifting apart.
        foreach (var function in _functionsSorted)
        {
            string? target = ThunkTarget(function);
            if (target is not null)
            {
                _importsByThunk.TryAdd(function.Ranges[0].Rva, target);
            }
        }

        _relocationsSorted = [.. inventory.Relocations.OrderBy(r => r.Rva)];
    }

    public IReadOnlyList<RelocationInfo> Relocations => _relocationsSorted;

    public IBinaryImage Image => _image;

    public IReadOnlyList<FunctionInfo> Functions => _functionsSorted;

    /// <summary>Data symbols worth naming: the ones a reconstruction would have to reproduce.</summary>
    public IReadOnlyList<DataInfo> NamedData => _dataNamed;

    public IReadOnlyList<JumpTableInfo> JumpTables => _jumpTables;

    /// <summary>The relocations that land inside one instruction.</summary>
    public List<RelocationInfo> RelocationsIn(uint rva, int length)
    {
        uint end = rva + (uint)Math.Max(length, 1);
        var result = new List<RelocationInfo>();
        foreach (var relocation in _relocationsSorted)
        {
            if (relocation.Rva >= end)
            {
                break;
            }

            if (relocation.Rva >= rva)
            {
                result.Add(relocation);
            }
        }

        return result;
    }

    public bool TryFunction(uint rva, out FunctionInfo function) => _functionsByRva.TryGetValue(rva, out function!);

    public bool TryData(string name, out DataInfo data)
    {
        if (_dataByName.TryGetValue(name, out data!))
        {
            return true;
        }

        return _dataByName.TryGetValue(NormalizeSymbol(name), out data!);
    }

    /// <summary>The function whose extent contains an address, if any.</summary>
    public FunctionInfo? FunctionContaining(uint rva)
    {
        foreach (var function in _functionsSorted)
        {
            uint start = function.Ranges[0].Rva;
            if (start > rva)
            {
                break;
            }

            uint size = function.Ranges[0].Size;
            if (size > 0 && rva < start + size)
            {
                return function;
            }
        }

        return null;
    }

    /// <summary>How many bytes a jump table takes: one 4-byte slot per entry.</summary>
    public static uint TableSpan(JumpTableInfo table) => (uint)(table.Entries * 4);

    /// <summary>The jump table an address belongs to, and which 4-byte slot of it.</summary>
    public (JumpTableInfo Table, int Index)? JumpTableEntry(uint rva)
    {
        foreach (var table in _jumpTables)
        {
            if (rva < table.Rva)
            {
                continue;
            }

            uint size = TableSpan(table);
            if (rva < table.Rva + size)
            {
                return (table, (int)((rva - table.Rva) / 4));
            }
        }

        return null;
    }

    /// <summary>
    /// The class of an address, which is what the model section of the document counts. What this
    /// side's container can say about an address is all it gets to say: an import slot is an import,
    /// a jump-table slot is a jump table, and everything else is an absolute address the rebuild may
    /// move.
    /// </summary>
    public ReferenceClass Classify(uint rva)
    {
        if (_importsByIat.ContainsKey(rva) || _importsByThunk.ContainsKey(rva))
        {
            return ReferenceClass.Import;
        }

        if (JumpTableEntry(rva) is not null)
        {
            return ReferenceClass.JumpTable;
        }

        return _image.ContainsRva(rva) ? ReferenceClass.Absolute : ReferenceClass.Unknown;
    }

    /// <summary>
    /// What an address is, as a symbol-like name: an import, a function, a data symbol, a jump-table
    /// slot, or a place inside a section. <paramref name="named"/> says whether the answer comes from a
    /// real symbol name or from the layout, which is the difference between a fact about the program
    /// and a fact about the build.
    /// </summary>
    public string Reference(uint rva, out bool named) => Reference(rva, branch: false, out named);

    /// <summary>
    /// The same, for a branch target, which reads better as a section offset than as a data relative
    /// address: the target of a jump is a place in the code, not a field of a structure.
    /// </summary>
    public string Reference(uint rva, bool branch, out bool named)
    {
        if (TryName(rva, out string? name))
        {
            named = true;
            return name;
        }

        if (JumpTableEntry(rva) is { } entry)
        {
            named = false;
            return $"jump_table[{entry.Index}] of {OwnerName(entry.Table)}";
        }

        // A branch target that is not a symbol is a place in a section; a memory operand that is not a
        // symbol is usually a field of something, which is how the two read differently here.
        if (!branch && DataContaining(rva, out var container, out uint offset) && container.Name is not null)
        {
            named = false;
            return $"{container.Name}+{offset}";
        }

        named = false;
        return SectionOffset(rva);
    }

    /// <summary>
    /// The name this side has for an address, or false when it has none. An import, a function, a data
    /// symbol: the things that are named rather than placed.
    /// </summary>
    public bool TryName(uint rva, out string name)
    {
        if (TryImport(rva, out var import))
        {
            name = ImportName(import);
            return true;
        }

        if (TryFunction(rva, out var function) && IsRealName(function.Name))
        {
            name = function.Name!;
            return true;
        }

        if (_dataByRva.TryGetValue(rva, out var data) && IsRealName(data.Name) && !data.Name!.StartsWith('.'))
        {
            name = data.Name!;
            return true;
        }

        name = string.Empty;
        return false;
    }

    /// <summary>The function a jump table belongs to, by name when it has one.</summary>
    public string OwnerName(JumpTableInfo table)
    {
        if (table.Owner is { Length: > 0 } owner && IsRealName(owner))
        {
            return owner;
        }

        foreach (var function in _functionsSorted)
        {
            if (table.UsedAtRva >= function.Ranges[0].Rva
                && (function.Ranges[0].Size == 0 || table.UsedAtRva < function.Ranges[0].Rva + function.Ranges[0].Size))
            {
                return function.Name ?? SectionOffset(function.Ranges[0].Rva);
            }
        }

        return SectionOffset(table.Rva);
    }

    public bool TryFunctionById(string id, out FunctionInfo function)
    {
        foreach (var candidate in _functionsSorted)
        {
            if (string.Equals(candidate.Id, id, StringComparison.Ordinal))
            {
                function = candidate;
                return true;
            }
        }

        function = null!;
        return false;
    }

    /// <summary>
    /// The data symbol whose extent contains an address, with the offset into it. A symbol without a
    /// recorded size extends to the next symbol, which is all a container without sizes can say.
    /// </summary>
    public bool DataContaining(uint rva, out DataInfo data, out uint offset)
    {
        DataInfo? best = null;
        foreach (var candidate in _dataByRva.Values)
        {
            if (candidate.Rva > rva)
            {
                continue;
            }

            uint size = candidate.Size > 0 ? candidate.Size : NextDataGap(candidate.Rva);
            if (rva - candidate.Rva < size)
            {
                if (best is null || candidate.Rva > best.Rva
                    || (candidate.Rva == best.Rva && size > best.Size))
                {
                    best = candidate;
                }
            }
        }

        if (best is null)
        {
            data = null!;
            offset = 0;
            return false;
        }

        data = best;
        offset = rva - best.Rva;
        return true;
    }

    /// <summary>How far an unsized data symbol reaches: up to the next symbol, or the section's end.</summary>
    private uint NextDataGap(uint rva)
    {
        uint next = uint.MaxValue;
        foreach (var candidate in _dataByRva.Keys)
        {
            if (candidate > rva && candidate < next)
            {
                next = candidate;
            }
        }

        BinarySection? section = _image.SectionContainingRva(rva);
        uint sectionEnd = section?.RvaEnd ?? rva;
        uint reach = Math.Max(sectionEnd, rva + 1) - rva;
        if (next != uint.MaxValue)
        {
            reach = Math.Min(reach, next - rva);
        }

        return Math.Max(reach, 1);
    }

    /// <summary>
    /// What is at an address that has no name: the words of the thing, or the literal text of it.
    ///
    /// A build moves things that are not named — a table of addresses, a constant, a string literal —
    /// and a reference to one of them is then an address that differs between two builds of the same
    /// source. Comparing the numbers says the code differs when the only difference is where the
    /// linker put something; comparing what is *at* the address says what the instruction really
    /// refers to, which is the same thing in both builds. This is what the plan asks for when it says
    /// the comparison must normalize what a rebuild is free to change.
    ///
    /// The answer is never an address, and never an offset from one: a table is identified by the
    /// *names* its entries point at or by the values between them, so two builds that put the same
    /// table in two places produce the same identity. A word that is itself an address inside this
    /// image is resolved the same way any other operand is — a name when it has one, and otherwise
    /// one level of the same question about what *it* points at — which is what makes a table of
    /// pointers to string literals compare equal after the literals move.
    /// </summary>
    public string? Identity(uint rva, int size = 4)
    {
        ulong key = Key(rva, size);
        if (_identity.TryGetValue(key, out string? cached))
        {
            return cached.Length == 0 ? null : cached;
        }

        string? identity = ComputeIdentity(rva, size, depth: 0);
        _identity[key] = identity ?? string.Empty;
        return identity;
    }

    /// <summary>
    /// One level of the same question, for a word of an identity that is itself an address: what is at
    /// that address. Its answer is placed in the outer identity, so a table of pointers to literals is
    /// identified by the literals — which is what makes it the same table after a rebuild that ordered
    /// the literals differently.
    /// </summary>
    private string? NestedIdentity(uint rva, int size)
    {
        ulong key = Key(rva, size);
        if (_identity.TryGetValue(key, out string? cached))
        {
            return cached.Length == 0 ? null : cached;
        }

        return ComputeIdentity(rva, size, depth: 1);
    }

    /// <summary>The identity of an address at a width. The width is part of the key: what a `fld qword
    /// ptr` reads is the same address as what a `fdivr dword ptr` reads, and not the same thing.</summary>
    private static ulong Key(uint rva, int size) => ((ulong)rva << 8) | (uint)Math.Clamp(size, 1, 255);

    private string? ComputeIdentity(uint rva, int size, int depth)
    {
        BinarySection? section = _image.SectionContainingRva(rva);
        if (section is null || rva < section.Rva || rva >= section.Rva + section.RawSize)
        {
            return null;
        }

        // Never code. What is at a code address is instructions, and two builds of the same source
        // have the same instructions at different addresses — an identity read out of the bytes of a
        // function would be a fact about the code's own addresses, which is the thing being avoided.
        if (section.IsCode)
        {
            return null;
        }

        if (StringAt(rva, section) is { } text)
        {
            return $"\"{text}\"";
        }

        // As many whole words as the instruction reads — four bytes for a `fdivr dword ptr`, eight for
        // an `fld qword ptr` — and at least one. The width is the thing itself: a fixed window instead
        // made the comparison depend on how much of the *next* constant happened to follow this one in
        // each build, which is exactly the layout dependence the identity exists to remove. An operand
        // with a register in it addresses a table rather than a word, and is given the wider window,
        // because the instruction reads one entry and the table is what the operand names.
        int count = Math.Clamp(size / 4, 1, 3);
        var words = new List<string>(count);
        for (uint index = 0; index < count; index++)
        {
            uint at = rva + (index * 4);
            if (at + 4 > section.Rva + section.RawSize)
            {
                break;
            }

            words.Add(WordIdentity(at, depth));
        }

        return words.Count == 0 ? null : "[" + string.Join(" ", words) + "]";
    }

    /// <summary>
    /// One word of an identity: the name of the thing it points at, what that thing is, or the value
    /// itself when it points at nothing.
    /// </summary>
    private string WordIdentity(uint rva, int depth)
    {
        Span<byte> word = stackalloc byte[4];
        if (!DataAt(rva, word))
        {
            return "?";
        }

        uint value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(word);
        if (value >= _image.ImageBase && value - _image.ImageBase <= uint.MaxValue)
        {
            uint target = (uint)(value - _image.ImageBase);
            if (_image.ContainsRva(target))
            {
                if (TryName(target, out string name))
                {
                    return name;
                }

                // One level down: a table of pointers to unnamed literals is identified by the
                // literals, which is the difference between a comparison that works after a rebuild
                // and one that only works when nothing moved.
                if (depth < 1 && NestedIdentity(target, 4) is { } nested && nested.Length <= 40)
                {
                    return nested;
                }

                return "->";
            }
        }

        return $"0x{value:x8}";
    }

    /// <summary>
    /// The literal at an address, when it is one: at least four printable bytes ending in a terminator
    /// before anything that is not printable. A short fragment is not enough to identify a place by —
    /// `"%d"` occurs in half the tables of a C library — so the answer is null rather than a guess.
    /// </summary>
    private string? StringAt(uint rva, BinarySection section)
    {
        if (_strings.TryGetValue(rva, out string? cached))
        {
            return cached.Length == 0 ? null : cached;
        }

        byte[] bytes = new byte[(int)Math.Min(160, section.Rva + section.RawSize - rva)];
        if (!DataAt(rva, bytes))
        {
            _strings[rva] = string.Empty;
            return null;
        }

        int end = -1;
        for (int at = 0; at < bytes.Length && at < 96; at++)
        {
            if (bytes[at] == 0)
            {
                end = at;
                break;
            }

            if (bytes[at] < 0x20 || bytes[at] >= 0x7F)
            {
                _strings[rva] = string.Empty;
                return null;
            }
        }

        string? text = end >= 4 ? System.Text.Encoding.ASCII.GetString(bytes, 0, end) : null;
        _strings[rva] = text ?? string.Empty;
        return text;
    }

    /// <summary>The four bytes at an address of the image, read the way a decoder reads a window.</summary>
    private bool DataAt(uint rva, Span<byte> destination)
    {
        int? offset = _image.RvaToOffset(rva);
        if (offset is null || offset < 0 || offset.Value + destination.Length > _image.Bytes.Length)
        {
            return false;
        }

        _image.Bytes.AsSpan(offset.Value, destination.Length).CopyTo(destination);
        return true;
    }

    /// <summary>
    /// True when a reference says nothing but where it is: a place in a section, a field of something,
    /// or a slot of a jump table the side cannot name. Everything else is a name or an identity.
    /// </summary>
    public static bool IsPositional(string text)
        => text.StartsWith("jump_table[", StringComparison.Ordinal)
           || text.StartsWith("0x", StringComparison.Ordinal)
           || PositionPattern().IsMatch(text);

    [System.Text.RegularExpressions.GeneratedRegex("^\\.?[A-Za-z_.$][\\w.$]*\\+[0-9A-Fa-fx]+$")]
    private static partial System.Text.RegularExpressions.Regex PositionPattern();

    /// <summary>Where an address is when nothing names it: <c>.text+0x68C</c>.</summary>
    public string SectionOffset(uint rva)
    {
        BinarySection? section = _image.SectionContainingRva(rva);
        return section is null ? $"0x{rva:X}" : $"{section.Name}+0x{rva - section.Rva:X}";
    }

    /// <summary>
    /// True for a name that comes from the program rather than from the layout. A placeholder such as
    /// <c>sub_1010</c> is derived from an address, so it says nothing about what a function is and two
    /// builds will never agree on it.
    /// </summary>
    public static bool IsRealName(string? name)
        => !string.IsNullOrEmpty(name) && !IsPlaceholder(name);

    /// <summary>
    /// True for a name the analysis made up from an address, such as <c>sub_401000</c>. A prefix test
    /// alone would throw away real symbols (<c>sub_fast</c> is a name a program really has), so the
    /// suffix has to look like an address for the name to be a placeholder.
    /// </summary>
    public static bool IsPlaceholder(string name)
    {
        foreach (string prefix in PlaceholderPrefixes)
        {
            if (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.Ordinal))
            {
                string rest = name[prefix.Length..];

                // The suffix has to look like an address, and an address is not one hex digit long.
                // `func_a` is a symbol a program can really have; `sub_401000` is not.
                if (rest.Length >= 4 && rest.All(char.IsAsciiHexDigit))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static readonly string[] PlaceholderPrefixes =
        ["sub_", "loc_", "j_", "thunk_", "unknown_", "func_", "data_", "byte_", "word_", "dword_"];

    /// <summary>The spelling two toolchains can be expected to share: decorations off.</summary>
    public static string NormalizeSymbol(string name)
    {
        string result = name.Trim();
        if (result.StartsWith("__imp_", StringComparison.Ordinal))
        {
            result = result["__imp_".Length..];
        }
        else if (result.StartsWith("_imp__", StringComparison.Ordinal))
        {
            result = result["_imp__".Length..];
        }

        while (result.StartsWith('_'))
        {
            result = result[1..];
        }

        int at = result.LastIndexOf('@');
        if (at > 0 && int.TryParse(result[(at + 1)..], out _))
        {
            result = result[..at];
            if (result.StartsWith('@'))
            {
                result = result[1..];
            }
        }

        return result;
    }

    private bool TryImport(uint rva, out ImportInfo import)
    {
        if (_importsByIat.TryGetValue(rva, out import!))
        {
            return true;
        }

        if (_importsByThunk.TryGetValue(rva, out string? thunk))
        {
            import = new ImportInfo { Dll = string.Empty, Name = thunk };
            return true;
        }

        return false;
    }

    private static string ImportName(ImportInfo import)
        => import.Dll.Length == 0
            ? import.Name ?? string.Empty
            : import.Name is null ? $"{import.Dll}!#{import.Ordinal}" : $"{import.Dll}!{import.Name}";

    /// <summary>The name a thunk forwards to, when the function is a thunk at all.</summary>
    private static string? ThunkTarget(FunctionInfo function)
    {
        if (function.ImportThunk is { Length: > 0 } thunk)
        {
            return thunk;
        }

        return function.Flags.Contains("import_thunk") ? function.Name : null;
    }
}


/// <summary>
/// Hands the formatter the name of whatever an instruction's operands point at, and remembers what it
/// was asked about. This is what turns "mov eax,[0x40A044]" in one build and "mov eax,[0x409044]" in
/// the next into the same instruction: the operand stops being an address and becomes the thing the
/// address refers to. Every reference it resolves is also recorded per instruction, so a difference
/// can say not just that two instructions differ but what each of them referred to.
/// </summary>
public sealed class SideSymbolResolver : ISymbolResolver
{
    private readonly SideIndex _index;
    private readonly ulong _imageBase;
    private readonly Dictionary<uint, List<(string Text, ReferenceClass Class)>> _references = [];
    private Dictionary<ulong, List<RelocationInfo>>? _relocationsByInstruction;
    private uint _functionStart;
    private uint _functionSize;

    public SideSymbolResolver(SideIndex index)
    {
        _index = index;
        _imageBase = index.Image.ImageBase;
    }

    /// <summary>Address operands that resolved to a symbol name.</summary>
    public int NamedReferences { get; private set; }

    /// <summary>Address operands that resolved to a place in the image with no name and nothing distinctive in it.</summary>
    public int UnnamedReferences { get; private set; }

    /// <summary>
    /// Address operands that have no name but are identified by what is at them: a string literal, a
    /// constant, a table of addresses. These compare equal across two builds even when the compiler
    /// put them somewhere else, which is the difference between "this code refers to the same thing"
    /// and "this code refers to the same address".
    /// </summary>
    public int IdentifiedReferences { get; private set; }

    /// <summary>
    /// Says which function the next decodes belong to, so a branch that stays inside it can be named
    /// by where it goes *within the function* rather than by where the function happens to sit. Two
    /// builds of one function have the same internal control flow; whether the linker put the function
    /// at the same address is a fact about the link, and comparing it as a difference says a
    /// reconstruction is wrong when it is only elsewhere. A size of zero means the function's extent
    /// is unknown, and then no target can be said to be inside it.
    /// </summary>
    public void BeginFunction(uint startRva, uint size)
    {
        _functionStart = startRva;
        _functionSize = size;
    }

    public void EndFunction() => _functionSize = 0;

    /// <summary>
    /// What one instruction refers to, in operand order, handed over and forgotten.
    ///
    /// The list exists to carry references out of the decoder's operand formatting and into the
    /// normalized instruction, and nothing reads it after that — so it is *taken* rather than read.
    /// Kept instead, it is a transcript of every instruction the comparison has ever decoded: on the
    /// 11.8 MB client that is 822,000 entries, each with its list and its strings, and the resolver
    /// holding them was a quarter of a gigabyte of live memory at the end of the summarizing pass —
    /// the largest single thing a comparison retained, and none of it read again.
    /// </summary>
    public List<(string Text, ReferenceClass Class)> TakeReferences(uint instructionRva)
        => _references.Remove(instructionRva, out var list)
            ? list
            : [];

    /// <summary>
    /// Records where each relocation sits, so an operand can be recognised as an address: the
    /// container only relocates what a rebuild is free to move.
    /// </summary>
    public void UseRelocations(IReadOnlyList<RelocationInfo> relocations)
    {
        _relocationsByInstruction = [];
        foreach (var relocation in relocations)
        {
            ulong address = _imageBase + relocation.Rva;
            if (!_relocationsByInstruction.TryGetValue(address, out var list))
            {
                _relocationsByInstruction[address] = list = [];
            }

            list.Add(relocation);
        }
    }

    public bool TryGetSymbol(
        in Instruction instruction,
        int operand,
        int instructionOperand,
        ulong address,
        int addressSize,
        out SymbolResult symbol)
    {
        symbol = default;
        bool branch = IsBranchTarget(in instruction, address);
        OpKind kind = instruction.GetOpKind(operand);
        int size = kind == OpKind.Memory
            ? instruction.MemoryBase != Register.None || instruction.MemoryIndex != Register.None
                ? 12
                : MemoryWidth(in instruction)
            : 4;
        if (!TryResolve(instruction.IP, address, branch, size, out string text, out bool named, out bool identified))
        {
            return false;
        }

        if (!named && !identified)
        {
            // Nothing here has a name. A memory operand or a branch target is an address by
            // construction and is admitted as the place it is — a difference the report then shows,
            // which is the honest answer when nothing at the address says what it is.
            //
            // An immediate is different: a constant that merely looks like an address must not enter
            // the model. What is *at* an address is the evidence, and a container's relocation is not
            // evidence on its own — a compiler emits a table's address into a slot the linker then
            // relocates, so in a `.rdata` of constants almost every immediate is relocated and almost
            // none of them are addresses.
            if (!branch && kind != OpKind.Memory)
            {
                return false;
            }
        }

        symbol = new SymbolResult(address, text);
        Record(instruction.IP, text, ClassOf(in instruction, (uint)(address - _imageBase)), named, identified);
        return true;
    }

    /// <summary>
    /// True when a reference's text is an identity rather than a name or a place. The three kinds are
    /// counted apart, because they are three different claims: a name is something the program says
    /// about itself, an identity is the reader's own answer about the bytes, and a place says only
    /// that the thing is somewhere.
    /// </summary>
    /// <summary>An immediate is address-shaped only when it names something or is identified by its contents.</summary>
    public static bool IsImmediate(OpKind kind)
        => kind is OpKind.Immediate8 or OpKind.Immediate8_2nd or OpKind.Immediate16
            or OpKind.Immediate32 or OpKind.Immediate64;

    public OpKind KindOf(in Instruction instruction, int operand)
        => instruction.GetOpKind(operand);

    /// <summary>Resolves an address to what it points at, for callers that only have an address.</summary>
    public bool Reference(ulong address, out SymbolResult symbol)
    {
        symbol = default;
        if (!TryResolve(address, address, branch: false, size: 4, out string text, out _, out _))
        {
            return false;
        }

        symbol = new SymbolResult(address, text);
        return true;
    }

    /// <summary>
    /// What class a reference belongs to. What the reference *is* comes first: a slot that reaches
    /// another module is an import, and no container's spelling of the fixup changes that. Otherwise
    /// the container's own kind decides, through the one table that knows what the kinds mean, and an
    /// address the container recorded no fixup for is still an address the rebuild may move.
    /// </summary>
    private ReferenceClass ClassOf(in Instruction instruction, uint rva)
    {
        ReferenceClass semantic = _index.Classify(rva);
        if (semantic is ReferenceClass.Import or ReferenceClass.JumpTable)
        {
            return semantic;
        }

        uint instructionRva = instruction.IP >= _imageBase ? (uint)(instruction.IP - _imageBase) : 0;
        foreach (var relocation in _index.RelocationsIn(instructionRva, instruction.Length))
        {
            ReferenceClass @class = RelocationModel.Classify(relocation);
            if (@class != ReferenceClass.Unknown)
            {
                return @class;
            }
        }

        return semantic;
    }

    /// <summary>How many bytes a memory operand reads, or four when the decoder does not say.</summary>
    private static int MemoryWidth(in Instruction instruction)
    {
        try
        {
            int bytes = instruction.MemorySize.GetSize();
            return bytes is 1 or 2 or 4 or 8 ? bytes : 4;
        }
        catch (Exception)
        {
            return 4;
        }
    }

    private bool TryResolve(
        ulong instructionIp,
        ulong address,
        bool branch,
        int size,
        out string text,
        out bool named,
        out bool identified)
    {
        text = string.Empty;
        named = false;
        identified = false;
        if (address < _imageBase || address - _imageBase > uint.MaxValue)
        {
            return false;
        }

        uint rva = (uint)(address - _imageBase);
        if (!_index.Image.ContainsRva(rva))
        {
            return false;
        }

        text = _index.Reference(rva, branch, out named);

        // A branch that stays inside the function being read needs no name: the function's own entry
        // is the one address two builds of it agree on, and everything inside is an offset from it.
        if (!named && branch && InsideTheFunction(rva))
        {
            text = $".fn+0x{rva - _functionStart:x}";
            return true;
        }

        // A data address with no name of its own and nothing but a place to be: it is identified by
        // what is at it — the literal, the table, the constant — and the identity *is* the text, with
        // no address in it. A rebuild that emits the same bytes somewhere else refers to the same
        // thing, and saying so is the difference between a comparison about the code and one about
        // where the linker put things.
        if (!named && !branch && SideIndex.IsPositional(text) && _index.Identity(rva, size) is { } identity)
        {
            text = identity;
            identified = true;
        }

        return true;
    }

    /// <summary>True when the address is inside the function currently being read.</summary>
    private bool InsideTheFunction(uint rva)
        => _functionSize > 0 && rva >= _functionStart && rva < _functionStart + _functionSize;

    private void Record(ulong instructionIp, string text, ReferenceClass @class, bool named, bool identified)
    {
        uint rva = instructionIp >= _imageBase ? (uint)(instructionIp - _imageBase) : 0;
        if (!_references.TryGetValue(rva, out var list))
        {
            _references[rva] = list = [];
        }

        if (list.Exists(entry => string.Equals(entry.Text, text, StringComparison.Ordinal)
                                 && entry.Class == @class))
        {
            return;
        }

        list.Add((text, @class));
        if (identified)
        {
            IdentifiedReferences++;
        }
        else if (named)
        {
            NamedReferences++;
        }
        else
        {
            UnnamedReferences++;
        }
    }

    private static bool IsBranchTarget(in Instruction instruction, ulong address)
        => instruction.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64
           && instruction.NearBranchTarget == address;
}
