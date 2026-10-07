using Recon.Analysis;
using Recon.DebugInfo;
using Recon.Images;
using Recon.Pe;

namespace Recon.Vb6;

/// <summary>
/// The inventory of a Visual Basic 6 program compiled to p-code.
///
/// A p-code program is not machine code: its `.text` is a stream of interpreter tokens, and reading
/// those bytes as x86 would be inventing a program — which is what the tool used to refuse to do, and
/// did refuse, by reporting `isa: vb6-pcode` with one function named `entry` covering the whole section
/// and a problem saying the decoder does not speak it. Everything a p-code program *does* state about
/// itself was therefore invisible to `inventory`, and to everything built on it: no procedures, no
/// instruction counts, nothing for `inspect`, `report` or a comparison to work with.
///
/// What the program states is exact. Each object's method table points at a <c>ProcDscInfo</c> that
/// records how many bytes the procedure's stream is, so a procedure's extent is a number the compiler
/// wrote down rather than one inferred from the bytes — which is why these functions are `high`
/// confidence where an x86 analysis is often guessing. The instructions inside those extents are
/// measured against the runtime that will interpret them (see <see cref="PcodeProgram"/>), so without a
/// runtime the extents are still published and the instruction counts are not: the tool says which of
/// the two it has, rather than filling the gap with a guess.
///
/// This is the plan's "keep `isa` as its own axis" made concrete: one inventory shape, functions that
/// happen to be p-code procedures, and the instruction set named on each function.
/// </summary>
public static class PcodeInventory
{
    /// <summary>
    /// The functions and statistics of a p-code program. <paramref name="tables"/> is what a runtime
    /// measured; when it is null the procedures are still listed and their instructions are not
    /// measured, and <paramref name="runtimeNote"/> says why.
    /// </summary>
    public static AnalysisResult Analyze(
        PeImage pe,
        IBinaryImage image,
        byte[] bytes,
        Vb6Program program,
        IReadOnlyList<PcodeTableEvidence>? tables,
        string? runtimeNote)
    {
        var result = new AnalysisResult();
        var pcode = PcodeProgram.Read(pe, bytes, program, tables ?? [], withInstructions: false);

        foreach (var procedure in pcode.Procedures)
        {
            // How sure the tool is that this is a function of the stated extent. A p-code procedure is
            // the strongest case there is: the compiler wrote the extent into the descriptor, so nothing
            // here is inferred. It drops to `medium` when the bytes admit more than one reading of the
            // stream, and to `low` when the descriptor itself did not hold up.
            string confidence = procedure.Problems.Count > 0 || procedure.CodeBytes == 0
                ? "low"
                : procedure.Readings > 1
                    ? "medium"
                    : "high";
            var entry = new FunctionEntry
            {
                Id = FunctionId(procedure.CodeRva),
                // The program's own identity for a procedure: the object it belongs to and its index in
                // that object's method table. VB6 does not store procedure names in the binary — they
                // live in the source — so this is as close to a name as the file itself gets, and the
                // `unknowns` list says so for anything reading the document mechanically.
                Name = Label(procedure),
                Demangled = Label(procedure),
                Isa = "vb6-pcode",
                Section = SectionOf(image, procedure.CodeRva),
                Confidence = confidence,
                FoundBy = [SymbolSourceNames.ToWire(SymbolSource.PcodeProcedure)],
                Ranges = [new Recon.Analysis.Range { Rva = procedure.CodeRva, Size = (uint)procedure.CodeBytes }],
            };

            entry.Unknowns.Add("name_is_object_and_method_index");
            bool measured = procedure.Status != "unmeasured";
            if (measured && procedure.Readings > 1)
            {
                entry.Unknowns.Add($"stream_reads_{procedure.Readings}_ways");
            }

            if (measured && procedure.Padding > 0)
            {
                entry.Flags.Add($"padded:{procedure.Padding}");
            }

            // Only ever said about a stream that was read: without a runtime nothing is decoded, and
            // "no exit instruction" would be a claim about a reading that did not happen.
            if (measured && !procedure.EndsWithExit)
            {
                entry.Flags.Add("no_exit_instruction");
            }

            foreach (var problem in procedure.Problems)
            {
                result.Problems.Add($"{entry.Name}: {problem}");
            }

            result.Functions.Add(entry);
        }

        foreach (var problem in pcode.Problems)
        {
            result.Problems.Add(problem);
        }

        // The section fallback ranges the x86 analysis publishes, for the same reason: an unknown range
        // that is not listed at all reads as an empty program.
        foreach (var section in image.Sections.Where(s => !s.IsCode))
        {
            result.Data.Add(new DataEntry
            {
                Rva = section.Rva,
                Size = Math.Max(section.VirtualSize, section.RawSize),
                Kind = section.IsWritable ? "data" : "read_only",
                Source = "section",
                Name = section.Name,
            });
        }

        // The same keys the machine-code analysis publishes, so a document says the same things about a
        // program whatever it is written in: things that do not apply to p-code are zero rather than
        // absent, because an absent key reads as "not measured" and these were looked for.
        foreach (string name in new[]
                 {
                     "functions_import_thunks", "functions_named_by_signature", "calling_conventions_known",
                     "jump_tables", "xrefs",
                 })
        {
            result.Statistics[name] = 0;
        }

        result.Statistics["functions"] = result.Functions.Count;
        result.Statistics["functions_high"] = result.Functions.Count(f => f.Confidence == "high");
        result.Statistics["functions_medium"] = result.Functions.Count(f => f.Confidence == "medium");
        result.Statistics["functions_low"] = result.Functions.Count(f => f.Confidence == "low");
        result.Statistics["functions_with_unknown_size"] = result.Functions.Count(f => f.Ranges.Count == 0 || f.Ranges[0].Size == 0);
        result.Statistics["instructions"] = pcode.Procedures.Sum(p => p.InstructionCount);
        result.Statistics["pcode_objects"] = pcode.ObjectsRead;
        result.Statistics["pcode_runtime_used"] = tables is null ? 0 : 1;
        result.Statistics["pcode_empty_slots"] = pcode.EmptySlots;
        result.Statistics["pcode_entries_not_procedures"] = pcode.EntriesThatAreNotProcedures;
        result.Statistics["data_ranges"] = result.Data.Count;

        if (tables is null && runtimeNote is not null)
        {
            result.Problems.Add(runtimeNote);
        }

        result.Functions.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    /// <summary>A procedure's identity in this document: <c>FrmHex[3]</c>.</summary>
    public static string Label(PcodeProcedure procedure)
        => $"{procedure.ObjectName}[{procedure.MethodIndex}]";

    /// <summary>The id scheme the rest of the inventory uses: <c>f_</c> and the rva.</summary>
    private static string FunctionId(uint rva) => $"f_{rva:x6}";

    private static string SectionOf(IBinaryImage image, uint rva)
        => image.Sections.FirstOrDefault(s => rva >= s.Rva && rva < s.Rva + Math.Max(s.VirtualSize, s.RawSize))?.Name ?? string.Empty;
}
