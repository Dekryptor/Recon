using Iced.Intel;
using Recon.Analysis;
using Recon.Inventory;
using Recon.Pe;
using System.Text;

namespace Recon.Compare;

/// <summary>One instruction in its layout-independent form.</summary>
public sealed class NormalizedInstruction
{
    public uint Rva { get; set; }

    public int Length { get; set; }

    /// <summary>The instruction as text, with every reference replaced by what it points at.</summary>
    public string Text { get; set; } = string.Empty;

    public Mnemonic Mnemonic { get; set; }

    /// <summary>True when a relocation covers this instruction: the operand is an address.</summary>
    public bool Relocated { get; set; }

    /// <summary>
    /// Identities referenced by this instruction, in operand order, or null when it refers to
    /// nothing — which is most instructions. Allocated on the first reference rather than on
    /// construction: on a 4.4-million-instruction binary the two empty lists every instruction used to
    /// carry cost more than a hundred megabytes each, and the comparison is run twice per side.
    /// </summary>
    public List<string>? References { get; set; }

    /// <summary>Reference classes seen here, for the model section of the document. Null when none.</summary>
    public List<ReferenceClass>? ReferenceClasses { get; set; }

    /// <summary>Records one reference, allocating the two lists on the first one.</summary>
    public void AddReference(string text, ReferenceClass @class)
    {
        (References ??= []).Add(text);
        (ReferenceClasses ??= []).Add(@class);
    }

    /// <summary>
    /// Alignment padding: the toolchain's fill bytes, which are part of the function's extent but not
    /// of its code. Decided during normalization, from the toolchain profile.
    /// </summary>
    public bool IsPadding { get; set; }
}

/// <summary>
/// What a comparison's summarizing pass needs to know about one body, measured without keeping it.
///
/// Every field is what <see cref="NormalizedFunction"/> plus a body key would have said, and the point
/// of the type is that no instruction list exists at any moment while it is measured: the pass that
/// fills this walks one instruction at a time and keeps a count, a histogram, a small pending run of
/// trailing padding and a running hash. On the 11.8 MB client the largest single function is 1.08 MB —
/// something like 430,000 instructions — and materializing it to count it was what set the peak of a
/// comparison at six hundred megabytes.
/// </summary>
public sealed class BodyStatistics
{
    /// <summary>Instructions in the body, trailing padding excluded.</summary>
    public int InstructionCount { get; set; }

    /// <summary>Trailing padding instructions dropped from the end of the body.</summary>
    public int TrimmedPadding { get; set; }

    /// <summary>Undecodable bytes hit while walking the body; the body is then only partly read.</summary>
    public int InvalidBytes { get; set; }

    /// <summary>Instructions a relocation covers, in the body as trimmed.</summary>
    public int RelocatedCount { get; set; }

    /// <summary>The instructions' hash, in the same form a materialized body produces: the body's key.</summary>
    public string BodyKey { get; set; } = string.Empty;

    /// <summary>How many of each mnemonic the body is made of.</summary>
    public Dictionary<Mnemonic, int> Mnemonics { get; } = [];
}

/// <summary>A function body reduced to what two builds can be expected to share.</summary>
public sealed class NormalizedFunction
{
    public required FunctionInfo Function { get; init; }

    public List<NormalizedInstruction> Instructions { get; set; } = [];

    /// <summary>Padding instructions dropped from the end of the body.</summary>
    public int TrimmedPadding { get; set; }

    /// <summary>Undecodable bytes hit while walking the body; the body is then only partly read.</summary>
    public int InvalidBytes { get; set; }

    /// <summary>Hash of the normalized instruction texts: equal keys mean equal bodies.</summary>
    public string BodyKey { get; set; } = string.Empty;

    public int RelocatedCount => Instructions.Count(i => i.Relocated);

    public uint Rva => Function.Ranges[0].Rva;

    public uint Size => Function.Ranges[0].Size;
}

/// <summary>
/// Turns a function into a normalized body: the same code, with the addresses a rebuild is free to
/// change replaced by the names of the things they refer to. Two builds of the same source then
/// compare equal even though every address in them differs, which is the whole point of the
/// relocation model in section 3.1 of the plan.
/// </summary>
public sealed class FunctionNormalizer
{
    private readonly SideIndex _index;
    private readonly byte[] _bytes;
    private readonly CodeDecoder _decoder;
    private readonly SideSymbolResolver _resolver;
    private readonly ComparisonOptions _options;

    public FunctionNormalizer(SideIndex index, byte[] bytes, ComparisonOptions? options = null)
    {
        _index = index;
        _bytes = bytes;
        _options = options ?? new ComparisonOptions();
        _resolver = new SideSymbolResolver(index);
        _resolver.UseRelocations(index.Relocations);
        _decoder = new CodeDecoder(index.Image, _resolver);
        _decoder.Data = bytes;
    }

    public int NamedReferences => _resolver.NamedReferences;

    public int UnnamedReferences => _resolver.UnnamedReferences;

    public int IdentifiedReferences => _resolver.IdentifiedReferences;

    public NormalizedFunction Normalize(FunctionInfo function)
    {
        var result = new NormalizedFunction { Function = function };

        uint rva = function.Ranges[0].Rva;
        uint size = function.Ranges[0].Size;
        int? offset = _index.Image.RvaToOffset(rva);
        if (offset is null || size == 0 || offset.Value >= _bytes.Length)
        {
            return result;
        }

        int length = (int)Math.Min(size, (uint)(_bytes.Length - offset.Value));

        // A jump table can sit inside the function that reads it: a Mach-O switch keeps its table in
        // __text, between the arms. Those bytes are data, and decoding them invents instructions
        // that differ between two builds of the same switch, so they are left out of the body.
        // The body is read in the function's own frame of reference: a branch target inside it is an
        // offset from this entry, so moving the function does not change a single instruction's text.
        _resolver.BeginFunction(rva, size);
        try
        {
            foreach (var (at, count) in CodeSpans(rva, offset.Value, length))
            {
                foreach (var insn in _decoder.DecodeRange(_bytes, at, count, rva + (uint)(at - offset.Value)))
                {
                    ReadInstruction(result, insn);
                }
            }
        }
        finally
        {
            _resolver.EndFunction();
        }

        return result;
    }

    /// <summary>
    /// Walks a body and reports what a summary needs, without building the body.
    ///
    /// The walk is the same walk <see cref="Normalize"/> makes — the same spans, the same frame of
    /// reference, the same references taken from the resolver — and the arithmetic is the same
    /// arithmetic <see cref="NormalizedFunction"/> and <see cref="BodyKey(NormalizedFunction)"/> would
    /// have done on the result: trailing padding is dropped (only the *trailing* run, so interior
    /// padding is counted and hashed like any other instruction), an invalid instruction contributes
    /// its bytes and nothing else, and the key is the hash of the trimmed instructions' texts. The
    /// difference is that nothing is kept: at most a run of padding waits to find out whether it is
    /// the end of the body.
    /// </summary>
    /// <param name="referenceClasses">
    /// Counted here when given, because this walk reads every instruction of every body and the model
    /// section of the document wants a tally of what instructions refer to.
    /// </param>
    public BodyStatistics Summarize(FunctionInfo function, Dictionary<string, int>? referenceClasses = null)
    {
        var stats = new BodyStatistics();

        uint rva = function.Ranges[0].Rva;
        uint size = function.Ranges[0].Size;
        int? offset = _index.Image.RvaToOffset(rva);
        if (offset is null || size == 0 || offset.Value >= _bytes.Length)
        {
            return stats;
        }

        int length = (int)Math.Min(size, (uint)(_bytes.Length - offset.Value));

        // Trailing padding may be nothing at all, so a run of it is held back until an instruction
        // that is not padding says the run is interior — and then it counts like everything else.
        var pending = new List<NormalizedInstruction>();

        _resolver.BeginFunction(rva, size);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        try
        {
            foreach (var (at, count) in CodeSpans(rva, offset.Value, length))
            {
                foreach (var insn in _decoder.DecodeRange(_bytes, at, count, rva + (uint)(at - offset.Value)))
                {
                    if (insn.IsInvalid)
                    {
                        stats.InvalidBytes += insn.Length;
                        continue;
                    }

                    var normalized = new NormalizedInstruction
                    {
                        Rva = insn.Rva,
                        Length = insn.Length,
                        Text = insn.Text,
                        Mnemonic = insn.Mnemonic,
                        Relocated = _index.RelocationsIn(insn.Rva, insn.Length).Count > 0,
                        IsPadding = _options.IgnorePadding && _options.PaddingMnemonics.Contains(insn.Mnemonic),
                    };

                    foreach (var reference in _resolver.TakeReferences(insn.Rva))
                    {
                        normalized.AddReference(reference.Text, reference.Class);
                    }

                    if (normalized.IsPadding)
                    {
                        pending.Add(normalized);
                        continue;
                    }

                    foreach (var held in pending)
                    {
                        Count(stats, held, referenceClasses, hash);
                    }

                    pending.Clear();
                    Count(stats, normalized, referenceClasses, hash);
                }
            }
        }
        finally
        {
            _resolver.EndFunction();
        }

        // What is left pending is the body's trailing padding: trimmed, and the key is the key of what
        // is not.
        stats.TrimmedPadding = pending.Count;
        stats.BodyKey = stats.InstructionCount == 0
            ? string.Empty
            : Convert.ToHexStringLower(hash.GetHashAndReset());
        return stats;
    }

    /// <summary>One instruction's contribution to a summary, and to the hash of the body's texts.</summary>
    private static void Count(
        BodyStatistics stats,
        NormalizedInstruction instruction,
        Dictionary<string, int>? referenceClasses,
        System.Security.Cryptography.IncrementalHash hash)
    {
        stats.InstructionCount++;
        if (instruction.Relocated)
        {
            stats.RelocatedCount++;
        }

        stats.Mnemonics[instruction.Mnemonic] = stats.Mnemonics.GetValueOrDefault(instruction.Mnemonic) + 1;

        Span<byte> encoded = instruction.Text.Length <= 256
            ? stackalloc byte[256]
            : new byte[Encoding.UTF8.GetMaxByteCount(instruction.Text.Length)];
        int written = Encoding.UTF8.GetBytes(instruction.Text, encoded);
        hash.AppendData(encoded[..written]);
        hash.AppendData("\n"u8);

        if (referenceClasses is not null)
        {
            foreach (var @class in instruction.ReferenceClasses ?? [])
            {
                string name = @class.ToString();
                referenceClasses[name] = referenceClasses.GetValueOrDefault(name) + 1;
            }
        }
    }

    /// <summary>
    /// Reads one instruction into the normalized body: the bytes of a reference become the name of
    /// what they refer to, and padding is marked so the trailing run can be trimmed.
    /// </summary>
    private void ReadInstruction(NormalizedFunction result, DecodedInsn insn)
    {
        if (insn.IsInvalid)
        {
            result.InvalidBytes += insn.Length;
            return;
        }

        var normalized = new NormalizedInstruction
        {
            Rva = insn.Rva,
            Length = insn.Length,
            Text = insn.Text,
            Mnemonic = insn.Mnemonic,
            Relocated = _index.RelocationsIn(insn.Rva, insn.Length).Count > 0,
            IsPadding = _options.IgnorePadding && _options.PaddingMnemonics.Contains(insn.Mnemonic),
        };

        foreach (var reference in _resolver.TakeReferences(insn.Rva))
        {
            normalized.AddReference(reference.Text, reference.Class);
        }

        result.Instructions.Add(normalized);
    }

    /// <summary>
    /// The byte ranges of a function body that are code, as offsets into the file. A jump table this
    /// side knows about is cut out, wherever in the body it sits and whether it sits in one piece or
    /// runs off the end of the function.
    /// </summary>
    private List<(int Offset, int Length)> CodeSpans(uint rva, int offset, int length)
    {
        var isData = new bool[length];
        foreach (var table in _index.JumpTables)
        {
            if (table.Rva < rva || table.Rva >= rva + (uint)length)
            {
                continue;
            }

            uint start = table.Rva - rva;
            uint end = Math.Min((uint)length, start + SideIndex.TableSpan(table));
            for (uint at = start; at < end; at++)
            {
                isData[at] = true;
            }
        }

        var spans = new List<(int Offset, int Length)>();
        int at2 = 0;
        while (at2 < length)
        {
            if (isData[at2])
            {
                at2++;
                continue;
            }

            int start = at2;
            while (at2 < length && !isData[at2])
            {
                at2++;
            }

            spans.Add((offset + start, at2 - start));
        }

        return spans;
    }

    /// <summary>
    /// Trailing alignment padding — MSVC fills with <c>int3</c>, GCC with <c>nop</c> — belongs to a
    /// function's extent, not to its code, and is trimmed before the bodies are compared.
    /// </summary>
    public static NormalizedFunction TrimPadding(NormalizedFunction function)
    {
        int end = function.Instructions.Count;
        while (end > 0 && function.Instructions[end - 1].IsPadding)
        {
            end--;
        }

        function.TrimmedPadding = function.Instructions.Count - end;
        function.Instructions = function.Instructions.Take(end).ToList();
        function.BodyKey = BodyKey(function);
        return function;
    }

    /// <summary>The comparison key of a body: its instructions, in normalized form.</summary>
    public static string BodyKey(NormalizedFunction function)
    {
        if (function.Instructions.Count == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        foreach (var instruction in function.Instructions)
        {
            text.Append(instruction.Text).Append('\n');
        }

        return PeImage.HashBytes(Encoding.UTF8.GetBytes(text.ToString()));
    }
}
