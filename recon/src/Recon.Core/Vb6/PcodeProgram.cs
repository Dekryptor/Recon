using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Recon.Images;
using Recon.Pe;

namespace Recon.Vb6;

/// <summary>One decoded p-code instruction, at the address it starts at.</summary>
public sealed record PcodeInstruction
{
    public uint Rva { get; init; }

    /// <summary>The first byte, which is what the interpreter dispatches on.</summary>
    public int Opcode { get; init; }

    /// <summary>
    /// The lead byte that selected a secondary table, or -1 when the instruction is one of the primary
    /// opcodes. A lead instruction's second byte indexes the table the lead byte's handler names.
    /// </summary>
    public int Lead { get; init; } = -1;

    /// <summary>The byte that indexes the table this instruction was read from.</summary>
    public int TableOpcode { get; init; }

    public int Size { get; init; }

    /// <summary>
    /// The instruction as it lies in the stream, in hex: <c>1c d8 01</c>. Read from the bytes rather
    /// than reconstructed from the opcode, because a listing that showed only what it understood would
    /// hide the very bytes a reader is checking.
    /// </summary>
    public string Bytes { get; init; } = string.Empty;

    /// <summary>The name derived from the handler, when there is exactly one. Empty otherwise.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>True when the slot dispatches to the runtime's shared unhandled-case handler.</summary>
    public bool Unhandled { get; init; }

    /// <summary>
    /// What the bytes after the opcode are, read from the runtime's handler: <c>none</c> when there are
    /// none, <c>data</c> when the handler reads them as a value, <c>slot</c> when the word is a frame
    /// slot, <c>target</c> when the word says where control goes.
    /// </summary>
    public string Operand { get; init; } = "none";

    /// <summary>How many bytes of operand the instruction has, or -1 when its length is not one number.</summary>
    public int OperandBytes { get; init; } = -1;

    /// <summary>
    /// Where in the operand the word the kind is about sits. Not always zero: a <c>For</c>/<c>Next</c>
    /// takes its counter's frame slot first and the distance it branches by second.
    /// </summary>
    public int OperandAt { get; init; }

    /// <summary>
    /// Where a <c>target</c> operand goes: the procedure's code address plus the word the operand holds
    /// at <see cref="OperandAt"/>. This is the interpreter's own rule — the branch reads the word, adds
    /// the frame slot holding the stream's base, and dispatches there — and the arithmetic is done here
    /// so that a listing can show where the instruction goes rather than a number.
    /// </summary>
    public uint? Target { get; init; }

    /// <summary>
    /// Whether the target lands on an instruction start of the same procedure. True for every branch in
    /// the corpus, which is what says the rule is the interpreter's; a false here is a target that goes
    /// somewhere this reading does not have an instruction, and is worth showing.
    /// </summary>
    public bool TargetIsInstructionStart { get; init; }

    /// <summary>
    /// The frame slot the operand names, sign-extended: the interpreter reads it with a sign-extending
    /// load. Present for a <c>slot</c> operand, and for a <c>target</c> operand whose handler also
    /// names one — a For/Next instruction's counter.
    /// </summary>
    public int? FrameSlot { get; init; }

    /// <summary>
    /// Where in the operand that slot word sits, in bytes from the first byte after the opcode, or
    /// <c>-1</c> when the handler sign-extends none.
    /// </summary>
    public int FrameSlotAt { get; init; } = -1;

    /// <summary>
    /// Where the procedure's stream starts. A target operand is a distance from it, so a listing cannot
    /// say where an instruction goes without knowing it.
    /// </summary>
    public uint CodeRva { get; init; }

    /// <summary>How the opcode is written: <c>0x1C</c>, or <c>0xFD 0x03</c> for a lead byte's table.</summary>
    public string Describe() => Lead < 0 ? $"0x{Opcode:X2}" : $"0x{Lead:X2} 0x{TableOpcode:X2}";

    /// <summary>
    /// The operand as a reader wants it: where a branch goes, which frame slot an instruction names, and
    /// nothing at all where the operand is a value — the bytes are in <see cref="Bytes"/> and repeating
    /// them here would be a second rendering of the same fact.
    /// </summary>
    public string DescribeOperand()
    {
        if (Operand == "target" && Target is uint target)
        {
            string sign = (long)target >= CodeRva ? "+" : "-";
            string offset = Math.Abs((long)target - CodeRva).ToString("X", CultureInfo.InvariantCulture);

            // A branch that works through a frame slot says which one first: the two words are what
            // the instruction's operand is, and a reader lifting the instruction needs both.
            string through = FrameSlot is int counter
                ? $"frame[{(counter < 0 ? "-" : "+")}0x{Math.Abs(counter):X}] then "
                : string.Empty;
            return through + $"{sign}0x{offset} -> 0x{target:X}"
                + (TargetIsInstructionStart ? string.Empty : " (not an instruction start)");
        }

        if (Operand == "slot" && FrameSlot is int slot)
        {
            return $"frame[{(slot < 0 ? "-" : "+")}0x{Math.Abs(slot):X}]";
        }

        return string.Empty;
    }
}

/// <summary>
/// One procedure of a p-code program: where its byte stream starts, how long it is, and — when asked
/// for — what is in it.
///
/// The stream is found the way the interpreter finds it. A method table entry points at a
/// <c>ProcDscInfo</c>, the structure that trails the code; the code starts <c>wPCodeBackOffset</c>
/// bytes before it. That field is a number the compiler wrote down rather than one inferred from the
/// bytes, so a stream either reaches its descriptor or the reason it did not is reported.
/// </summary>
public sealed class PcodeProcedure
{
    public int ObjectIndex { get; init; }

    public int MethodIndex { get; init; }

    public string ObjectName { get; init; } = string.Empty;

    /// <summary>The <c>ProcDscInfo</c> the method table points at.</summary>
    public uint DescriptorRva { get; init; }

    public uint CodeRva { get; init; }

    /// <summary>How long the stream is, which is what the descriptor's back-offset says.</summary>
    public int CodeBytes { get; init; }

    public int FrameSize { get; init; }

    public int ArgumentSize { get; init; }

    /// <summary>The descriptor's own size: 0x18 plus its cleanup table.</summary>
    public int DescriptorBytes { get; init; }

    public int InstructionCount { get; init; }

    /// <summary>
    /// How many readings of this stream reach its descriptor. One for almost every procedure; more
    /// when the table gives an opcode two lengths and the bytes fit either.
    /// </summary>
    public int Readings { get; init; }

    /// <summary>The instructions, when the run asked for them. Empty otherwise.</summary>
    public IReadOnlyList<PcodeInstruction> Instructions { get; init; } = [];

    /// <summary>
    /// Bytes between the last instruction and the descriptor. The compiler pads a stream out, so one
    /// to four are ordinary; anything longer is reported rather than swallowed.
    /// </summary>
    public int Padding { get; init; }

    /// <summary><c>exact</c>, <c>padded</c>, <c>unmeasured</c> or <c>overrun</c>.</summary>
    public string Status { get; init; } = "exact";

    /// <summary>The last instruction is ExitProcHresult, which is how a procedure ends.</summary>
    public bool EndsWithExit { get; init; }

    /// <summary>The bytes before the descriptor that are not an instruction, in hex.</summary>
    public string Trailer { get; init; } = string.Empty;

    public IReadOnlyList<string> Problems { get; init; } = [];

    public string Describe() =>
        $"{ObjectName}[{MethodIndex}]: {CodeBytes} bytes at 0x{CodeRva:X}, {InstructionCount} instructions ({Status})";
}

/// <summary>
/// The procedures of a Visual Basic 6 program compiled to p-code, and their instruction streams.
///
/// The program's structure is read by <see cref="Vb6Program"/>: the entry point names the header, the
/// header names the project data, the project data names the object table, and each object descriptor
/// names an <c>ObjectInfo</c> whose method count and dispatch table are filled in — in a p-code build
/// they are, where a native build leaves them empty, which is why this could not be read before one
/// was found. Each entry of that table is a <c>ProcDscInfo</c>, and the code starts a recorded number
/// of bytes before it.
///
/// The instructions are decoded with lengths measured out of the runtime's own handlers (see
/// <see cref="PcodeOpcodes"/>), including the four secondary tables the lead bytes select. Nothing here
/// comes from a published table: the stream is read against the interpreter that will run it.
/// </summary>
public sealed class PcodeProgram
{
    /// <summary>
    /// The three opcodes that end a procedure: 0x13 ExitProcHresult, which checks the error state on
    /// the way out, 0x14 ExitProc, and 0x15 ExitProcI2. All three are one byte and all three leave for
    /// the interpreter. Measured: every stream in the corpus ends with one of them and nothing else.
    /// </summary>
    private static bool IsExitOpcode(int opcode) => opcode is 0x13 or 0x14 or 0x15;

    /// <summary>Past this much slack before a descriptor, the stream is not read as padded.</summary>
    private const int MaxPadding = 4;

    public IReadOnlyList<PcodeProcedure> Procedures { get; init; } = [];

    public int ObjectsRead { get; init; }

    /// <summary>Method table slots with no address: procedures the project declares and never writes.</summary>
    public int EmptySlots { get; init; }

    /// <summary>Slots whose entry does not name this object: a table longer than the object's code.</summary>
    public int EntriesThatAreNotProcedures { get; init; }

    /// <summary>Objects whose method table is not filled in, and every other reason nothing was read.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>How long each opcode of each table is, as measured off the runtime.</summary>
    private sealed class Sizes
    {
        public required IReadOnlyList<OpcodeEvidence> Primary { get; init; }

        /// <summary>Lead byte to the table it selects.</summary>
        public required IReadOnlyDictionary<int, IReadOnlyList<OpcodeEvidence>> Led { get; init; }

        /// <summary>The lengths the table gives this opcode: one, several, or none.</summary>
        public List<int>? PrimarySizes(int opcode)
            => opcode < Primary.Count && Primary[opcode].Sizes.Count > 0 ? [.. Primary[opcode].Sizes] : null;

        public List<int>? LedSizes(int lead, int opcode)
            => Led.TryGetValue(lead, out var rows) && opcode < rows.Count && rows[opcode].Sizes.Count > 0
                ? [.. rows[opcode].Sizes]
                : null;

        /// <summary>
        /// What was measured about this opcode beyond its length — whether its handler loops over its
        /// own operands, which is how an instruction whose length is an operand word's is sized.
        /// </summary>
        public OpcodeEvidence? PrimaryEvidence(int opcode)
            => opcode < Primary.Count ? Primary[opcode] : null;

        public OpcodeEvidence? LedEvidence(int lead, int opcode)
            => Led.TryGetValue(lead, out var rows) && opcode < rows.Count ? rows[opcode] : null;

        public (string Name, bool Unhandled) Name(int lead, int opcode)
        {
            var rows = lead < 0 ? Primary : Led.TryGetValue(lead, out var led) ? led : null;
            if (rows is null || opcode >= rows.Count)
            {
                return (string.Empty, false);
            }

            return (rows[opcode].Name, rows[opcode].IsUnhandled);
        }
    }

    /// <summary>
    /// Reads every procedure of <paramref name="program"/> and decodes its stream.
    /// <paramref name="withInstructions"/> asks for the instructions themselves; without it only the
    /// shape of each stream is measured, which is what a run over a whole corpus wants.
    /// </summary>
    public static PcodeProgram Read(
        PeImage pe,
        byte[] bytes,
        Vb6Program program,
        IReadOnlyList<PcodeTableEvidence> tables,
        bool withInstructions = false)
    {
        var problems = new List<string>();
        var procedures = new List<PcodeProcedure>();
        var sizes = HasTables(tables) ? BuildSizes(tables) : null;
        int objectsRead = 0;
        int emptySlots = 0;
        int notProcedures = 0;

        foreach (var (obj, index) in program.Objects.Select((o, i) => (o, i)))
        {
            uint objectInfoVa = At(pe, bytes, obj.DescriptorRva, 4) ?? 0;
            uint objectInfo = objectInfoVa >= pe.ImageBase ? objectInfoVa - (uint)pe.ImageBase : 0;
            if (objectInfo == 0)
            {
                problems.Add($"{obj.Name}: the object descriptor does not point at an object");
                continue;
            }

            int count = (int)(At(pe, bytes, objectInfo + 0x20, 2) ?? 0);
            uint table = At(pe, bytes, objectInfo + 0x24, 4) ?? 0;
            if (count == 0 || table < pe.ImageBase)
            {
                problems.Add(
                    $"{obj.Name}: {count} method(s) and the dispatch table at 0x{table:X}, which is not a pointer");
                continue;
            }

            objectsRead++;
            uint tableRva = table - (uint)pe.ImageBase;
            for (int method = 0; method < count; method++)
            {
                uint entry = At(pe, bytes, tableRva + (uint)(method * 4), 4) ?? 0;
                if (entry < pe.ImageBase)
                {
                    // A slot with no address behind it: an event a project declares and never
                    // implements, or an interface slot the linker left empty. It is not a procedure.
                    emptySlots++;
                    continue;
                }

                uint descriptorRva = entry - (uint)pe.ImageBase;
                if (PeLoader.RvaToOffset(pe, descriptorRva) is null)
                {
                    // An entry pointing outside the image: the table is longer than the object's
                    // procedures, and what follows is whatever the linker put there next.
                    notProcedures++;
                    continue;
                }

                // A ProcDscInfo says which object it belongs to: its first field is the object's own
                // ObjectInfo. An entry that names a different object, or nothing, is not a procedure of
                // this one — which is how the matter of a table that is longer than the object's code
                // is settled without guessing.
                if (At(pe, bytes, descriptorRva, 4) != objectInfoVa)
                {
                    notProcedures++;
                    continue;
                }

                var procedure = ReadProcedure(pe, bytes, obj, index, method, descriptorRva, sizes, withInstructions);
                if (procedure is not null)
                {
                    procedures.Add(procedure);
                }
            }
        }

        return new PcodeProgram
        {
            Procedures = procedures,
            Problems = problems,
            ObjectsRead = objectsRead,
            EmptySlots = emptySlots,
            EntriesThatAreNotProcedures = notProcedures,
        };
    }

    private static PcodeProcedure? ReadProcedure(
        PeImage pe,
        byte[] bytes,
        Vb6Object obj,
        int objectIndex,
        int method,
        uint descriptorRva,
        Sizes? sizes,
        bool withInstructions)
    {
        int argumentSize = (int)(At(pe, bytes, descriptorRva + 0x04, 2) ?? 0);
        int frameSize = (int)(At(pe, bytes, descriptorRva + 0x06, 2) ?? 0);
        int back = (int)(At(pe, bytes, descriptorRva + 0x08, 2) ?? 0);
        int descriptorBytes = (int)(At(pe, bytes, descriptorRva + 0x0A, 2) ?? 0);

        var problems = new List<string>();
        if (back is 0 || back > 0x40000)
        {
            problems.Add($"the back-offset at 0x{descriptorRva + 0x08:X} is {back}, which is not a stream length");
            return new PcodeProcedure
            {
                ObjectIndex = objectIndex,
                MethodIndex = method,
                ObjectName = obj.Name,
                DescriptorRva = descriptorRva,
                Status = "unmeasured",
                DescriptorBytes = descriptorBytes,
                Problems = problems,
            };
        }

        uint codeRva = descriptorRva - (uint)back;
        if (PeLoader.RvaToOffset(pe, codeRva) is null)
        {
            problems.Add($"the stream at 0x{codeRva:X} is not in any section of the image");
            return new PcodeProcedure
            {
                ObjectIndex = objectIndex,
                MethodIndex = method,
                ObjectName = obj.Name,
                DescriptorRva = descriptorRva,
                CodeRva = codeRva,
                CodeBytes = back,
                Status = "unmeasured",
                DescriptorBytes = descriptorBytes,
                Problems = problems,
            };
        }

        int end = (int)descriptorRva;
        if (sizes is null)
        {
            // No runtime to measure against: the descriptor still states where the stream starts and how
            // long it is, which is everything about this procedure except what is inside it.
            return new PcodeProcedure
            {
                ObjectIndex = objectIndex,
                MethodIndex = method,
                ObjectName = obj.Name,
                DescriptorRva = descriptorRva,
                CodeRva = codeRva,
                CodeBytes = back,
                FrameSize = frameSize,
                ArgumentSize = argumentSize,
                DescriptorBytes = descriptorBytes,
                Status = "unmeasured",
            };
        }

        var search = Readings(pe, bytes, sizes, (int)codeRva, end);

        // Which reading to show when a stream has more than one: the one that ends with
        // ExitProcHresult, which is how a procedure ends; then the one that lands exactly on the
        // descriptor; then the first found. The others are counted rather than hidden.
        var chosen = search.Readings.FirstOrDefault(r => r.EndsWithExit)
            ?? search.Readings.FirstOrDefault(r => r.Padding == 0)
            ?? search.Readings.FirstOrDefault();

        string status;
        IReadOnlyList<PcodeInstruction> decoded = chosen?.Instructions ?? [];
        int padding = chosen?.Padding ?? 0;
        bool endsWithExit = chosen?.EndsWithExit == true;

        if (chosen is null)
        {
            status = "unmeasured";
            problems.Add(search.Reason);
        }
        else if (search.Readings.Count > 1)
        {
            status = "ambiguous";
            problems.Add(
                $"{search.Readings.Count} readings reach the descriptor — the table gives more than one length for " +
                "an opcode in this stream and the bytes fit each; the one ending with ExitProcHresult is listed");
        }
        else
        {
            status = padding == 0 ? "exact" : "padded";
        }

        return new PcodeProcedure
        {
            ObjectIndex = objectIndex,
            MethodIndex = method,
            ObjectName = obj.Name,
            DescriptorRva = descriptorRva,
            CodeRva = codeRva,
            CodeBytes = back,
            FrameSize = frameSize,
            ArgumentSize = argumentSize,
            DescriptorBytes = descriptorBytes,
            InstructionCount = decoded.Count,
            Instructions = withInstructions ? decoded : [],
            Readings = search.Readings.Count,
            Padding = padding,
            Status = status,
            EndsWithExit = endsWithExit,
            Trailer = padding > 0 ? Hex(pe, bytes, (uint)(end - padding), padding) : string.Empty,
            Problems = problems,
        };
    }

    /// <summary>
    /// The measured length of every opcode of every table, by the bytes that select them: the primary
    /// table as itself, and each secondary table under the lead byte whose handler dispatches through
    /// it. A runtime whose tables are not these is a runtime the program was not built for, which is
    /// why the tables are passed in rather than looked up.
    /// </summary>
    /// <summary>
    /// True when there is a runtime to measure against. A program's own procedure table can be read
    /// without one — the descriptors state each stream's extent — but nothing inside a stream can be
    /// measured, so a run without tables publishes extents and says the instructions are unmeasured
    /// rather than guessing lengths or refusing to list the procedures at all.
    /// </summary>
    public static bool HasTables(IReadOnlyList<PcodeTableEvidence> tables) => tables.Any(t => t.IsPrimary);

    /// <summary>
    /// How many bytes of the stream an instruction is written in: the bytes themselves, in hex, so a
    /// listing shows what is there rather than what the reader made of it.
    /// </summary>
    private static string Hex(byte[] bytes, int at, int size)
    {
        var text = new StringBuilder(size * 3);
        for (int i = 0; i < size; i++)
        {
            if (at + i >= bytes.Length)
            {
                break;
            }

            if (i > 0)
            {
                text.Append(' ');
            }

            text.Append(bytes[at + i].ToString("x2", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    private static Sizes BuildSizes(IReadOnlyList<PcodeTableEvidence> tables)
    {
        var primary = tables.FirstOrDefault(t => t.IsPrimary)?.Opcodes
            ?? throw new ArgumentException("the table set has no primary table", nameof(tables));

        var led = new Dictionary<int, IReadOnlyList<OpcodeEvidence>>();
        foreach (var table in tables.Where(t => t.LeadOpcode is >= 0 and <= 255))
        {
            led[table.LeadOpcode] = table.Opcodes;
        }

        return new Sizes { Primary = primary, Led = led };
    }

    /// <summary>
    /// The measured tables of a runtime, which is what a program is decoded against: the primary table
    /// and the four the lead bytes select, each read from the handlers the runtime dispatches to.
    /// </summary>
    public static IReadOnlyList<PcodeTableEvidence> ReadTables(IBinaryImage image, PeImage pe, byte[] bytes, PcodeRuntime runtime)
        => PcodeOpcodes.ReadAll(runtime, image, pe, bytes).Tables;

    /// <summary>The ways a stream can be read, and — when there are none — the furthest attempt.</summary>
    private sealed record SearchResult(List<Reading> Readings, string Reason);

    /// <summary>One way a procedure's stream reads: its instructions, where it stops, and how it ends.</summary>
    private sealed record Reading(IReadOnlyList<PcodeInstruction> Instructions, int Padding, bool EndsWithExit);

    /// <summary>
    /// Every way a procedure's stream can be read, given what the tables say about lengths.
    ///
    /// Almost every stream has exactly one reading. A handful contain an opcode the table gives two
    /// lengths for — the API call, whose operand size is in the import descriptor it names — and there
    /// the stream is read both ways and a reading is kept only if it reaches the descriptor. Where more
    /// than one does, that is a fact about the file and is reported as one rather than settled by
    /// preferring a length.
    ///
    /// A reading ends at the last ExitProcHresult within the padding window, because that is where a
    /// procedure ends: the bytes after it are the alignment the compiler put before the descriptor, and
    /// reading them as instructions would be reading padding.
    /// </summary>
    private static SearchResult Readings(PeImage pe, byte[] bytes, Sizes sizes, int start, int end)
    {
        const int MaxReadings = 4;
        const int MaxSteps = 200_000;

        var readings = new List<Reading>();
        var work = new Stack<(int At, List<PcodeInstruction> Path)>();
        work.Push((start, []));
        int steps = 0;
        int furthest = start;
        string reason = $"no reading of the stream at 0x{start:X} reaches the descriptor at 0x{end:X}";

        while (work.Count > 0 && readings.Count < MaxReadings && steps < MaxSteps)
        {
            var (at, path) = work.Pop();
            while (at <= end)
            {
                steps++;
                if (at > furthest)
                {
                    furthest = at;
                }

                if (at == end)
                {
                    // The path decoded the whole stream: the reading is the path up to its last exit,
                    // with whatever the compiler wrote between that and the descriptor as padding.
                    Add(Finalise(path, end));
                    break;
                }

                int opcode = (int)(At(pe, bytes, (uint)at, 1) ?? 0);
                int lead = sizes.Led.ContainsKey(opcode) ? opcode : -1;
                int tableOpcode = opcode;
                List<int>? candidates;

                if (lead >= 0)
                {
                    if (at + 1 >= end)
                    {
                        reason = $"the lead byte at 0x{at:X} has no byte after it before the descriptor";
                        Offer(path);
                        break;
                    }

                    tableOpcode = (int)(At(pe, bytes, (uint)(at + 1), 1) ?? 0);
                    candidates = sizes.LedSizes(lead, tableOpcode);
                    if (candidates is null)
                    {
                        reason = $"the length of 0x{lead:X2} 0x{tableOpcode:X2} at 0x{at:X} is not measured";
                        Offer(path);
                        break;
                    }
                }
                else
                {
                    candidates = sizes.PrimarySizes(opcode);
                    if (candidates is null)
                    {
                        reason = $"the length of 0x{opcode:X2} at 0x{at:X} is not measured";
                        Offer(path);
                        break;
                    }
                }

                // How many bytes are not in the secondary table's row: the lead byte itself, and the
                // byte after it that selects the row — which is what the *lead byte's* own row
                // measured, because its handler consumes exactly that byte before dispatching. Read
                // from the table rather than assumed, since assuming one of the two is how a stream of
                // lead instructions comes out a byte short each and never reaches its descriptor.
                int extra = lead >= 0 ? sizes.PrimarySizes(opcode)?.FirstOrDefault(1) ?? 1 : 0;
                int size = candidates[0] + extra;

                // An instruction whose length is its operand word's: the handler loops over the bytes
                // after the word, one pass per two of them, so the measured length describes one pass
                // and the word says how many. The word is in the stream being read, so the length is
                // read rather than assumed — and the table only says this is such an instruction when
                // the loop it found is the one around the operand.
                var countedEvidence = lead >= 0 ? sizes.LedEvidence(lead, tableOpcode) : sizes.PrimaryEvidence(opcode);
                if (countedEvidence is { IsCounted: true, CountedUnit: > 0 } counted && at + extra + 3 <= end)
                {
                    int operandBytes = (int)(At(pe, bytes, (uint)(at + extra + 1), 2) ?? 0);
                    size = candidates[0] - counted.CountedUnit + operandBytes + extra;
                }
                if (at + size > end)
                {
                    // The instruction does not fit. What is left is either the alignment the compiler
                    // put before the descriptor or a stream whose lengths do not describe it.
                    if (end - at > MaxPadding)
                    {
                        reason =
                            $"0x{opcode:X2} at 0x{at:X} is {size} bytes, which runs {at + size - end} past the descriptor";
                    }

                    Offer(path);
                    break;
                }

                var (name, unhandled) = sizes.Name(lead, tableOpcode);
                var evidence = lead >= 0 ? sizes.LedEvidence(lead, tableOpcode) : sizes.PrimaryEvidence(opcode);
                int sizeOfOperand = evidence?.OperandBytes ?? -1;
                int operandAt = evidence?.OperandAt ?? 0;

                // Where the handler sign-extends a frame slot from, when it does: the word the kind is
                // about for a slot operand, and the other word of the operand for a branch that works
                // through one — a For/Next, whose first word is its counter's slot.
                int slotAt = evidence is { OperandSlotAt: >= 0 } && evidence.OperandSlotAt + 2 <= sizeOfOperand
                    ? evidence.OperandSlotAt
                    : -1;

                // Where the instruction goes, when its operand says: the interpreter's rule is the
                // procedure's own code address plus the word, and the word is at the offset the table
                // measured. Nothing is resolved for an operand whose kind is not `target`, so a listing
                // cannot show a frame slot as a place to go.
                uint? target = null;
                if (evidence?.Operand == "target" && sizeOfOperand >= 2 && operandAt + 2 <= sizeOfOperand
                    && at + extra + 1 + operandAt + 2 <= end)
                {
                    int word = (int)(At(pe, bytes, (uint)(at + extra + 1 + operandAt), 2) ?? 0);
                    target = (uint)(start + word);
                }

                var instruction = new PcodeInstruction
                {
                    Rva = (uint)at,
                    Opcode = opcode,
                    Lead = lead,
                    TableOpcode = tableOpcode,
                    Size = size,
                    Bytes = Hex(bytes, at, size),
                    CodeRva = (uint)start,
                    Name = name,
                    Unhandled = unhandled,
                    Operand = evidence?.Operand ?? "none",
                    OperandBytes = sizeOfOperand,
                    OperandAt = operandAt,
                    Target = target,
                    FrameSlotAt = slotAt,
                    FrameSlot = slotAt >= 0 && slotAt + 2 <= sizeOfOperand
                        && at + extra + 1 + slotAt + 2 <= end
                            ? (short)(At(pe, bytes, (uint)(at + extra + 1 + slotAt), 2) ?? 0)
                            : null,
                };

                if (candidates.Count > 1)
                {
                    // Read the stream each other way from here; the descriptor decides which fits.
                    for (int i = 1; i < candidates.Count; i++)
                    {
                        int other = candidates[i] + extra;
                        if (at + other <= end)
                        {
                            work.Push((at + other, [.. path, instruction with { Size = other }]));
                        }
                    }
                }

                path = [.. path, instruction];
                at += size;
            }
        }

        return new SearchResult(readings, readings.Count > 0 ? string.Empty : reason);

        // A path that cannot be decoded further may still be the procedure: the compiler pads the tail
        // out with the alignment before the descriptor, and those fill bytes are not p-code. Where the
        // path already reached an exit that is within that padding of the descriptor, the reading is
        // the path up to the exit and the fill is the padding — the same reading Finalise would have
        // taken had the walk been able to step over the fill.
        void Offer(List<PcodeInstruction> path)
        {
            if (Finalise(path, end) is { } reading)
            {
                readings.Add(reading);
            }
        }

        void Add(Reading? reading)
        {
            if (reading is not null)
            {
                readings.Add(reading);
            }
            else
            {
                reason = $"the stream at 0x{start:X} has no ExitProcHresult in its last {MaxPadding} bytes";
            }
        }
    }

    /// <summary>
    /// A path that reached the descriptor, as a reading — or nothing, when it did not.
    ///
    /// A procedure ends with ExitProcHresult and the compiler pads the stream out to the descriptor, so
    /// where the last few bytes hold that opcode, everything after it is padding and the reading ends
    /// there. Where they do not, the reading is the whole path and the tail is measured as slack.
    /// </summary>
    private static Reading? Finalise(List<PcodeInstruction> path, int end)
    {
        // Where the targets go, once the whole reading is known: a branch that lands on an instruction
        // start of the same procedure is the interpreter's rule working, and one that lands anywhere
        // else is worth showing rather than hiding. This is the check the corpus is held to — all 845
        // branch operands of the 42 programs land on a start — asked of every listing.
        var starts = path.Select(i => i.Rva).ToHashSet();
        for (int i = 0; i < path.Count; i++)
        {
            if (path[i].Target is uint target && starts.Contains(target))
            {
                path[i] = path[i] with { TargetIsInstructionStart = true };
            }
        }

        for (int i = path.Count - 1; i >= 0; i--)
        {
            int stop = (int)(path[i].Rva + (uint)path[i].Size);
            if (IsExitOpcode(path[i].Opcode) && end - stop <= MaxPadding)
            {
                return new Reading([.. path.Take(i + 1)], end - stop, true);
            }
        }

        // An empty path is not a reading: nothing was decoded, so nothing reaches the descriptor.
        if (path.Count == 0)
        {
            return null;
        }

        int reached = (int)(path[^1].Rva + (uint)path[^1].Size);
        if (path[^1].Rva >= (uint)end)
        {
            return null;
        }

        return end - reached <= MaxPadding ? new Reading([.. path], end - reached, false) : null;
    }

    private static string Hex(PeImage pe, byte[] bytes, uint rva, int count)
    {
        var parts = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            parts.Add($"{At(pe, bytes, rva + (uint)i, 1) ?? 0:X2}");
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// A little-endian field at an address, or null when the address is not in the image — a field that
    /// is not there reads as absent rather than as zero, so nothing is decided on a missing byte.
    /// </summary>
    private static uint? At(PeImage pe, byte[] bytes, uint rva, int size)
    {
        if (PeLoader.RvaToOffset(pe, rva) is not int offset || offset < 0 || offset + size > bytes.Length)
        {
            return null;
        }

        return size switch
        {
            1 => bytes[offset],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2)),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)),
            _ => null,
        };
    }
}
