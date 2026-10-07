using System.Buffers.Binary;
using Iced.Intel;
using Recon.Analysis;
using Recon.Images;
using Recon.Pe;

namespace Recon.Vb6;

/// <summary>
/// What one p-code opcode does, as far as the interpreter's own code says.
///
/// Nothing here is taken from a table somebody wrote down. The opcode's handler is an address read
/// out of the runtime's dispatch table; the calls are the ones that handler makes, resolved against
/// the runtime's own export table; and the size is how far the handler moves the instruction pointer
/// when it takes its operand.
/// </summary>
public sealed class OpcodeEvidence
{
    public int Opcode { get; init; }

    public uint HandlerRva { get; init; }

    /// <summary>
    /// True when this opcode goes where the interpreter sends a case it does not implement, so there
    /// is no handler to read: the slot exists in the table and nothing is behind it.
    /// </summary>
    public bool IsUnhandled { get; init; }

    /// <summary>
    /// Bytes the handler consumes, including the opcode's own byte, when it moves the instruction
    /// pointer by a constant. Null when the size depends on the operand, which is what a branch or a
    /// variable-length operand looks like from here.
    /// </summary>
    public int? InstructionSize { get; init; }

    /// <summary>The sizes measured when they are not all the same.</summary>
    public IReadOnlyList<int> Sizes { get; init; } = [];

    /// <summary>
    /// The runtime's own functions this handler calls, by export name. This is the evidence a name is
    /// derived from, so it is reported whether or not a name was derived.
    /// </summary>
    public IReadOnlyList<string> Calls { get; init; } = [];

    /// <summary>
    /// What the instruction does to the operand stack, in 4-byte slots: negative for the values it
    /// takes off, positive for the one it leaves. Empty where the handler's own code does not say —
    /// a stack pointer restored from memory, a call through a pointer, an amount read from the
    /// instruction's operand — and more than one entry where its paths disagree, which is reported
    /// rather than averaged.
    /// </summary>
    public IReadOnlyList<int> StackEffects { get; init; } = [];

    /// <summary>
    /// Why a path that hands the next opcode back has no number: the call through a pointer, the stack
    /// pointer written by something the walk cannot follow. Stated on the row so that "no effect" is a
    /// reading and not a shrug.
    /// </summary>
    public IReadOnlyList<string?> Unmeasured { get; init; } = [];

    /// <summary>
    /// Addresses outside the handler's own code that its paths jump to. Which of them are the
    /// runtime's error paths is decided from every handler of the runtime at once, after the walk.
    /// </summary>
    public IReadOnlyList<uint> RaisedJumps { get; init; } = [];

    /// <summary>
    /// The jumps this handler's own code makes *with an error code loaded* — `mov eax, 9C68h` then
    /// `jmp` — each with the register the code was loaded into. Together they are where the function
    /// that raises is read off, because the block they land in pushes that register and calls it.
    /// </summary>
    public IReadOnlyList<(uint Target, string Owner)> RaiseJumps { get; init; } = [];

    /// <summary>
    /// The routines this handler calls that never come back — no `ret` anywhere in them. The call is the
    /// end of the path that made it, and one of these is the runtime's raiser: a handler that calls it
    /// can raise without ever jumping to the error block itself.
    /// </summary>
    public IReadOnlyList<uint> NoReturnCalls { get; init; } = [];

    /// <summary>
    /// Whether a path through the handler reaches the runtime's error path: the address the
    /// interpreter jumps to with an error code loaded, or the shared code the arithmetic jumps to on
    /// overflow. An instruction that can raise is one whose listing needs to say so.
    /// </summary>
    public bool Raises { get; init; }

    /// <summary>Why the stack effect is the number it is, or why there is none.</summary>
    public string StackBasis { get; init; } = string.Empty;

    /// <summary>
    /// Runtime functions the handler reaches through a helper it calls, and did not call itself. A
    /// handler whose only call is to a helper is not silent: the helper is where the work is, and
    /// this is what the helper calls.
    /// </summary>
    public IReadOnlyList<string> ReachedCalls { get; init; } = [];

    /// <summary>Calls that are not to an export: internal helpers, and virtual calls through a pointer.</summary>
    public int InternalCalls { get; init; }

    public int IndirectCalls { get; init; }

    /// <summary>How many instructions the handler was found to consist of.</summary>
    public int Instructions { get; init; }

    /// <summary>
    /// Whether the handler loops over operands of its own, so that the instruction's length is not one
    /// number at all. Reading a list of variables to free is such an instruction: its handler reads a
    /// 16-bit byte count and then that many bytes, two at a time, one pass of a loop per pair. Saying
    /// the length is one number describes the first pass and nothing else, and a stream decoded that
    /// way reads the rest of the operand list as instructions — which is what a stream of Mandelbrot's
    /// did, and why the decoder sizes those instructions from their own operand word.
    /// </summary>
    public bool IsCounted { get; init; }

    /// <summary>What one pass of that loop consumes, in bytes.</summary>
    public int CountedUnit { get; init; }

    /// <summary>
    /// What the bytes after the opcode are: <c>none</c> when the instruction has no operand,
    /// <c>data</c> when the handler reads them as a value, <c>target</c> when it reads them as where
    /// control goes. Read from the handler's own instructions — see the listing's operand column.
    /// </summary>
    public string Operand { get; init; } = "none";

    /// <summary>
    /// How many bytes of operand the handler reads, or <c>-1</c> when its own length is not one
    /// number. Stated beside the kind because a branch in this interpreter is the same 16-bit word as
    /// a frame slot, and what makes one a target is what the handler does with it.
    /// </summary>
    public int OperandBytes { get; init; }

    /// <summary>
    /// Where in the operand the word the kind is about sits, in bytes from the first byte after the
    /// opcode. It is not always zero: every <c>For</c>/<c>Next</c> instruction takes the frame slot of
    /// its counter first and the distance it branches by second, and a listing that resolved the first
    /// two bytes of those would point at the counter.
    /// </summary>
    public int OperandAt { get; init; }

    /// <summary>
    /// Where in the operand the word of a frame slot sits, or <c>-1</c> when the handler does not
    /// sign-extend one. Separate from <see cref="OperandAt"/> because an operand can hold both: every
    /// <c>For</c>/<c>Next</c> instruction names the frame slot of its counter and then the distance it
    /// branches by, and a reader who wants to lift the instruction needs the slot as much as the
    /// branch.
    /// </summary>
    public int OperandSlotAt { get; init; } = -1;

    /// <summary>
    /// Whether the operand was read only in code this handler hands off to. Reported because the kind's
    /// basis says where the read is, and a read in shared code is not the handler's own instruction.
    /// </summary>
    public bool OperandReadInSharedCodeOnly { get; init; }

    /// <summary>The first place the handler read anything after the opcode, of any width.</summary>
    public uint? OperandReadRva { get; init; }

    /// <summary>Why the operand is the kind it is, in words.</summary>
    public string OperandBasis { get; init; } = string.Empty;

    /// <summary>
    /// A name derived from the evidence, when the evidence gives exactly one. Empty when the handler
    /// calls more than one thing that is not common to all opcodes, which is the case for several.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Why that name, in words: which call it came from, or why none was derived.</summary>
    public string Basis { get; init; } = string.Empty;

    /// <summary>
    /// This row with a name on it. Naming needs the whole runtime — a call is scaffolding only
    /// relative to how many other opcodes make it — so a table is read first and named afterwards,
    /// and this is the copy that carries the answer.
    /// </summary>
    /// <summary>
    /// This row with a name on it. Naming needs the whole runtime — a call is scaffolding only
    /// relative to how many other opcodes make it — so a table is read first and named afterwards.
    /// </summary>
    public OpcodeEvidence Named(string name, string basis) => Copy(this, name, basis, Raises, StackBasis);

    /// <summary>
    /// This row with the two facts that need every handler of the runtime before they can be stated:
    /// whether it can raise, and why its stack effect is what it is.
    /// </summary>
    public OpcodeEvidence WithErrorPath(bool raises, string stackBasis) => Copy(this, Name, Basis, raises, stackBasis);

    /// <summary>
    /// The row's copy, in one place. Two separate copies of this listing is how a field stops
    /// travelling: the first revision of the naming pass had two, and the addresses a handler jumps to
    /// were dropped by the one that ran first — which looked exactly like the walk never having found
    /// them.
    /// </summary>
    private static OpcodeEvidence Copy(OpcodeEvidence row, string name, string basis, bool raises, string stackBasis) => new()
    {
        Opcode = row.Opcode,
        HandlerRva = row.HandlerRva,
        IsUnhandled = row.IsUnhandled,
        InstructionSize = row.InstructionSize,
        Sizes = row.Sizes,
        StackEffects = row.StackEffects,
        Unmeasured = row.Unmeasured,
        Raises = raises,
        StackBasis = stackBasis,
        RaisedJumps = row.RaisedJumps,
        RaiseJumps = row.RaiseJumps,
        NoReturnCalls = row.NoReturnCalls,
        Calls = row.Calls,
        ReachedCalls = row.ReachedCalls,
        InternalCalls = row.InternalCalls,
        IndirectCalls = row.IndirectCalls,
        Instructions = row.Instructions,
        IsCounted = row.IsCounted,
        CountedUnit = row.CountedUnit,
        Operand = row.Operand,
        OperandAt = row.OperandAt,
        OperandSlotAt = row.OperandSlotAt,
        OperandReadInSharedCodeOnly = row.OperandReadInSharedCodeOnly,
        OperandReadRva = row.OperandReadRva,
        OperandBytes = row.OperandBytes,
        OperandBasis = row.OperandBasis,
        Name = name,
        Basis = basis,
    };
}

/// <summary>
/// One dispatch table of a Visual Basic 6 runtime, with every one of its 256 slots read.
///
/// The runtime has five of these. The primary table is indexed by the first byte of an instruction;
/// the other four are indexed by the byte *after* one of four lead bytes, which is what
/// <see cref="LeadOpcode"/> names — the lead byte's own handler dispatches through the table, so the
/// pairing is read out of the runtime rather than assumed.
/// </summary>
public sealed record PcodeTableEvidence
{
    public required uint TableRva { get; init; }

    /// <summary>
    /// The opcode byte that selects this table: -1 for the primary table, the one lead byte whose
    /// handler dispatches through it when there is exactly one, and -2 when the pairing is not a
    /// single byte — which no runtime read so far produces and which is reported rather than guessed.
    /// </summary>
    public required int LeadOpcode { get; init; }

    public required int UsedBySites { get; init; }

    public required IReadOnlyList<OpcodeEvidence> Opcodes { get; init; }

    /// <summary>
    /// The primary opcodes whose handlers dispatch through this table. The pairing below is decided
    /// from this list, so a reader can disagree with it: one claimant makes the table that lead byte's,
    /// none or several make it a table without a lead.
    /// </summary>
    public IReadOnlyList<int> ClaimedBy { get; init; } = [];

    public bool IsPrimary => LeadOpcode < 0;

    /// <summary>How many opcodes of this table were measured at a single length.</summary>
    public int Measured => Opcodes.Count(o => o.InstructionSize is not null);
}

/// <summary>
/// The 256 opcodes of a Visual Basic 6 runtime, each one read from the handler the runtime dispatches
/// it to.
///
/// The opcode table says *where* each opcode goes; this says what is there. A handler is a short run
/// of the interpreter's own code that ends by dispatching the next opcode, so the walk is bounded by
/// the interpreter itself: decode from the handler's address, follow its branches, and stop where it
/// dispatches again. What it calls on the way is the evidence — a handler that calls
/// <c>__vbaVarCat</c> is the opcode that concatenates, and that is a fact about the shipped file
/// rather than a name from a reconstruction of it.
///
/// **Why a name is only derived when there is one call.** Handlers call all sorts of things: helpers,
/// cleanup, the runtime's own primitives. A name taken from the wrong call would be exactly the kind
/// of plausible reconstruction this reader exists to replace, so when the evidence is ambiguous the
/// calls are reported and no name is claimed.
/// </summary>
public static class PcodeOpcodes
{
    /// <summary>
    /// The dispatch every handler ends with: <c>jmp dword ptr [eax*4+imm32]</c>, seven bytes. The
    /// interpreter loads the next opcode into <c>eax</c> just before it, in one of several ways — the
    /// plain one is eleven bytes, a handler that takes an operand loads it from further along — so
    /// what is matched here is the jump itself rather than the sequence in front of it. That is also
    /// what keeps the walk inside a handler: stop at the jump and the handler is what was read.
    /// </summary>
    private const int DispatchJumpLength = 7;

    /// <summary>
    /// How far a conditional branch may go and still be read as the handler's own code. The handlers
    /// here are short runs and branch within themselves; the shared code they all reach — overflow
    /// paths, error paths — is further out than this.
    /// </summary>
    private const int BranchReach = 0x100;

    /// <summary>Distance from a handler past which a branch target is somebody else's code.</summary>
    private const int HandlerReach = 0x1000;

    /// <summary>A handler that has not dispatched the next opcode after this many instructions is not one.</summary>
    private const int MaxInstructionsPerHandler = 400;

    /// <summary>
    /// How many jumps out of a handler may be followed. Two: one to leave the handler and one more,
    /// which is as far as the interpreter's own shared code goes — a dispatcher reached from a
    /// dispatcher. Past that the code being read belongs to one handler in name only.
    /// </summary>
    private const int MaxTailHops = 2;

    /// <summary>
    /// How many instructions before a dispatch jump its own sequence can start. The sequence is the
    /// fetch, the move and the jump; anything more than a few back is not part of it.
    /// </summary>
    private const int DispatchSequenceSpan = 4;

    /// <summary>Whether the instruction is a branch: conditional jumps are branches too.</summary>
    private static bool IsBranch(DecodedInsn insn) => insn.IsJump || insn.IsConditionalJump;

    /// <summary>Whether a value a handler loaded is an address in this image's code.</summary>
    private static bool IsCodeAddress(PeImage pe, long value)
        => value >= (long)pe.ImageBase && value - (long)pe.ImageBase < (long)pe.ImageBase;

    /// <summary>
    /// The target of a jump through a register, when the path taken to reach the jump loaded that
    /// register with an address: <c>mov edi, 66109E7Bh</c> … <c>jmp edi</c>. The address is in the
    /// instruction that loaded it, so this is a reading of the handler's own code.
    ///
    /// The registers are the ones the path itself set, not the ones any earlier walk of this handler
    /// set: this interpreter writes a continuation into a register and then jumps *backwards*, into
    /// code shared by several opcodes —
    ///
    /// ```asm
    /// 66 109E53: mov edi, 66109E7Bh      ; this opcode's continuation
    /// 66 109E58: jmp 66109E4Ah           ; into the shared prologue
    /// 66 109E4Ah: pop ebx …              ; …which ends in
    /// 66 109E51: jmp edi                 ; an *earlier* address than the move that set edi
    /// ```
    ///
    /// so a register is resolved by the path that reached the jump, and an address comparison would
    /// refuse the one case the walk most needs.
    /// </summary>
    private static uint? ResolveRegisterJump(DecodedInsn insn, IReadOnlyDictionary<string, uint>? registers)
    {
        if (registers is null)
        {
            return null;
        }

        foreach (string register in insn.ReadRegisters)
        {
            if (registers.TryGetValue(register, out uint target))
            {
                return target;
            }
        }

        return null;
    }

    /// <summary>
    /// The primary table's 256 opcodes. The four secondary tables hold the byte *after* a lead byte
    /// and are read with <see cref="ReadAll"/>; reading them here would put four sets of numbers
    /// beside the primary one with nothing saying which is which.
    /// </summary>
    public static IReadOnlyList<OpcodeEvidence> Read(PcodeRuntime runtime, IBinaryImage image, PeImage pe, byte[] bytes)
        => ReadAll(runtime, image, pe, bytes).Tables.FirstOrDefault(t => t.IsPrimary)?.Opcodes ?? [];

    /// <summary>
    /// Every dispatch table the runtime has, each read as the instruction set it is: the primary table
    /// under its own 256 bytes, and each secondary table under the lead byte that selects it.
    ///
    /// The pairing between a lead byte and a table is read out of the runtime: the primary handler for
    /// 0xFB dispatches through one of the other tables, 0xFC through another, and so on. A lead byte
    /// whose handler does not dispatch through a table this runtime has is reported with no table
    /// rather than given one.
    /// </summary>
    public static PcodeOpcodeReading ReadAll(PcodeRuntime runtime, IBinaryImage image, PeImage pe, byte[] bytes)
    {
        var primary = runtime.PrimaryTable;
        if (primary is null)
        {
            return new PcodeOpcodeReading([], 0);
        }

        var secondary = runtime.Tables.Where(t => !t.IsPrimary).ToDictionary(t => t.TableRva);
        var leads = new Dictionary<uint, SortedSet<int>>();
        uint unhandled = runtime.UnhandledCaseHandlerRva ?? 0;

        // Every opcode's entry in the runtime, not just this table's. A handler's code can end by
        // falling into the one next to it in the file — an error path that does not return — and the
        // entry addresses are what says where one opcode's code stops and another's starts. Without
        // them, the fall-through is read as operands of an instruction it has nothing to do with.
        var entries = runtime.Tables.SelectMany(t => t.Handlers).Where(h => h != 0).ToHashSet();

        // How many opcodes — of the runtime, not of one table — call each function. A function that a
        // large fraction of them reach is scaffolding: the interpreter's own housekeeping, or the
        // error path every handler ends in. It cannot be what tells one opcode from another, and a
        // count taken per table would let the error path through in the tables that are smaller.
        var frequency = new CallFrequency();

        var tables = new List<PcodeTableEvidence>
        {
            Read(primary, -1, image, pe, bytes, entries, secondary.Keys.ToHashSet(), leads, unhandled, frequency),
        };

        foreach (var table in runtime.Tables.Where(t => !t.IsPrimary))
        {
            var claimants = leads.TryGetValue(table.TableRva, out var claimed) ? claimed : [];
            int lead = claimants.Count == 1 ? claimants.Min : -2;
            tables.Add(Read(table, lead, image, pe, bytes, entries, null, null, unhandled, frequency) with { ClaimedBy = [.. claimants] });
        }

        // Only now is the count complete: every table has been walked, so the calls that are common
        // across the runtime are known, and each table can decide which of its rows they leave.
        var scaffold = frequency.Scaffolding();
        var raisesCache = new Dictionary<uint, bool>();

        // The function the runtime raises through, read off the blocks the handlers themselves jump to
        // with an error code loaded.
        var raisingDecoder = new CodeDecoder(image) { Data = bytes };
        uint raiser = FindRaiser(raisingDecoder, tables.SelectMany(x => x.Opcodes).SelectMany(r => r.RaiseJumps));


        for (int i = 0; i < tables.Count; i++)
        {
            tables[i] = tables[i] with
            {
                Opcodes = [.. tables[i].Opcodes.Select(Rename)] ,
            };
        }

        OpcodeEvidence Rename(OpcodeEvidence row)
        {
            // A row the runtime does not handle is not an instruction and does not get a name: the
            // interpreter's own table says so, its slot points at the handler that raises, and the
            // name of the operation would be the name of the error. 188 slot of this runtime are
            // that, and an earlier revision named all 188 of them `RtlUnwind` — which is exactly the
            // uniform, plausible, wrong answer this reader is built to refuse.
            if (row.IsUnhandled)
            {
                string why = row.HandlerRva == 0
                    ? "the runtime's dispatch table has no handler in this opcode's slot"
                    : "the runtime sends this opcode to the handler that raises: it has no case for it";
                row = row.Named(string.Empty, $"{why}, so there is no instruction to name");
            }
            else
            {
                string name = DeriveName([.. row.Calls], [.. row.ReachedCalls], scaffold, out string basis);
                row = row.Named(name, basis);
            }

            // An instruction that reaches the runtime's error path can raise, and its listing needs to
            // say so. Reaching it is a fact about the handler's own code — the jump is in it — and the
            // error path is identified by how many handlers share the destination.
            // A handler raises if some code it jumps to can raise: the addresses are the ones its own
            // paths left by, and each is followed until the raiser is called or a return is met.
            bool raises = row.RaisedJumps.Any(jump => Raises(raisingDecoder, raisesCache, raiser, jump))
                || (raiser != 0 && row.NoReturnCalls.Contains(raiser));

            string stackBasis = row.StackEffects.Count switch
            {
                1 => "the handler's own instructions: what it pushes, what it pops, and what the routines it calls remove at their `ret`",
                > 1 => $"the handler's paths disagree, so all their effects are stated: {string.Join(", ", row.StackEffects)} slots",
                _ when row.Unmeasured.Count > 0 =>
                    $"the paths that hand the next opcode back have no single amount: {string.Join("; ", row.Unmeasured)}",
                _ => "every path this handler takes leaves the interpreter — it raises or calls a routine that does not come back — so no next opcode is read from it",
            };

            return row.WithErrorPath(raises, stackBasis);
        }

        return new PcodeOpcodeReading(tables, raiser);
    }

    /// <summary>
    /// What reading the runtime's opcode tables produced: the tables themselves, and the address of the
    /// function the handlers raise through — a fact about the runtime that outlives the reading, and the
    /// basis of every row's <c>raises</c> flag.
    /// </summary>
    public sealed record PcodeOpcodeReading(IReadOnlyList<PcodeTableEvidence> Tables, uint RaiserRva);

    private static PcodeTableEvidence Read(
        PcodeDispatchTable table,
        int leadOpcode,
        IBinaryImage image,
        PeImage pe,
        byte[] bytes,
        IReadOnlySet<uint> entries,
        IReadOnlySet<uint>? leadTables,
        Dictionary<uint, SortedSet<int>>? leadsOut,
        uint unhandledHandlerRva,
        CallFrequency frequency)
    {
        var exports = pe.Exports
            .Where(e => e.Name is not null && e.Forwarder is null)
            .GroupBy(e => e.Rva)
            .ToDictionary(g => g.Key, g => g.First().Name!);

        uint unhandled = unhandledHandlerRva;
        // Registers are tracked: a handler that loads a continuation into a register and jumps through
        // it, and one that moves the instruction pointer by a register, are both read from what the
        // instructions say they touch. Without this the walk sees neither, and a handler that restores
        // esi from a register is measured as though it had not.
        var decoder = new CodeDecoder(image) { Data = bytes, TrackRegisters = true };

        // Walk every handler and record what it calls. Which of those calls distinguishes an opcode is
        // a question about the whole runtime, so it is answered after all the tables are read.
        // What each routine the handlers call removes from the stack when it returns, remembered by
        // address: the same routine is called from many handlers, and its `ret N` does not change.
        var cleanups = new Dictionary<uint, RetShape>();

        var walked = new List<Row>();

        // How wide the opcode is: one byte in the primary table, two where a lead byte opened the
        // table, so the operand starts after it. A measured length counts from the opcode byte in both
        // tables — a lead byte is not part of any row's own measurement — which is why a row's operand
        // is one byte less than its length and not two less in the tables a lead byte opened.
        int opcodeBytes = leadOpcode < 0 ? 1 : 2;

        // A second decoder, for reading what a handler does with the bytes after its opcode. The
        // walk's decoder is shared and carries the state the walk left in it; a question asked of the
        // same handlers along a different path is asked with one of its own.
        var operands = new CodeDecoder(image) { Data = bytes, TrackRegisters = true };

        for (int opcode = 0; opcode < table.Handlers.Count; opcode++)
        {
            uint handler = table.Handlers[opcode];
            if (handler == 0)
            {
                walked.Add(new Row(opcode, handler, [], [], [], [], [], false, [], [], [], 0, 0, 0, [], false, 0,
                    "none", 0, 0, -1, false, null, "the runtime's table has no handler for this opcode"));
                continue;
            }

            var result = Walk(decoder, pe, bytes, exports, handler, entries, cleanups);

            // What the bytes after the opcode are. The kind comes from the instructions that read
            // them, never from a table of names: a table of names would be a second copy of a fact
            // this reader is supposed to be measuring, and it would agree with itself.
            var (kind, operandAt, operandSlotAt, operandBasis) = ClassifyOperand(
                operands, bytes, handler, opcodeBytes, result.Sizes, result.Unit, result.Counted,
                result.OperandReads, result.OperandReadOnlyInSharedCode, result.OperandReadRva, entries);
            walked.Add(new Row(
                opcode,
                handler,
                result.Calls,
                Reachable(decoder, bytes, pe, exports, result.InternalTargets),
                result.Sizes,
                result.StackEffects,
                result.StackEffects.Where(e => e.Slots is null).Select(e => e.Why).Distinct().ToList(),
                result.Raises,
                result.ExternalJumps,
                result.RaiseJumps,
                result.NoReturnCalls,
                result.Instructions,
                result.InternalCalls,
                result.IndirectCalls,
                result.DispatchTables,
                result.Counted,
                result.Unit,
                kind,
                operandAt,
                result.Sizes.Count == 1 ? result.Sizes[0] - 1 : -1,
                operandSlotAt,
                result.OperandReadOnlyInSharedCode,
                result.OperandReadRva,
                operandBasis));

            // A primary handler that dispatches through one of the *other* tables is the lead byte for
            // it: the lead byte is consumed, then the byte after it indexes the table its handler
            // names. That is where the pairing comes from — the lead handler's own jump — and a
            // handler dispatching through the primary table is just the interpreter's loop continuing.
            if (leadsOut is not null && leadTables is not null)
            {
                foreach (uint target in result.DispatchTables.Distinct().Where(leadTables.Contains))
                {
                    if (!leadsOut.TryGetValue(target, out var claimants))
                    {
                        leadsOut[target] = claimants = [];
                    }

                    claimants.Add(opcode);
                }
            }

            frequency.Note(result.Calls.Concat(Reachable(decoder, bytes, pe, exports, result.InternalTargets)), handler);
        }

        var rows = walked.Select(w =>
        {
            // A loop in a handler is not by itself a variable-length instruction: a handler may loop
            // over anything, and most that loop do it for reasons of their own. What makes the length
            // variable is that the loop is *around the operand*: the handler reads a word after the
            // opcode and then spends one pass of the loop per two bytes of it. That is measurable —
            // the length measured through one pass is the opcode byte, the word, and one pass —
            // and it is what tells these three from the eight other handlers that loop.
            bool counted = w.Counted && w.Unit > 0 && w.Sizes.Count == 1 && w.Sizes[0] == 3 + w.Unit;

            return new OpcodeEvidence
        {
            Opcode = w.Opcode,
            HandlerRva = w.Handler,
            IsUnhandled = w.Handler == 0 || (unhandled != 0 && w.Handler == unhandled),
            InstructionSize = w.Sizes.Count == 1 ? w.Sizes[0] : null,
            IsCounted = counted,
            CountedUnit = w.Unit,
            Operand = w.Operand,
            OperandAt = w.OperandAt,
            OperandSlotAt = counted ? -1 : w.OperandSlotAt,
            OperandReadInSharedCodeOnly = w.OperandReadInSharedCodeOnly,
            OperandReadRva = w.OperandReadRva,

            // A counted instruction's operand is a list whose length is in the stream, so the measured
            // size describes one pass through it and no number describes the operand.
            OperandBytes = counted ? -1 : w.OperandBytes,
            OperandBasis = w.OperandBasis,
            Sizes = w.Sizes,
            StackEffects = w.StackEffects.Where(e => e.Slots is not null).Select(e => e.Slots!.Value).Distinct().ToList(),
            Unmeasured = w.StackEffects.Where(e => e.Slots is null).Select(e => e.Why).Distinct().ToList(),
            RaisedJumps = w.ExternalJumps.Distinct().ToList(),
            RaiseJumps = w.RaiseJumps,
            NoReturnCalls = w.NoReturnCalls,
            Raises = w.WalkRaises,
            Calls = w.Calls.Distinct().ToList(),
            ReachedCalls = w.Reached.Distinct().ToList(),
            InternalCalls = w.Internal,
            IndirectCalls = w.Indirect,
            Instructions = w.Instructions,
            Name = string.Empty,
            Basis = string.Empty,
        };
        }).ToList();

        return new PcodeTableEvidence
        {
            TableRva = table.TableRva,
            LeadOpcode = leadOpcode,
            UsedBySites = table.UsedBySites,
            Opcodes = rows,
        };
    }

    /// <summary>
    /// The calls that are common enough to be scaffolding: made by this many different opcodes, they
    /// cannot be what distinguishes one from another. The threshold is a measured fraction rather than
    /// a guess — an eighth of the table — and the names that fall on the far side of it are the ones
    /// that name an operation.
    /// </summary>
    /// <summary>
    /// How many opcodes of the runtime call each function, counted two ways, so that "common" can be
    /// told from "identifying" without either count hiding the other.
    ///
    /// Counting rows alone would let one handler shared by many opcodes look like many separate
    /// observations — which is the mistake that made 188 unimplemented opcodes all report the same
    /// name, because they share the handler that raises the error. Counting handlers alone would miss
    /// the reverse: a function that most opcodes reach through their own code. A function is
    /// scaffolding when *either* count says it is common.
    /// </summary>
    private sealed class CallFrequency
    {
        private readonly Dictionary<string, int> _handlers = new(StringComparer.Ordinal);
        private readonly HashSet<uint> _seenHandlers = [];

        public void Note(IEnumerable<string> calls, uint handler)
        {
            if (!_seenHandlers.Add(handler))
            {
                return;
            }

            foreach (string call in calls.Distinct())
            {
                _handlers[call] = _handlers.GetValueOrDefault(call) + 1;
            }
        }

        /// <summary>
        /// The functions that cannot tell one opcode from another: called by more than one in eight of
        /// the runtime's handlers, or by at least eight of them. Counted once per handler, because two
        /// opcodes that share a handler are one piece of code — and a call they both make is no more
        /// distinguishing for being counted twice.
        ///
        /// What this count is *not* used for is deciding whether an opcode is implemented. The handler
        /// the runtime sends its unimplemented slots to is one piece of code reached by one handler, so
        /// this count calls the error it raises identifying — and an earlier revision named all 188 of
        /// those opcodes after it. Whether a slot is a case the runtime handles is the dispatch table's
        /// own statement, and that is where the reader takes it from.
        /// </summary>
        public HashSet<string> Scaffolding()
        {
            int threshold = Math.Max(8, _seenHandlers.Count / 8);
            return _handlers.Where(kv => kv.Value >= threshold).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        }
    }

    private static string DeriveName(List<string> calls, List<string> reached, HashSet<string> scaffold, out string basis)
    {
        var distinguishing = calls.Distinct().Where(c => !scaffold.Contains(c)).ToList();
        var named = distinguishing.Where(IsNamed).ToList();
        var ordinal = distinguishing.Where(c => !IsNamed(c)).ToList();

        // A handler whose own code reads the instruction and then calls a helper it loaded into a
        // register may call no export at all, while the helper is a preface to exactly one. That is
        // still evidence, and it is evidence about this opcode: the name is the runtime function the
        // handler reaches, said to be reached through the helper rather than called by the handler.
        if (named.Count == 0)
        {
            var viaHelper = reached.Distinct().Where(c => IsNamed(c) && !scaffold.Contains(c)).ToList();
            if (viaHelper.Count == 1)
            {
                basis = $"the handler calls a helper, which reaches {viaHelper[0]}, and nothing else that is not shared";
                return NameFrom(viaHelper[0]);
            }

            if (viaHelper.Count > 1 && calls.Count == 0)
            {
                basis = $"the handler calls a helper, which reaches {viaHelper.Count} runtime functions: {string.Join(", ", viaHelper)}";
                return string.Empty;
            }
        }

        if (distinguishing.Count == 1 && named.Count == 1)
        {
            basis = $"the handler calls {named[0]}, and nothing else that is not shared with other opcodes";
            return NameFrom(named[0]);
        }

        if (named.Count == 0 && ordinal.Count > 0)
        {
            // An import by ordinal is evidence — it is in the file — but the name of the function is
            // not, and one is not invented for it: the opcode is described by the number it calls.
            basis = ordinal.Count == 1
                ? $"the handler calls {ordinal[0]}, which is imported by ordinal: the call is in the file and the function's name is not"
                : $"the handler calls {ordinal.Count} imports by ordinal: the calls are in the file and the functions' names are not";
            return string.Empty;
        }

        basis = distinguishing.Count == 0
            ? "the handler calls nothing that is not shared with other opcodes: what it does is in its own code"
            : $"the handler calls {distinguishing.Count} things that are not shared: {string.Join(", ", distinguishing)}";
        return string.Empty;
    }

    /// <summary>Whether a call is to something the file names: an export, or an import by name.</summary>
    private static bool IsNamed(string call) => !call.Contains("!#", StringComparison.Ordinal);

    /// <summary>
    /// The opcode's name, taken from the runtime function its handler reaches.
    ///
    /// The runtime's exports are the interpreter's own routines and are named as C functions are:
    /// `__vbaStrCat`, `__vbaVarAdd`. The import name is the decoration of a DLL that a linker added,
    /// and it says nothing about the operation — `rtcMidCharBstr (MSVBVM60.DLL)` is the same
    /// evidence as `__vbaMidStmtBstr`, one import table over. So the leading underscores and the
    /// library suffix are dropped, which is what the names in circulation do: the instruction is
    /// `MidStmtBstr`, not `__vbaMidStmtBstr`.
    /// </summary>
    private static string NameFrom(string call)
    {
        string name = call;
        int decorated = name.IndexOf(" (", StringComparison.Ordinal);
        if (decorated > 0)
        {
            name = name[..decorated];
        }

        name = name.TrimStart('_');
        return name;
    }

    /// <summary>
    /// The exports reachable from the helpers a handler calls.
    ///
    /// A handler that reads the instruction pointer and then jumps into a routine it loaded into a
    /// register has said everything about itself except the name of the operation, and that routine
    /// is usually a few instructions in front of the export that does the work. Following the call
    /// one level — through the runtime's own code, stopping at the first export on each path — is
    /// what turns "the handler calls a helper" into a name. Level two is where a helper that only
    /// prepares a call sits, and there is no third level to reach: an export that calls another
    /// export is the export's own business, not this opcode's.
    ///
    /// What a helper reaches is only used when the handler itself calls no export, so this cannot
    /// overwrite a direct observation with a second-hand one.
    /// </summary>
    private static List<string> Reachable(CodeDecoder decoder, byte[] bytes, PeImage pe, Dictionary<uint, string> exports, List<uint> internalTargets)
    {
        if (internalTargets.Count == 0)
        {
            return [];
        }

        var reached = new List<string>();
        var seen = new HashSet<uint>();
        var work = new Stack<(uint Rva, int Depth)>(internalTargets.Select(t => (t, 0)));

        while (work.Count > 0 && seen.Count < 64)
        {
            var (rva, depth) = work.Pop();
            if (!seen.Add(rva) || depth > 2)
            {
                continue;
            }

            if (!DecodeAt(decoder, rva, out DecodedInsn insn))
            {
                continue;
            }

            // The helper is read as a straight run of instructions — a preface is instructions, not
            // one call — until it returns, branches, or runs longer than a preface plausibly is.
            // A branch is followed: the work is sometimes past a test, and a tail call is the
            // helper being a two-line hand-off. A dispatch is not: that jump belongs to the opcode
            // being interpreted, not to this helper.
            for (int step = 0; step < 16; step++)
            {
                if (insn.IsCall)
                {
                    if (insn.DirectTargetRva is uint target)
                    {
                        string? name = ResolveTarget(bytes, pe, exports, target);
                        if (name is not null)
                        {
                            reached.Add(name);
                        }
                        else if (depth < 2)
                        {
                            work.Push((target, depth + 1));
                        }
                    }
                    else
                    {
                        // A call through something the helper did not set is the end of what can be
                        // followed: past it the helper's own code is not this opcode's operation.
                        break;
                    }
                }

                // An unconditional jump is a tail call or a hand-off to the code that does the work,
                // and is followed. A *conditional* jump is not: a helper that tests something has an
                // error path, and the first thing on the runtime's error path is the unwind. Following
                // it would name 188 opcodes `RtlUnwind`, which is the shape of answer this whole
                // reader exists to refuse — plausible, uniform, and about the interpreter's plumbing
                // rather than about the operation.
                if (insn.IsJump && !insn.IsConditionalJump && insn.DirectTargetRva is uint destination)
                {
                    if (!IsDispatchJump(insn))
                    {
                        work.Push((destination, depth + 1));
                    }

                    break;
                }

                if (insn.IsConditionalJump)
                {
                    break;
                }

                if (insn.IsReturn)
                {
                    break;
                }

                uint next = rva + (uint)insn.Length;
                if (!DecodeAt(decoder, next, out insn))
                {
                    break;
                }

                rva = next;
            }
        }

        return reached;
    }

    /// <summary>
    /// Whether an instruction is the interpreter dispatching the next opcode: a jump through a scaled
    /// index with no base register, which is `jmp dword ptr [eax*4 + table]` and nothing else in the
    /// interpreter. Following that would read the next instruction's handler, not this opcode's.
    /// </summary>
    private static bool IsDispatchJump(DecodedInsn insn)
        => insn.IsJump && insn.MemoryRefs.Any(m => m.HasIndex && !m.HasBase);

    /// <summary>
    /// What one instruction does to the operand stack, in 4-byte slots: positive for a value left on
    /// it, negative for one taken off.
    ///
    /// The interpreter's operand stack is the machine's own stack, so a handler's `push` and `pop` are
    /// the instruction's effect and a call's is its callee's: the runtime's routines are stdcall, and
    /// the arguments a handler put on the stack are removed by the `ret N` at the end of the routine
    /// it called. That is why `call __vbaStrCat; push eax` — the whole body of the string
    /// concatenation — comes out as minus one slot rather than plus one, and why the reference table's
    /// "pop 2, push 1" is the same number seen from the other side.
    ///
    /// A `mov esp, ...`, an `and esp, ...` or a `leave` makes the amount unknowable: after any of them
    /// the pointer is wherever it says, and the difference from before is not something the walk can
    /// claim. Those are also the exits, which are recorded as exits rather than as effects.
    /// </summary>
    /// <summary>
    /// What one instruction does to the operand stack, and what the walk has to be told about it: that
    /// the amount is not knowable, or that the path ends here because the call does not come back.
    /// </summary>
    private readonly record struct StackChange(int Slots, bool Known, string? Why, uint NoReturn);

    private static StackChange StackDelta(DecodedInsn insn, CodeDecoder decoder, Dictionary<uint, RetShape> shapes)
    {
        switch (insn.Mnemonic)
        {
            case Mnemonic.Push:
            case Mnemonic.Pushfd:
                return new StackChange(1, true, null, 0);
            case Mnemonic.Pop:
            case Mnemonic.Popfd:
                return new StackChange(-1, true, null, 0);
            case Mnemonic.Pushad:
                return new StackChange(8, true, null, 0);
            case Mnemonic.Popad:
                return new StackChange(-8, true, null, 0);
            case Mnemonic.Add when WritesStackPointer(insn) && insn.Immediate is long added:
                return new StackChange(-(int)(added / 4), true, null, 0);
            case Mnemonic.Sub when WritesStackPointer(insn) && insn.Immediate is long taken:
                return new StackChange((int)(taken / 4), true, null, 0);
            case Mnemonic.Call:
                if (insn.DirectTargetRva is not uint target)
                {
                    return new StackChange(0, false, "a call through a pointer", 0);
                }

                RetShape shape = Shape(decoder, shapes, target);
                if (!shape.Returns)
                {
                    // The routine has no `ret` in it, so the call is the end of this path: nobody comes
                    // back to read the next instruction. The raiser is one of these — its whole job is
                    // to leave — and treating it as "a call whose amount is unknown" is what left more
                    // than half the handlers without a stack effect.
                    return new StackChange(0, true, null, target);
                }

                if (!shape.Agrees)
                {
                    return new StackChange(0, false, $"a call whose returns disagree, at {target:x}", 0);
                }

                return new StackChange(-(shape.Cleanup / 4), true, null, 0);
            case Mnemonic.Mov:
            case Mnemonic.Leave:
            case Mnemonic.And:
            case Mnemonic.Or:
            case Mnemonic.Xchg:
                return WritesStackPointer(insn)
                    ? new StackChange(0, false, $"the stack pointer written by {insn.Mnemonic}", 0)
                    : new StackChange(0, true, null, 0);
            default:
                return new StackChange(0, true, null, 0);
        }
    }

    private static bool WritesStackPointer(DecodedInsn insn)
        => insn.WrittenRegisters.Contains("ESP", StringComparer.OrdinalIgnoreCase)
            || insn.WrittenRegisters.Contains("RSP", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the code at an address can raise: whether the interpreter's raiser is reachable from it.
    ///
    /// The raiser is called, and the call is where we are told what raising means here:
    ///
    /// ```asm
    /// 66 108D50: push eax                 ; the error code the handler loaded
    /// 66 108D51: call 66385 2Ch           ; the runtime's raiser — it compares the code, 9C68h included
    /// ```
    ///
    /// so a block that the handlers jump to with an error code loaded is a raise *when it reaches that
    /// call*, and not because of the shape it was entered by. The first rule tried here — "the address
    /// is jumped to by many handlers" — called 43 opcodes raising that jump to a shared body doing
    /// nothing of the kind, and the second — "a called function whose first instruction is a push" — was
    /// true of almost every function in the file and made 652 opcodes raise, the exits among them.
    /// </summary>
    private static bool Raises(CodeDecoder decoder, Dictionary<uint, bool> cache, uint raiser, uint rva)
    {
        if (cache.TryGetValue(rva, out bool cached))
        {
            return cached;
        }

        cache[rva] = false;
        bool raises = raiser != 0 && ReachesRaiser(decoder, rva, raiser);
        cache[rva] = raises;
        return raises;
    }

    /// <summary>
    /// The function the runtime raises through, voted for by the code the handlers jump to with an error
    /// code loaded.
    ///
    /// The vote is the whole of it: the error blocks are entered with a code in a register, so the
    /// `push eax; call R` at the head of such a block is a raise, and R is the raiser. The blocks are
    /// not all shaped the same — most load the code and jump to a block that does the pushing, and the
    /// arithmetic fixups push what they were left — which is why the vote is taken over everything each
    /// of them reaches rather than over its first instruction.
    ///
    /// No threshold on the share of the vote: a raiser that only some of the error blocks agree on is
    /// still the raiser those blocks call, and demanding a majority of them left the field at "no raiser
    /// at all" on this build.
    /// </summary>
    private static uint FindRaiser(CodeDecoder decoder, IEnumerable<(uint Target, string Owner)> sites)
    {
        var callees = new Dictionary<uint, int>();
        foreach (var (target, owner) in sites.Distinct().Take(64))
        {
            if (owner.Length == 0)
            {
                continue;
            }

            // The block is entered with the code in a register, so a `push` of that register followed by
            // a call is the raise. Nothing else about the block is consulted.
            DecodedInsn? previous = null;
            foreach (var (_, insn) in Straightish(decoder, target))
            {
                if (previous is { Mnemonic: Mnemonic.Push } push
                    && insn.IsCall && insn.DirectTargetRva is uint callee && callee != 0
                    && push.ReadRegisters.Any(r => r.Equals(owner, StringComparison.OrdinalIgnoreCase)))
                {
                    callees[callee] = callees.GetValueOrDefault(callee) + 1;
                }

                previous = insn;
            }
        }

        return callees.Count == 0 ? 0 : callees.OrderByDescending(kv => kv.Value).First().Key;
    }

    /// <summary>
    /// Whether one of these instructions is reached from an address without leaving its neighbourhood,
    /// and without passing a return.
    ///
    /// This is what "the code at an address does this" means when the address is the middle of a block:
    /// a short jump within the block, a conditional branch out of it and back, the tail of a
    /// `mov eax, 6; jmp ...` that carries the error code into an address somebody else's handler also
    /// jumps to. What it does not do is follow a call — the instruction is being looked for, not its
    /// callees — or count anything past the first return.
    /// </summary>
    private static bool Reaches(CodeDecoder decoder, uint entry, Func<DecodedInsn, bool> found)
        => Straightish(decoder, entry).Any(pair => found(pair.Insn));

    /// <summary>
    /// Whether the code at an address raises: whether it pushes an error code and calls the raiser,
    /// which is that one block's own statement of it.
    /// </summary>
    private static bool ReachesRaiser(CodeDecoder decoder, uint entry, uint raiser)
    {
        DecodedInsn? previous = null;
        foreach (var (_, insn) in Straightish(decoder, entry))
        {
            if (previous is { Mnemonic: Mnemonic.Push }
                && insn.IsCall && insn.DirectTargetRva == raiser)
            {
                return true;
            }

            previous = insn;
        }

        return false;
    }

    private static IEnumerable<(uint Rva, DecodedInsn Insn)> Straightish(CodeDecoder decoder, uint entry)
    {
        var seen = new HashSet<uint>();
        var work = new Stack<uint>();
        work.Push(entry);
        int read = 0;

        while (work.Count > 0 && read < 80)
        {
            uint rva = work.Pop();
            while (seen.Add(rva) && read < 80)
            {
                DecodedInsn? insn = decoder.DecodeOne(rva);
                if (insn is null || insn.Length <= 0)
                {
                    break;
                }

                read++;
                yield return (rva, insn);

                if (insn.IsReturn)
                {
                    break;
                }

                if (insn.IsJump && insn.DirectTargetRva is uint target)
                {
                    bool near = Math.Abs((long)target - entry) <= BranchReach;
                    if (insn.IsConditionalJump)
                    {
                        if (near)
                        {
                            work.Push(target);
                        }
                    }
                    else if (near)
                    {
                        // A near jump stays inside this block, and may be how the code gets to the call.
                        rva = target;
                        continue;
                    }
                    else
                    {
                        // A far one is somebody else's code, and it has been read: that is the whole
                        // point of the handlers handing off to it.
                        break;
                    }
                }

                rva += (uint)insn.Length;
            }
        }
    }

    /// <summary>
    /// How many bytes of arguments the routine at an address removes before returning: the `ret N` of
    /// a stdcall function, 0 for a cdecl one, and null where the routine's end cannot be read.
    ///
    /// The value is read from the routine's own code and remembered, because the same routine is
    /// called by many handlers and the answer does not change. A routine with more than one `ret` is
    /// only used when every one of them cleans the same amount: a routine that cleans differently on
    /// different paths is one whose effect this cannot state.
    /// </summary>
    private static RetShape Shape(CodeDecoder decoder, Dictionary<uint, RetShape> shapes, uint target)
    {
        if (shapes.TryGetValue(target, out RetShape cached))
        {
            return cached;
        }

        // Marked before the walk so that a routine that reaches itself does not recurse for ever.
        shapes[target] = default;
        RetShape found = FindReturnShape(decoder, shapes, target);
        shapes[target] = found;
        return found;
    }

    /// <summary>
    /// What a routine does at its end: whether it returns at all, and how many bytes it removes when it
    /// does.
    ///
    /// Whether it returns is a separate fact from how much, and the difference matters: a routine with
    /// no `ret` anywhere never comes back, so a handler that calls it is finished — the raiser is one of
    /// these — while a routine whose `ret`s disagree does come back and its effect is simply not a fact
    /// about it.
    /// </summary>
    private readonly record struct RetShape(bool Returns, bool Agrees, int Cleanup);

    private static RetShape FindReturnShape(CodeDecoder decoder, Dictionary<uint, RetShape> shapes, uint entry)
    {
        // Where this routine's own code ends: the first `ret` in a linear reading from the entry. Above
        // that address the same region holds other routines — the runtime is a run of small thunks, one
        // after another, with a `ret` at the end of each — so a jump past it is a tail call into one of
        // them, and the amount *it* removes is the amount this call removes. Without the bound, the walk
        // read the rets of the neighbours and reported that the routine's own returns disagreed, which is
        // how 592 handler paths lost their stack effect: `ret 0Ch` here, `ret 4` in the next thunk.
        uint limit = LinearLimit(decoder, entry);

        var seen = new HashSet<uint>();
        var work = new Stack<uint>();
        work.Push(entry);

        var amounts = new HashSet<int>();
        bool returns = false;
        int read = 0;
        while (work.Count > 0 && read < 400)
        {
            uint rva = work.Pop();
            while (seen.Add(rva) && read < 400)
            {
                DecodedInsn? insn = decoder.DecodeOne(rva);
                if (insn is null || insn.Length <= 0)
                {
                    break;
                }

                read++;
                if (insn.IsReturn)
                {
                    returns = true;
                    amounts.Add(insn.RetPopBytes ?? 0);
                    break;
                }

                if (insn.IsJump || insn.IsConditionalJump)
                {
                    if (insn.DirectTargetRva is uint destination)
                    {
                        if (destination < limit)
                        {
                            work.Push(destination);
                        }
                        else
                        {
                            // A tail call: what the code over there removes is what this routine removes.
                            RetShape tail = Shape(decoder, shapes, destination);
                            if (tail.Returns)
                            {
                                returns = true;
                                if (!tail.Agrees)
                                {
                                    return new RetShape(true, false, 0);
                                }

                                amounts.Add(tail.Cleanup);
                            }
                        }
                    }

                    if (insn.IsConditionalJump)
                    {
                        rva += (uint)insn.Length;
                        continue;
                    }

                    break;
                }

                rva += (uint)insn.Length;
            }
        }

        return amounts.Count switch
        {
            1 when returns => new RetShape(true, true, amounts.First()),
            0 => new RetShape(returns, false, 0),
            _ => new RetShape(true, false, 0),
        };
    }

    /// <summary>
    /// Where a routine's own code ends: the address of the first `ret` read straight through from the
    /// entry, or the far end of the reading if it makes none.
    /// </summary>
    private static uint LinearLimit(CodeDecoder decoder, uint entry)
    {
        uint rva = entry;
        for (int read = 0; read < 512; read++)
        {
            DecodedInsn? insn = decoder.DecodeOne(rva);
            if (insn is null || insn.Length <= 0)
            {
                break;
            }

            if (insn.IsReturn)
            {
                return rva;
            }

            rva += (uint)insn.Length;
        }

        return rva;
    }

    /// <summary>Decode the instruction at an RVA, or say there is nothing to decode there.</summary>
    private static bool DecodeAt(CodeDecoder decoder, uint rva, out DecodedInsn insn)
    {
        insn = null!;
        DecodedInsn? decoded = decoder.DecodeOne(rva);
        if (decoded is null || decoded.Length <= 0)
        {
            return false;
        }

        insn = decoded;
        return true;
    }

    /// <summary>
    /// One opcode's walk, before the whole runtime has been seen: the evidence a row is built from,
    /// and the two things that can only be judged once every handler has been walked — which calls are
    /// scaffolding, and which addresses outside a handler are the runtime's error paths.
    /// </summary>
    private sealed record Row(
        int Opcode,
        uint Handler,
        List<string> Calls,
        List<string> Reached,
        List<int> Sizes,
        List<(int? Slots, int Hops, string? Why)> StackEffects,
        List<string?> Unmeasured,
        bool WalkRaises,
        List<uint> ExternalJumps,
        List<(uint Target, string Owner)> RaiseJumps,
        List<uint> NoReturnCalls,
        int Instructions,
        int Internal,
        int Indirect,
        List<uint> Tables,
        bool Counted,
        int Unit,
        string Operand,
        int OperandAt,
        int OperandBytes,
        int OperandSlotAt,
        bool OperandReadInSharedCodeOnly,
        uint? OperandReadRva,
        string OperandBasis);

    /// <summary>
    /// A path through a handler: where it is, what the instruction pointer has been moved by, the
    /// registers it loaded with addresses, whether it branched or moved the pointer by something the
    /// walk cannot follow, and the stack effect of everything it did on the way.
    ///
    /// The stack effect travels with the path for the same reason the consumed count does: two paths
    /// through one handler can leave the stack differently, and a number summed over both would be a
    /// number neither of them produced.
    /// </summary>
    private readonly record struct Path(
        uint Rva,
        int Consumed,
        int Hops,
        IReadOnlyDictionary<string, uint>? Registers,
        bool Branched,
        bool PointerMovedOther,
        int Slots,
        bool SlotsKnown,
        string? Unknown,
        long? Immediate,
        string? ImmediateOwner,
        bool Raises,
        bool Left);

    /// <summary>
    /// What one walk of a handler found. A class rather than a tuple: the walk answers more than a
    /// dozen questions now, and a positional tuple of that length is a place where two answers of the
    /// same type silently swap.
    /// </summary>
    private sealed class HandlerWalk
    {
        public List<string> Calls { get; } = [];

        public List<uint> InternalTargets { get; } = [];

        public List<int> Sizes { get; set; } = [];

        public List<(int? Slots, int Hops, string? Why)> StackEffects { get; set; } = [];

        public bool Raises { get; set; }

        public int Instructions { get; set; }

        public int InternalCalls { get; set; }

        public int IndirectCalls { get; set; }

        public List<uint> DispatchTables { get; set; } = [];

        public List<uint> ExternalJumps { get; } = [];

        public List<(uint Target, string Owner)> RaiseJumps { get; set; } = [];

        public List<uint> NoReturnCalls { get; set; } = [];

        /// <summary>
        /// Every read of two bytes of the operand on a path that stayed in this handler's own code: the
        /// instruction that read it, and how far the instruction pointer had already been moved when it
        /// did — which is where in the operand the read is, the two being the same thing.
        ///
        /// The advance is what makes the offset readable at all. A handler moves the pointer from field
        /// to field as it takes them — <c>movsx eax,word[esi]</c>, <c>add esi,2</c>, and the next field
        /// is <c>[esi]</c> again — so the same displacement means different operands at different
        /// points, and a scan that read one of these as offset zero would resolve a <c>For Each</c>'s
        /// frame slot as the distance it branches by.
        /// </summary>
        public List<(uint Rva, int Advanced)> OperandReads { get; set; } = [];

        /// <summary>
        /// Whether the operand was read only in code this handler handed off to, never in its own.
        /// </summary>
        public bool OperandReadOnlyInSharedCode { get; set; }

        /// <summary>The first read of anything after the opcode, of any width, or null when there is none.</summary>
        public uint? OperandReadRva { get; set; }

        public bool Counted { get; set; }

        public int Unit { get; set; }
    }

    private static HandlerWalk Walk(
        CodeDecoder decoder,
        PeImage pe,
        byte[] bytes,
        Dictionary<uint, string> exports,
        uint handlerRva,
        IReadOnlySet<uint> entries,
        Dictionary<uint, RetShape> cleanups)
    {
        var found = new HandlerWalk();
        var calls = found.Calls;
        var internalTargets = found.InternalTargets;

        // The instructions, and what the instruction pointer has been moved by on the way to each of
        // them. The consumed count travels with the path because two paths through one handler can
        // consume different amounts, and adding them up afterwards would be adding up two
        // instructions that were never executed one after the other.
        var work = new Stack<Path>();
        var visited = new HashSet<uint>();

        // What the handler reads through the instruction pointer, and where it hands control back to
        // the interpreter. Both are recorded with the path they were read on.
        var reads = new List<(int Step, int Hops, uint Rva, int Displacement, int Bytes, int Consumed)>();
        var dispatches = new List<(int Step, int Hops, uint JumpRva, uint Table, int Consumed, bool PointerMovedOther)>();
        var exits = new List<(int Consumed, int Hops, int Step, bool PointerMovedOther)>();
        var moves = new List<(int Step, int Hops, uint Rva, int Bytes, int Consumed)>();

        // The stack effect of each way out of this handler, and the addresses outside it this handler
        // branches to. Both are judged after every handler of the runtime has been walked.
        var nets = new List<(int? Slots, int Hops, bool Raises, string? Why)>();

        // The lists the result carries: taken from it rather than made beside it, so that what is
        // filled in here and what is read there cannot come apart.
        var externalJumps = found.ExternalJumps;
        var raiseJumps = found.RaiseJumps;
        var noReturnCalls = found.NoReturnCalls;

        work.Push(new Path(handlerRva, 0, 0, null, false, false, 0, true, null, null, null, false, false));

        int instructions = 0;
        int internalCalls = 0;
        int indirect = 0;
        int step = 0;

        // Which step reached each address, which is what makes a branch backwards identifiable as a
        // loop rather than as another path through the same code.
        var stepAt = new Dictionary<uint, int>();

        // Whether the handler loops over operands: how far each pass moves the instruction pointer.
        bool counted = false;
        int unit = 0;

        while (work.Count > 0 && instructions < MaxInstructionsPerHandler)
        {
            var path = work.Pop();
            (uint rva, int consumed, int hops, IReadOnlyDictionary<string, uint>? registers, bool branched, bool pointerMovedOther) =
                (path.Rva, path.Consumed, path.Hops, path.Registers, path.Branched, path.PointerMovedOther);
            if (!visited.Add(rva))
            {
                continue;
            }

            var insn = decoder.DecodeOne(rva);
            if (insn is null || insn.Length == 0)
            {
                continue;
            }

            instructions++;
            step++;
            stepAt.TryAdd(rva, step);
            uint next = rva + (uint)insn.Length;

            // What this instruction does to the operand stack, which in this interpreter *is* the
            // machine stack: a `push` moves a value onto it, a `pop` takes one off, and a call takes
            // its arguments off through the callee's own `ret N` — which is why a handler that looks
            // like `call __vbaStrCat; push eax` has an effect of minus one and not of plus one. Where
            // the amount depends on something the walk cannot see (a call through a pointer, a stack
            // pointer restored from memory), the path knows no longer.
            StackChange change = StackDelta(insn, decoder, cleanups);
            if (change.NoReturn != 0)
            {
                // A call that never returns ends the path where it stands: the interpreter does not get
                // its next opcode from here, which is why the path contributes no stack effect. What
                // follows is still read — the row's sizes and the calls it names are evidence about the
                // handler, and those do not stop being true because this path left.
                noReturnCalls.Add(change.NoReturn);
                path = path with { Left = true };
            }

            if (!change.Known)
            {
                path = path with { SlotsKnown = false, Unknown = path.Unknown ?? change.Why };
            }
            else if (path.SlotsKnown)
            {
                path = path with { Slots = path.Slots + change.Slots };
            }

            // A jump to an address the walk has not seen this handler use, taken right after an
            // immediate was loaded, is how the interpreter is told to raise: `mov eax, 9C68h` then
            // `jmp 0x108D50`, the one place in the runtime that raises. The target is recorded here
            // and judged after every handler has been walked — one handler raising is a handler, many
            // raising at the same address is the runtime's error path.
            if (insn.IsJump && insn.DirectTargetRva is uint raiseTarget && path.Immediate is not null
                && (long)raiseTarget - handlerRva is < -(long)BranchReach or > (long)BranchReach)
            {
                // The handler's own code loads an error code and leaves: the runtime's error block is
                // where it goes, and that block is what says which function raises — the register the
                // code was loaded into is the one it pushes.
                externalJumps.Add(raiseTarget);
                raiseJumps.Add((raiseTarget, path.ImmediateOwner ?? string.Empty));
                path = path with { Raises = true };
            }
            else if (insn.IsJump && insn.DirectTargetRva is uint jumped
                && (long)jumped - handlerRva is < -(long)BranchReach or > (long)BranchReach)
            {
                // A branch out of the handler that is not the dispatch: a shared path the interpreter
                // owns. Which of them raise is settled after the walk; this is the list to settle.
                externalJumps.Add(jumped);
            }

            if (insn.Immediate is long loaded && insn.WrittenRegisters.Count == 1)
            {
                path = path with { Immediate = loaded, ImmediateOwner = insn.WrittenRegisters[0] };
            }
            else if (!insn.IsNop)
            {
                path = path with { Immediate = null, ImmediateOwner = null };
            }

            if (IsDispatchTail(bytes, insn, out uint dispatchTable))
            {
                // The instruction names the table by address; everything here is an offset. Reading a
                // jump and comparing it with a table found by its offset is how the pairing between a
                // lead byte and its table was missed for every lead byte at once.
                dispatchTable -= (uint)pe.ImageBase;
                // The handler is done: this is where it hands the next opcode back to the interpreter.
                // The jump goes to a handler for a *different* opcode, so it is not followed.
                dispatches.Add((step, hops, rva, dispatchTable, consumed, pointerMovedOther));
                if (!path.Left)
                {
                    nets.Add((path.SlotsKnown ? path.Slots : null, hops, path.Raises, path.Unknown));
                }
                continue;
            }

            // Where a branch goes, read from the instruction or from the register it was loaded into
            // a few instructions earlier. Resolving it first is what tells a jump through a register
            // that has a destination from one that leaves: <c>jmp edi</c> after
            // <c>mov edi, 66109E69h</c> is a hand-off and is followed, and one through a register the
            // walk did not see loaded is the end of this instruction's code.
            uint? jumpTarget = null;
            if (IsBranch(insn))
            {
                jumpTarget = insn.DirectTargetRva
                    ?? (insn.IsConditionalJump ? null : ResolveRegisterJump(insn, registers));
            }

            if (insn.IsReturn || (IsBranch(insn) && jumpTarget is null))
            {
                // An opcode whose handler leaves for its caller — the end of a procedure, which the
                // interpreter finishes by restoring its frame and jumping to the address it saved —
                // ends the instruction as surely as a dispatch does. There is nothing after it here:
                // the next byte belongs to whoever called this, not to the next instruction.
                exits.Add((consumed, hops, step, pointerMovedOther));
                nets.Add((path.SlotsKnown ? path.Slots : null, hops, path.Raises, path.Unknown));
                continue;
            }

            if (insn.IsCall)
            {
                // A call is to an address, and a handler reaches that address directly, through a
                // stub, or through a register it loaded a moment ago — `mov ebx, 66111940h` then
                // `call ebx` is how the interpreter calls a helper it may replace at run time, and
                // the address is right there in the code. Only a call through something the walk did
                // not set (a pointer in memory, a parameter) is left as indirect.
                uint? target = insn.DirectTargetRva ?? ResolveRegisterJump(insn, registers);
                if (target is uint address)
                {
                    string? name = ResolveTarget(bytes, pe, exports, address);
                    if (name is null)
                    {
                        // Not an export, and not a stub of one: a helper inside the runtime. What it
                        // reaches is read afterwards, because a helper is often one line of plumbing
                        // around the export that does the work.
                        internalCalls++;
                        internalTargets.Add(address);
                    }
                    else
                    {
                        calls.Add(name);
                    }
                }
                else
                {
                    indirect++;
                }
            }

            bool advanced = Advances(insn, bytes, out int advancedBy);
            int moved = consumed + (advanced ? advancedBy : 0);

            // An instruction that writes the instruction pointer *without* the walk being able to say
            // by how much — `sub esi, ecx`, where the count came out of a call — moves it somewhere this
            // walk cannot follow. Everything measured after that is measured from a pointer that is not
            // where the walk thinks it is, so a length taken from it would be taken from the wrong
            // place; what the handler read is still readable, and that is what the operand is.
            if (!advanced && insn.WrittenRegisters.Any(r => string.Equals(r, "ESI", StringComparison.OrdinalIgnoreCase)))
            {
                pointerMovedOther = true;
                path = path with { PointerMovedOther = true };
            }

            if (advanced)
            {
                moves.Add((step, hops, rva, advancedBy, consumed));
            }

            foreach (var memory in insn.MemoryRefs)
            {
                if (memory.Read && string.Equals(memory.BaseRegister, "ESI", StringComparison.OrdinalIgnoreCase)
                    && memory.IndexRegister is null && memory.Size > 0)
                {
                    reads.Add((step, hops, rva, unchecked((int)(uint)memory.Address), memory.Size, consumed));
                }
            }

            // A register this instruction loaded with an address in the image travels with the path,
            // for a jump through it further on. Copy-on-write: the paths that do not set one share the
            // map they were given.
            if (insn.Immediate is long value && insn.WrittenRegisters.Count == 1 && IsCodeAddress(pe, value))
            {
                var updated = registers is null
                    ? new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, uint>(registers, StringComparer.OrdinalIgnoreCase);
                updated[insn.WrittenRegisters[0]] = (uint)(value - (long)pe.ImageBase);
                registers = updated;
                path = path with { Registers = updated };
            }

            // A code address written into memory — `mov dword ptr [ebp-6Ch], 6610D135h` — is a
            // continuation: the shared block this handler jumps to next reaches it by loading that
            // slot and jumping through it, which is how the interpreter does a call and return inside
            // itself. The walk cannot follow a jump through a slot, so it follows the store: the code
            // at the stored address is part of this handler's path, and what it reads through esi is
            // this instruction's operand. `AryInRecLdPr` and its neighbours are measured this way, and
            // without it they measure the one byte they read before handing off.
            if (insn.Immediate is long stored && IsCodeAddress(pe, stored) && insn.MemoryRefs.Any(m => m.Write))
            {
                work.Push(path with { Rva = (uint)(stored - (long)pe.ImageBase), Branched = true });
            }

            // Straight on is part of the handler while it is still in the handler's neighbourhood, or
            // inside the code the handler handed off to. Without this the walk follows a
            // falling-through run of instructions out of the handler and into whatever was assembled
            // after it, which in this runtime is another handler's code.
            bool nextInWindow = (long)next - handlerRva is > -(long)HandlerReach and < (long)HandlerReach;

            if (IsBranch(insn))
            {
                // Backwards, to code this walk has already been through: a loop. What it consumes in a
                // pass is the handler looping over its own operands — the length of such an
                // instruction is the operand word's, so one number cannot describe it.
                if (jumpTarget is uint loopHead && loopHead < rva && stepAt.TryGetValue(loopHead, out int loopStep))
                {
                    var pass = moves
                        .Where(m => m.Step > loopStep && m.Step <= step && m.Hops == 0 && m.Bytes > 0)
                        .OrderByDescending(m => m.Rva)
                        .FirstOrDefault();

                    if (pass.Bytes > 0 && pass.Rva >= loopHead && pass.Rva < rva)
                    {
                        counted = true;
                        unit = pass.Bytes;
                    }
                }

                if (jumpTarget is uint target)
                {
                    // How far a branch may go and still be this handler's own code. The handlers in
                    // this interpreter are short runs and their branches are shorter still; what lies
                    // further out is the shared code every handler reaches — the overflow paths, the
                    // error paths, the interpreter's exit — and what it does there is not what this
                    // instruction consumed. An unconditional jump out is the hand-off, and is followed
                    // a little way; a conditional one out is the shared code, and is not.
                    bool outside = (long)target - handlerRva is < -(long)BranchReach or > (long)BranchReach;
                    bool handOff = outside && !insn.IsConditionalJump;

                    // Landing on another opcode's entry is usually not this handler's code: a
                    // fall-through after a call that does not return reaches the handler assembled next
                    // to it, and reading that handler's operands as this instruction's is how the API
                    // call came out at 13 bytes rather than 5. An *unconditional jump* straight there,
                    // taken before the handler has read or consumed anything, is the opposite: the two
                    // instructions share one body, the jump being the only difference between them —
                    // `FStVarNoPop` is `FStVarCopy` behind a `push`, and 0x23 is 0x31 behind a prefix.
                    // The entry's own measurement is then this instruction's operand, so the jump is
                    // followed, under the same tail budget every other hand-off gets. The "before it has
                    // read anything" is what keeps an error path out: a handler that has already read
                    // its operands and *then* jumps into shared code is not sharing a body, and the
                    // operands of whatever it lands in are not its own.
                    bool sharedBody = !insn.IsConditionalJump && entries.Contains(target) && target != handlerRva
                        && consumed == 0 && !reads.Any(r => r.Hops <= hops && r.Step < step);
                    bool ownCode = !entries.Contains(target) || target == handlerRva;

                    if ((ownCode || sharedBody) && (!outside || (handOff && hops + 1 <= MaxTailHops)))
                    {
                        work.Push(path with { Rva = target, Hops = outside ? hops + 1 : hops, Branched = true });
                    }

                }

                // An unconditional jump goes only where it goes; a conditional one also falls through.
                if (!insn.IsConditionalJump)
                {
                    continue;
                }
            }

            // A fall-through that lands on another opcode's entry has left this instruction: what
            // follows is that opcode's code, and reading its operands as ours is how a length comes
            // out too long — the API call measured at 13 bytes rather than 5, because its error path
            // ends in a call that does not return and the next handler's operands were counted as
            // its own. The entry addresses come from the tables, so this is the file drawing the line
            // rather than a distance chosen here.
            //
            // Only after a branch, though. Two opcodes can share a body: opcode 0x23 is a two-byte
            // prefix in front of the code that is opcode 0x31's entry, and a walk that refused the
            // straight-line fall-through there would lose the operand that makes both of them three
            // bytes long. Straight on from the entry is this instruction's code by definition; it is
            // the code a *branch* arrives at that has to be checked against the entries.
            // A call's fall-through is a landing as well: what follows a call is where the callee
            // comes back to, not the straight line of this handler's operands. Where a call does not
            // return — the error raises, which push their arguments and jump into the runtime rather
            // than returning — the assembler has put the next opcode's handler directly after it, and
            // the walk that steps over the call reads that handler's operands as this instruction's.
            // That is the API call measured at 13 bytes rather than 5. The prefix case above is not
            // touched by this: there the fall-through is from the prefix's own instruction.
            bool anotherOpcodeStartsHere = (branched || insn.IsCall) && entries.Contains(next) && next != handlerRva;

            if ((nextInWindow || hops > 0) && !anotherOpcodeStartsHere)
            {
                work.Push(path with { Rva = next, Consumed = moved });
            }
        }

        // The tables this handler dispatches through *itself*, on a path that stayed in its own code.
        // A dispatch the walk reached by handing off is somebody else's jump: the walk follows those to
        // read what a handler consumes, and a table named there says nothing about this opcode.
        // A path that raises never returns to the interpreter, so what it left on the stack is not this
        // instruction's effect: only the paths that dispatch or leave are counted, which is the same
        // rule the lengths use for the paths that only hand off.
        var effects = nets.Where(n => !n.Raises).Select(n => (n.Slots, n.Hops, n.Why)).ToList();
        // Only the paths that load an error code on their way out raise. Jumping somewhere outside the
        // handler is not in itself raising: a shared body is outside the handler too.
        bool raises = nets.Any(n => n.Raises);

        found.Sizes = Sizes(handlerRva, reads, dispatches, exits, moves);
        found.StackEffects = effects;
        found.Raises = raises;
        found.Instructions = instructions;
        found.InternalCalls = internalCalls;
        found.IndirectCalls = indirect;
        found.DispatchTables = dispatches.Where(d => d.Hops == 0).Select(d => d.Table).Distinct().ToList();
        // The distinct copies are made here because the aliases above *are* these lists: adding a
        // de-duplicated version of a list into itself is a collection modified while it is being read.
        found.RaiseJumps = raiseJumps.Distinct().ToList();
        found.NoReturnCalls = noReturnCalls.Distinct().ToList();
        found.Counted = counted;
        found.Unit = unit;
        // Reads of the operand's own word, wherever the handler reads it. A handler that hands off to
        // the interpreter's shared code to read its second word is reading its own operand there — the
        // `jmp` before the read is how the assembler shares one tail between several opcodes — so the
        // hand-off hops are allowed for, up to the same budget the walk follows.
        // Whether *every* read of the operand was found past a hand-off. A handler that is a stub —
        // `xor ebx,ebx; jmp <shared block>` — has no operand-reading code of its own: the bytes are
        // read by the shared code it leaves for, and saying "the handler reads them" would be a claim
        // about code that is not this handler's.
        found.OperandReadOnlyInSharedCode = reads.Count > 0 && reads.All(r => r.Hops > 0);

        // The first place the handler read anything after the opcode, whatever the width. The operand's
        // *kind* is decided from the word-sized reads — a branch and a frame slot are words — but a row
        // whose operand is a byte or a dword is still read by its handler, and a basis that said "no read
        // was found" for one of those was the reader reporting its own filter instead of the code.
        found.OperandReadRva = reads
            .Where(r => r.Displacement >= 0)
            .OrderBy(r => r.Step)
            .Select(r => (uint?)r.Rva)
            .FirstOrDefault();
        found.OperandReads = reads
            .Where(r => r.Hops <= MaxTailHops && r.Bytes == 2 && r.Displacement >= 0)
            .Select(r => (r.Rva, r.Consumed))
            .Distinct()
            .ToList();
        return found;
    }

    /// <summary>
    /// How long the instruction is, measured from the handler's code, one number per way out of it.
    ///
    /// The interpreter reads the next opcode from <c>[esi + K]</c> and moves <c>esi</c> past the
    /// operand and that byte: what the instruction consumed, going into the dispatch, is where the
    /// fetch read it plus the displacement, plus the opcode byte itself. A handler that reads its
    /// operand into <c>esi</c> instead — a branch, which goes wherever the operand says — moves the
    /// pointer to somewhere that is not the next instruction at all, so for those the size is the
    /// operand's own width, which the extent of what was read says.
    ///
    /// Where several paths through a handler dispatch, they are measured separately and reported
    /// separately: an instruction whose length depends on which way it goes has more than one length,
    /// and one number would be a choice between them that the file does not make.
    /// </summary>
    private static List<int> Sizes(uint handlerRva,
        List<(int Step, int Hops, uint Rva, int Displacement, int Bytes, int Consumed)> reads,
        List<(int Step, int Hops, uint JumpRva, uint Table, int Consumed, bool PointerMovedOther)> dispatches,
        List<(int Consumed, int Hops, int Step, bool PointerMovedOther)> exits,
        List<(int Step, int Hops, uint Rva, int Bytes, int Consumed)> moves)
    {
        var sizes = new List<int>();

        foreach (var (jumpStep, jumpHops, _, _, pathConsumed, pointerMovedOther) in dispatches)
        {
            // The fetch is the last read of [esi] in the sequence leading to the dispatch jump: the
            // interpreter reads the next opcode from it and moves esi past it in the instruction or
            // two after.
            var fetch = reads
                .Where(r => r.Step < jumpStep && jumpStep - r.Step <= DispatchSequenceSpan)
                .OrderByDescending(r => r.Step)
                .FirstOrDefault();

            if (fetch.Bytes == 0)
            {
                // A dispatch with no fetch read in front of it: a jump table reached without reading
                // the opcode through esi. What the path consumed and the byte it is dispatching on is
                // all there is to say — unless nothing was consumed either, and the path got here by
                // handing off, in which case it is the shared code's own dispatch and not this
                // instruction's length.
                if (jumpHops > 0 && pathConsumed == 0)
                {
                    continue;
                }

                sizes.Add(pathConsumed + 1);
                continue;
            }

            // What was read along this path that the fetch did not account for: a branch reads its
            // target through esi and goes there, so no move of esi contains its operand's width.
            int extent = reads
                .Where(r => r.Step < fetch.Step && r.Hops <= fetch.Hops)
                .Select(r => r.Displacement + r.Bytes)
                .DefaultIfEmpty(0)
                .Max();

            // A hand-off — a path that left the handler to reach this dispatcher — is measured from
            // what the shared code read, not from where its fetch reads: the shared blocks of this
            // interpreter begin by reading the operand at [esi], and then read the next opcode at
            // [esi + n], and using that displacement would count the operand twice. The 25 variable
            // operations of the first lead table — `ImpVar`, `EqvVar`, `ModVar` and the comparisons —
            // are exactly this shape: a two-byte operand read at [esi], then a fetch at [esi+2].
            // Matching the fetch against the dispatch instead, as this did, discards them all.
            if (jumpHops > 0 && fetch.Hops != jumpHops)
            {
                continue;
            }

            // A path that handed off, read nothing on the way there, and dispatches where the fetch is
            // at [esi] measured one byte and the byte is not there: the handler's own code did nothing
            // but jump. That is not a length for this instruction — `AryInRecLdPr` reads its operand in
            // the code its shared block returns to, and the path that stops at the jump would answer 1
            // where the file answers 5. Where the handler really is a single byte, its own dispatch is
            // at hops 0 and this does not touch it; where the hand-off is all there is, the fallback
            // below still answers 1.
            if (jumpHops > 0 && extent == 0 && fetch.Displacement == 0)
            {
                continue;
            }

            // A dispatch is measured by where its fetch reads, whatever the path did to esi on the way:
            // the position the walk has followed *is* where the next instruction begins, and the
            // collection loops — which save esi across a call and restore it — depend on that, because
            // their second operand is read at [esi] again rather than at a displacement.
            sizes.Add(Math.Max(fetch.Consumed + fetch.Displacement + 1, extent + 1));
        }

        foreach (var (pathConsumed, exitHops, exitStep, pointerMovedOther) in exits)
        {
            // The same scrutiny the dispatches get. An exit the walk reached by handing off is this
            // instruction's end only if the handed-off code was doing something for it: it moved the
            // pointer, or it read the operand there. The shared code also leaves for reasons of its
            // own — an error path, a cleanup — with nothing consumed and nothing read where it went,
            // and the byte it would be measured at is not this instruction's operand. `AryInRecLdPr`
            // is the case: its own code stores a continuation and jumps, the shared block it jumps to
            // reads the operands and *then* dispatches, and one of its branches leaves through the
            // interpreter's exit — a path that would report the instruction at one byte.
            if (exitHops > 0 && pathConsumed == 0 && !reads.Any(r => r.Hops > 0 && r.Step < exitStep))
            {
                continue;
            }

            // Out by the interpreter's exit: the operand was what esi was moved by on the way — unless
            // something on the way moved the pointer by an amount this walk cannot follow, in which
            // case what was moved by is not a length at all and what the handler read is the operand.
            if (pointerMovedOther)
            {
                int readTo = reads
                    .Where(r => r.Step < exitStep && r.Hops == 0)
                    .Select(r => r.Displacement + r.Bytes)
                    .DefaultIfEmpty(0)
                    .Max();
                sizes.Add(readTo + 1);
                continue;
            }

            sizes.Add(1 + pathConsumed);
        }

        if (sizes.Count == 0)
        {
            // Neither a dispatch nor an exit was reached. What the handler's own code did is still
            // measured, and that is the length: the pointer was moved by what was moved and read by
            // what was read, and the rest of the instruction is not in this handler at all — which is
            // the one-byte case for a handler that reads nothing before it hands off, and the width of
            // the operand it does read otherwise.
            // Only what the handler's own code did: a path that handed off reaches code written for
            // something else, and an operand read there belongs to that code's instruction, not this
            // one. That is the same scrutiny the dispatches above are given.
            int moved = moves.Where(m => m.Hops == 0).Select(m => m.Consumed + m.Bytes).DefaultIfEmpty(0).Max();
            int extent = reads.Where(r => r.Hops == 0).Select(r => r.Displacement + r.Bytes).DefaultIfEmpty(0).Max();
            sizes.Add(1 + Math.Max(moved, extent));
        }

        // A length is at least the opcode byte, and this walk measures from an esi that points past
        // it. A path that reads only *before* esi — a handler that reads the byte that selected its
        // table, as the completion-callback handlers of the sixth table do — has no operand extent to
        // report, and zero is not a length any instruction can have. The floor says what such a
        // measurement amounts to: the sub-opcode byte, which is what is known.
        return sizes.Select(s => Math.Max(1, s)).Distinct().Order().ToList();
    }

    /// <summary>
    /// Whether this instruction is the handler's dispatch of the next opcode — the same seven bytes
    /// every handler ends with. The table it jumps through is not checked against one particular
    /// table here: any of them means this handler has finished. The table comes out as the image
    /// writes it, which is an address: <see cref="Walk"/> turns it into an offset.
    /// </summary>
    private static bool IsDispatchTail(byte[] bytes, DecodedInsn insn, out uint tableRva)
    {
        tableRva = 0;
        if ((long)insn.Rva + DispatchJumpLength > bytes.Length)
        {
            return false;
        }

        var span = bytes.AsSpan((int)insn.Rva, DispatchJumpLength);
        if (span[0] != 0xFF || span[1] != 0x24 || span[2] != 0x85)
        {
            return false;
        }

        tableRva = BinaryPrimitives.ReadUInt32LittleEndian(span[3..]);
        return true;
    }

    /// <summary>
    /// Whether this instruction advances the instruction pointer by a constant, and by how much. The
    /// ways the interpreter does that are <c>inc esi</c> and <c>add esi, imm</c>; anything else is a
    /// move whose distance is not in the instruction, and it is not counted.
    /// </summary>
    /// <summary>
    /// What the bytes after an opcode are, read from the hander's own instructions.
    ///
    /// Four kinds, and each one is a shape in the code rather than a name in a table:
    ///
    /// <c>none</c> — the instruction has no operand bytes at all. The measured length is the opcode
    /// byte on its own.
    ///
    /// <c>target</c> — the word is where control goes. The interpreter's unconditional branch does not
    /// step over its operand: it reads the word, adds the frame slot that holds the instruction
    /// stream's base, and dispatches at what that makes —
    /// <c>movzx esi,word[esi]</c>, <c>add esi,[ebp-58h]</c>, then the ordinary fetch. Nothing else in
    /// this runtime hands the instruction pointer a value it read, which is why this shape, and not a
    /// guess about which opcodes look like branches, is what tells a target from a frame slot. The
    /// conditional branches are the same shape one jump away: their handler tests the condition and
    /// jumps to that tail without touching esi, so the word the tail reads is theirs. A jump to
    /// another opcode's entry is therefore part of the test — and only that, because a jump anywhere
    /// else is the interpreter's shared code and what it reads there is not this instruction's
    /// operand.
    ///
    /// <c>slot</c> — the word is a signed displacement into the frame: the handler loads it and then
    /// addresses memory with the frame base and what it loaded, <c>[ebp+eax]</c> or <c>[eax+ebp]</c>.
    ///
    /// <c>data</c> — everything else: the word is a value the instruction works on, a table index, an
    /// object offset, or one field of an operand that has several.
    ///
    /// The check that this is right is not internal. Over the 42 p-code programs of the corpus, every
    /// 16-bit operand of an instruction the table calls <c>target</c> lands exactly on an instruction
    /// start when added to the procedure's code address, and the same test over the opcodes this
    /// leaves as <c>data</c> lands at the rate of a wrong guess.
    /// </summary>
    private static (string Kind, int At, int SlotAt, string Basis) ClassifyOperand(
        CodeDecoder decoder,
        byte[] bytes,
        uint handlerRva,
        int opcodeBytes,
        IReadOnlyList<int> sizes,
        int unit,
        bool counted,
        IReadOnlyList<(uint Rva, int Advanced)> operandReads,
        bool readInSharedCodeOnly,
        uint? anyReadRva,
        IReadOnlySet<uint> entries)
    {
        int length = sizes.Count == 1 ? sizes[0] : counted ? unit : 0;
        int operandBytes = length > 0 ? length - 1 : -1;

        if (operandBytes == 0)
        {
            return ("none", 0, -1, "the instruction is the opcode byte alone: its handler reads nothing after it");
        }

        (int At, uint Rva)? slot = null;
        foreach (var (advanced, insns) in Runs(decoder, bytes, handlerRva, operandReads, operandBytes, entries))
        {
            if (BranchTargetAt(insns, bytes, operandBytes) is { } target && advanced + target.At < operandBytes)
            {
                int where = advanced + target.At;

                // A branch can also name a frame slot: the For/Next family takes its counter's slot
                // first and the distance it branches by second, and the two words are different
                // fields. A slot on the *same* word as the branch would be a contradiction, and one
                // reaching past the operand is not this instruction's.
                var inSameRun = slot
                    ?? (FrameSlotAt(insns) is { } here ? (At: advanced + here.At, Rva: here.Rva) : null);
                int beside = inSameRun is { } found && found.At != where && found.At + 2 <= operandBytes
                    ? found.At
                    : -1;
                return ("target", where, beside,
                    $"the handler computes the instruction stream's base plus the word at offset {where} of the "
                    + $"operand and goes there: {At(decoder, target.Rva)}"
                    + (beside >= 0
                        ? $"; the word at offset {beside} before it is the frame slot it works through"
                        : string.Empty));
            }

            if (slot is null && FrameSlotAt(insns) is { } inFrame)
            {
                slot = (advanced + inFrame.At, inFrame.Rva);
            }
        }

        if (slot is { } frameSlot)
        {
            return ("slot", frameSlot.At, frameSlot.At,
                $"the handler sign-extends the word at offset {frameSlot.At} of the operand and addresses memory "
                + $"from the frame base by what it loaded: {At(decoder, frameSlot.Rva)}");
        }

        if (operandBytes < 0)
        {
            return ("data", 0, -1,
                "the handler's own paths do not agree on one length, so its operand has no single size, let alone a kind");
        }

        // Where it was read, because a claim about an operand is a claim about an instruction: `data` is
        // the kind that says what the handler did *not* do with the bytes, and it used to be the one kind
        // that named no instruction, which made it the one kind a reader could not check.
        string read = At(decoder, operandReads.Count > 0 ? operandReads[0].Rva : anyReadRva ?? handlerRva);
        return readInSharedCodeOnly
            ? ("data", 0, -1,
                $"the handler's own code reads nothing after the opcode: the bytes are read as a value by the "
                + $"shared code it hands off to ({read}), and no path moves the instruction pointer by them")
            : ("data", 0, -1,
                $"the handler reads {operandBytes} byte(s) of operand as a value ({read}): an index, an offset, or a "
                + "field of an operand that has several, and no path moves the instruction pointer by any of them");
    }

    /// <summary>
    /// The runs of instructions that read one opcode's operand, each ending at the point the reading
    /// stops being about this operand.
    ///
    /// Three kinds of seed, because the code that reads an operand is reached three ways. The handler's
    /// own entry, for the handlers that read it straight away. The entry of another opcode the handler's
    /// code jumps to before reading anything, which is how the conditional branches reach the tail that
    /// reads their word. And each place the walk found the operand being read, which is where a handler
    /// that hands off to a shared block — <c>FLdPr</c> hands off one byte into another handler's code,
    /// and the block does the work — actually reads it.
    ///
    /// A run ends at a dispatch, a return, or a jump; a jump to another opcode's entry in the handler's
    /// own neighbourhood is followed, because that is what two opcodes sharing one body look like, and a
    /// jump anywhere else is the interpreter's shared code, whose reads are not this instruction's
    /// operand.
    /// </summary>
    private static List<(int Advanced, List<DecodedInsn> Insns)> Runs(
        CodeDecoder decoder, byte[] bytes, uint handlerRva, IReadOnlyList<(uint Rva, int Advanced)> operandReads,
        int operandBytes, IReadOnlySet<uint> entries)
    {
        const int MaxInsns = 12;
        const int MaxRuns = 8;

        var runs = new List<(int Advanced, List<DecodedInsn> Insns)>();
        var work = new Queue<(uint Start, int Advanced)>();
        var seen = new HashSet<uint>();

        // The handler's own entry, and the pointer at the start of its operand.
        work.Enqueue((handlerRva, 0));
        foreach (var (rva, advanced) in operandReads)
        {
            // Where the handler read the operand, if that is a place inside the operand: a read further
            // in is of whatever lies past this instruction, not of its operand.
            if (advanced < operandBytes)
            {
                work.Enqueue((rva, advanced));
            }
        }

        while (work.Count > 0 && runs.Count < MaxRuns)
        {
            var (start, advanced) = work.Dequeue();
            if (!seen.Add(start))
            {
                continue;
            }

            var run = new List<DecodedInsn>();
            uint rva = start;
            for (int i = 0; i < MaxInsns; i++)
            {
                var insn = decoder.DecodeOne(rva);
                if (insn is null || insn.Length == 0 || (long)rva + insn.Length > bytes.Length)
                {
                    break;
                }

                run.Add(insn);

                if (insn.IsReturn || IsDispatchTail(bytes, insn, out _))
                {
                    break;
                }

                if (insn.IsJump)
                {
                    if (insn.DirectTargetRva is uint target && target != start && entries.Contains(target)
                        && (long)target - handlerRva is > -(long)BranchReach and < (long)BranchReach)
                    {
                        // A jump to another opcode's entry is the same body read further along: the
                        // pointer is wherever the code that jumped left it, which for the conditional
                        // branches is still the start of the operand.
                        work.Enqueue((target, advanced));
                    }

                    if (!insn.IsConditionalJump)
                    {
                        break;
                    }
                }

                rva += (uint)insn.Length;

                // The fall-through of a call is where the callee comes back to, and where a call does not
                // return the assembler has put the next opcode's handler directly after it. A run that
                // stepped over that call would read the next opcode's operands as this one's.
                if (insn.IsCall && rva != start && entries.Contains(rva))
                {
                    break;
                }
            }

            if (run.Count > 0)
            {
                runs.Add((advanced, run));
            }
        }

        return runs;
    }

    /// <summary>
    /// Where in the operand the word that says where control goes is, or nothing when no word does.
    ///
    /// The arithmetic is the same every time — the frame slot holding the instruction stream's base
    /// plus the word — and the compiler emits it three ways round:
    ///
    /// <c>movzx esi,word[esi]</c> then <c>add esi,[ebp-58h]</c>: the word into the instruction pointer,
    /// the base added to it. The unconditional branch, and the conditional branches, which jump to its
    /// tail without touching the pointer.
    ///
    /// <c>mov esi,[ebp-58h]</c> then <c>add esi,eax</c>, with <c>eax</c> read from the operand: the base
    /// into the pointer, the word added to it. What a <c>Next</c> does once its counter is done.
    ///
    /// <c>add eax,[ebp-58h]</c> then <c>xchg esi,eax</c>: the base added to the register holding the
    /// word, and the sum swapped into the pointer — with the old pointer, which is the fall-through,
    /// kept in a register for the branch that is not taken. What <c>Next</c> with a step does.
    ///
    /// The offset is returned rather than a yes or no: an instruction can have two fields, and a
    /// <c>Next</c> has its counter's frame slot first and the distance second. A listing that resolved
    /// the first two bytes of one of those would point at the counter.
    /// </summary>
    private static (int At, uint Rva)? BranchTargetAt(List<DecodedInsn> run, byte[] bytes, int operandBytes)
    {
        for (int k = 0; k < run.Count; k++)
        {
            var insn = run[k];

            // The word's register, then the base added to it, then the instruction pointer set from
            // that register — by being it, or by the swap that keeps the fall-through.
            if (SingleWrittenRegister(insn) is string into && Reads(insn, into)
                && (FrameRead(insn) is not null || MovesPointerFromFrame(insn))
                && OperandLoad(run, k, into, operandBytes) is int word
                && (IsRegister(into, "ESI") || LoadsPointerFrom(run, k, into)))
            {
                return (word, insn.Rva);
            }

            // The base into the instruction pointer, then the register holding the word added to it.
            if (k + 1 < run.Count && insn.Mnemonic == Mnemonic.Mov && MovesPointerFromFrame(insn)
                && WritesPointer(run[k + 1]) && !Advances(run[k + 1], bytes, out _))
            {
                foreach (string added in run[k + 1].ReadRegisters.Where(r => !IsRegister(r, "ESI")))
                {
                    if (OperandLoad(run, k, added, operandBytes) is int from)
                    {
                        return (from, run[k + 1].Rva);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// How an instruction is written, for a sentence about it: <c>add esi,eax at 0x1095F7</c>. The
    /// decoder is asked for the text, and text is what makes a basis a fact somebody can check rather
    /// than a claim about one.
    /// </summary>
    private static string At(CodeDecoder decoder, uint rva)
        => decoder.DecodeOne(rva) is { } insn
            ? $"`{insn.Text}` at 0x{rva:X}"
            : $"the instruction at 0x{rva:X}";

    /// <summary>
    /// Where in the operand an instruction before <paramref name="before"/> read the word into
    /// <paramref name="register"/>: the offset of the two bytes it read through the instruction pointer.
    /// Nothing when no instruction before it wrote that register from the operand.
    /// </summary>
    private static int? OperandLoad(List<DecodedInsn> run, int before, string register, int operandBytes)
    {
        for (int k = 0; k < before; k++)
        {
            if (IsRegister(SingleWrittenRegister(run[k]), register) && OperandRead(run[k], 2) is int at
                && at >= 0 && at < operandBytes)
            {
                return at;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether an instruction after <paramref name="after"/> sets the instruction pointer from
    /// <paramref name="register"/> — <c>xchg esi,eax</c> or <c>mov esi,eax</c>, which is how the sum the
    /// word and the base make gets into the pointer on a path that is not the straight fall-through.
    /// </summary>
    private static bool LoadsPointerFrom(List<DecodedInsn> run, int after, string register)
    {
        for (int k = after + 1; k < run.Count; k++)
        {
            if (WritesPointer(run[k]) && Reads(run[k], register))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The register an instruction writes, when it writes exactly one.</summary>
    private static string? SingleWrittenRegister(DecodedInsn insn)
        => insn.HasWrittenRegisters && insn.WrittenRegisters.Count == 1 ? insn.WrittenRegisters[0] : null;

    private static bool Reads(DecodedInsn insn, string register)
        => insn.ReadRegisters.Any(r => IsRegister(r, register));

    /// <summary>
    /// The memory the instruction reads at a fixed displacement from the frame base — <c>[ebp-58h]</c>,
    /// which is where this interpreter keeps the instruction stream's base. Nothing when it reads no
    /// such slot, or reads one with an index: <c>[ebp+eax]</c> is the frame addressed *by* the operand,
    /// which is a slot and not the base of anything.
    /// </summary>
    private static MemoryRef? FrameRead(DecodedInsn insn)
    {
        if (!insn.HasMemoryRefs)
        {
            return null;
        }

        foreach (var memory in insn.MemoryRefs)
        {
            if (memory.Read && IsRegister(memory.BaseRegister, "EBP") && memory.IndexRegister is null)
            {
                return memory;
            }
        }

        return null;
    }

    /// <summary>
    /// The displacement of a read of the operand through the instruction pointer, when the instruction
    /// reads exactly <paramref name="bytes"/> of it. Nothing when it reads nothing there, or reads a
    /// different amount: the width is part of the question, because a handler that reads one byte of the
    /// operand and then more of it is not reading one word.
    /// </summary>
    private static int? OperandRead(DecodedInsn insn, int bytes)
    {
        if (!insn.HasMemoryRefs)
        {
            return null;
        }

        foreach (var memory in insn.MemoryRefs)
        {
            if (memory.Read && memory.Size == bytes && IsRegister(memory.BaseRegister, "ESI")
                && memory.IndexRegister is null)
            {
                return (int)memory.Address;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the instruction sets the instruction pointer from the frame — <c>mov esi,[ebp-58h]</c>,
    /// the slot the interpreter keeps the instruction stream's base in. A `mov`, not an `add`: adding
    /// the base to whatever the pointer already held would be a distance from somewhere else.
    /// </summary>
    private static bool MovesPointerFromFrame(DecodedInsn insn)
        => WritesPointer(insn) && insn.HasMemoryRefs
            && insn.MemoryRefs.Any(m => m.Read && IsRegister(m.BaseRegister, "EBP") && m.IndexRegister is null
                && m.Size == 4);

    /// <summary>
    /// Whether a run treats the word as a frame slot: it loads the word sign-extended into a register and
    /// then, within the same run, addresses memory with the frame base and that register —
    /// <c>[ebp+eax]</c> or <c>[eax+ebp]</c>. Sign extension is part of the test because the frame's slots
    /// are either side of the frame pointer, so an unsigned read of one would be a different instruction.
    /// </summary>
    private static (int At, uint Rva)? FrameSlotAt(List<DecodedInsn> run)
    {
        if (run.Count == 0 || run[0].Mnemonic != Mnemonic.Movsx || run[0].WrittenRegisters.Count != 1
            || OperandRead(run[0], 2) is not int at)
        {
            return null;
        }

        string loaded = run[0].WrittenRegisters[0];

        // The scan runs over the run and stops when the register stops holding the word. A fixed window
        // was the first version of this and it was one instruction short of the truth twice: 0x66 loads
        // its counter's slot and only compares against it seven instructions later, and a window is a
        // number where the file has a structure. The structure is that the register holds the
        // sign-extended word, so what ends the scan is that register being written again.
        for (int i = 1; i < run.Count; i++)
        {
            // The frame addressed by what was loaded: `mov [edi+ebp],bl` reads the word's destination,
            // and `cmp [edi+ebp+4],ecx` reads the word of a 32-bit counter four bytes on. Either
            // register can be the base: `[ebp+eax]` and `[ebx+ebp]` are the same address written the
            // two ways round, and the compiler emits both.
            foreach (var memory in run[i].MemoryRefs)
            {
                bool baseIsFrame = IsRegister(memory.BaseRegister, "EBP") && IsRegister(memory.IndexRegister, loaded);
                bool indexIsFrame = IsRegister(memory.IndexRegister, "EBP") && IsRegister(memory.BaseRegister, loaded);
                if (baseIsFrame || indexIsFrame)
                {
                    return (at, run[i].Rva);
                }
            }

            // The frame address *computed* into the register rather than used as one: the For Each
            // handlers do `movsx eax,[esi]` / `add esi,2` / `add eax,ebp` / `push eax` and call the
            // runtime with a pointer to the variable the instruction works on. `add eax,ebp` is ebp
            // plus the word, which is the same address the memory forms spell out.
            if (run[i].Mnemonic == Mnemonic.Add && !run[i].HasMemoryRefs
                && run[i].WrittenRegisters.Count == 1
                && IsRegister(run[i].WrittenRegisters[0], loaded)
                && run[i].ReadRegisters.Any(r => string.Equals(r, loaded, StringComparison.OrdinalIgnoreCase))
                && run[i].ReadRegisters.Any(r => IsRegister(r, "EBP")))
            {
                return (at, run[i].Rva);
            }

            // Past this point the instruction did not use the word, and if it wrote the register the
            // word was in, the register holds something else and the scan is over. This is checked
            // *after* the two forms above because both of them write the register they use: the load
            // `mov eax,[eax+ebp]` writes eax and `add eax,ebp` writes eax, and reading the break first
            // is what turned every one of these into a `data` row the first time this was written.
            if (run[i].WrittenRegisters.Any(r => string.Equals(r, loaded, StringComparison.OrdinalIgnoreCase)))
            {
                break;
            }
        }

        return null;
    }

    private static bool WritesPointer(DecodedInsn insn)
        => insn.HasWrittenRegisters && insn.WrittenRegisters.Any(r => IsRegister(r, "ESI"));

    private static bool IsRegister(string? name, string register)
        => name is not null && string.Equals(name, register, StringComparison.OrdinalIgnoreCase);

    private static bool Advances(DecodedInsn insn, byte[] bytes, out int size)
    {
        size = 0;
        if ((long)insn.Rva + insn.Length > bytes.Length)
        {
            return false;
        }

        var code = bytes.AsSpan((int)insn.Rva, insn.Length);
        switch (code.Length)
        {
            case 1 when code[0] == 0x46:                       // inc esi
                size = 1;
                return true;
            case 3 when code[0] == 0x83 && code[1] == 0xC6:    // add esi, imm8
                size = code[2];
                return true;
            case 6 when code[0] == 0x81 && code[1] == 0xC6:    // add esi, imm32
                size = (int)BinaryPrimitives.ReadUInt32LittleEndian(code[2..]);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The name of what a call goes to: an export of the runtime, or — when the call lands on a thunk
    /// — the export the thunk jumps to. A target that is neither is the interpreter's own code, which
    /// has no name in the file and is counted rather than named.
    /// </summary>
    private static string? ResolveTarget(byte[] bytes, PeImage pe, Dictionary<uint, string> exports, uint target)
    {
        if (exports.TryGetValue(target, out string? direct))
        {
            return direct;
        }

        // A stub: jmp rel32 (E9) or jmp [absolute] (FF 25). Both are how one function's code hands
        // off to another's without the call being to it.
        int? offset = PeLoader.RvaToOffset(pe, target);
        if (offset is null || offset.Value + 6 > bytes.Length)
        {
            return null;
        }

        var span = bytes.AsSpan(offset.Value, 6);
        if (span[0] == 0xE9)
        {
            uint destination = target + 5 + (uint)BinaryPrimitives.ReadUInt32LittleEndian(span[1..]);
            return exports.TryGetValue(destination, out string? viaJump) ? viaJump : null;
        }

        if (span[0] == 0xFF && span[1] == 0x25)
        {
            // A jump through the import address table: the runtime calling out of itself. The import
            // describes that slot, so the name comes from there.
            uint pointer = BinaryPrimitives.ReadUInt32LittleEndian(span[2..]);
            if (pointer < pe.ImageBase)
            {
                return null;
            }

            uint iatRva = pointer - (uint)pe.ImageBase;
            var import = pe.Imports.FirstOrDefault(i => i.IatRva == iatRva);
            if (import is null)
            {
                return null;
            }

            return import.Name is null
                ? $"{import.Dll}!#{import.Ordinal}"
                : $"{import.Name} ({import.Dll})";
        }

        return null;
    }
}
