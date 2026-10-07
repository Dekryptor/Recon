namespace Recon.Analysis;

/// <summary>
/// The prologue vocabulary a toolchain profile may name in <c>[codegen] prologue_hints</c>.
///
/// A hint is a short phrase per instruction, separated by semicolons, that is turned into the bytes a
/// function compiled by that toolchain starts with. It is a prefix match, not a pattern: the analyser
/// only needs to recognise the first two or three bytes to know a boundary is plausible, and the
/// confidence of what it finds there is recorded as low no matter which hint matched.
///
/// The vocabulary is small on purpose and covers what compilers actually emit: the 32-bit x86 frame
/// pointer sequence, and on x86-64 the frame pointer sequence with its REX prefix, the callee-saved
/// register a leaf-most function pushes first, the stack adjustment, and <c>endbr64</c>, which every
/// function of a control-flow-enforced binary starts with. A hint the vocabulary does not know is
/// dropped rather than matched as text, so a typo cannot silently match everything.
/// </summary>
public static class ProloguePatterns
{
    /// <summary>The instruction names a profile may use, in both widths.</summary>
    public static readonly string[] KnownInstructions =
    [
        "push ebp", "mov ebp, esp", "push esp", "sub esp, imm",
        "push rbp", "mov rbp, rsp", "push rbx", "push r12", "sub rsp, imm", "endbr64",
    ];

    /// <summary>
    /// Turns one hint into the byte prefix it means. An empty list means the hint is not part of the
    /// vocabulary, which is how a profile that names something unknown is told so by having no seeds.
    /// </summary>
    public static List<byte[]> Parse(string hint)
    {
        var bytes = new List<byte>();
        foreach (var part in hint.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "PUSH EBP":
                    bytes.Add(0x55);
                    break;
                case "MOV EBP, ESP":
                    bytes.AddRange([0x8B, 0xEC]);
                    break;
                case "PUSH ESP":
                    bytes.Add(0x54);
                    break;
                case "SUB ESP, IMM":
                    bytes.Add(0x83); // 83 EC imm8, or 81 EC imm32
                    break;

                // x86-64. The opcodes are the 32-bit ones with a REX prefix when a register that needs
                // one is named, which is why "push rbp" and "push ebp" are the same byte.
                case "PUSH RBP":
                    bytes.Add(0x55);
                    break;
                case "MOV RBP, RSP":
                    bytes.AddRange([0x48, 0x89, 0xE5]); // the form GCC and clang emit
                    break;
                case "PUSH RBX":
                    bytes.Add(0x53);
                    break;
                case "PUSH R12":
                    bytes.AddRange([0x41, 0x54]); // REX.B + push r12
                    break;
                case "SUB RSP, IMM":
                    bytes.AddRange([0x48, 0x83]); // 48 83 EC imm8
                    break;
                case "ENDBR64":
                    bytes.AddRange([0xF3, 0x0F, 0x1E, 0xFA]);
                    break;

                default:
                    return [];
            }
        }

        return bytes.Count > 0 ? [bytes.ToArray()] : [];
    }

    /// <summary>
    /// The hint used when a profile names none: the frame pointer sequence of the image's own width.
    /// The 32-bit one is what a 64-bit binary almost never contains, so guessing it there would find
    /// nothing and look like a binary with no functions.
    /// </summary>
    public static string Default(bool is64)
        => is64 ? "push rbp; mov rbp, rsp" : "push ebp; mov ebp, esp";
}
