using System.Text.RegularExpressions;
using Recon.Pe;

namespace Recon.Compare;

/// <summary>The result of comparing two function bodies.</summary>
public sealed class FunctionDiffResult
{
    public int LeftCount { get; set; }

    public int RightCount { get; set; }

    /// <summary>Instructions that align and are equal after normalization.</summary>
    public int Equal { get; set; }

    /// <summary>Equal instructions over the longer body: 1.0 when the bodies match completely.</summary>
    public double Score { get; set; }

    /// <summary>Every difference found, not only the reported ones.</summary>
    public int DifferenceCount { get; set; }

    /// <summary>
    /// The alignment the differences came out of: every instruction of both bodies in reading order,
    /// paired where they correspond. Kept so a caller can print the whole body, not just the changes.
    /// </summary>
    public List<(int? Left, int? Right)> Alignment { get; set; } = [];

    public List<InstructionDifference> Differences { get; set; } = [];

    public bool Exact => DifferenceCount == 0 && LeftCount == RightCount;
}

/// <summary>
/// Aligns two normalized bodies and says where they differ. Alignment is a longest common
/// subsequence over the normalized instructions, so an inserted or removed instruction shifts the
/// rest instead of making everything after it look different; a removed run and an added run of the
/// same length are then paired up positionally, because "this instruction changed" is more useful to
/// read than "this one went away and that one appeared".
/// </summary>
public static partial class FunctionDiffer
{
    private const int MaxReportedDifferences = 24;

    /// <summary>Above this many instruction pairs the exact alignment is skipped for a cheaper one.</summary>
    private const int MaxAlignmentCells = 400_000;

    public static FunctionDiffResult Diff(NormalizedFunction left, NormalizedFunction right)
    {
        var result = new FunctionDiffResult
        {
            LeftCount = left.Instructions.Count,
            RightCount = right.Instructions.Count,
        };

        var leftTokens = left.Instructions.Select(i => i.Text).ToArray();
        var rightTokens = right.Instructions.Select(i => i.Text).ToArray();

        var alignment = Align(leftTokens, rightTokens);
        result.Alignment = alignment;
        result.Equal = alignment.Count(a => a is { Left: not null, Right: not null });

        for (int index = 0; index < alignment.Count; index++)
        {
            var (leftIndex, rightIndex) = alignment[index];
            if (leftIndex is not null && rightIndex is not null)
            {
                continue; // equal by construction of the alignment
            }

            // A run of removed instructions followed by a run of added ones is a change, not a
            // deletion and an insertion: pair them while both runs last.
            int removedStart = index;
            while (index < alignment.Count && alignment[index] is { Left: not null, Right: null })
            {
                index++;
            }

            int removedCount = index - removedStart;
            int addedStart = index;
            while (index < alignment.Count && alignment[index] is { Left: null, Right: not null })
            {
                index++;
            }

            int addedCount = index - addedStart;
            int paired = Math.Min(removedCount, addedCount);

            for (int pair = 0; pair < paired; pair++)
            {
                var leftInsn = left.Instructions[alignment[removedStart + pair].Left!.Value];
                var rightInsn = right.Instructions[alignment[addedStart + pair].Right!.Value];
                Add(result, new InstructionDifference
                {
                    Kind = Classify(leftInsn, rightInsn),
                    LeftRva = leftInsn.Rva,
                    RightRva = rightInsn.Rva,
                    LeftText = leftInsn.Text,
                    RightText = rightInsn.Text,
                    LeftReference = leftInsn.References?.FirstOrDefault(),
                    RightReference = rightInsn.References?.FirstOrDefault(),
                });
            }

            for (int extra = paired; extra < removedCount; extra++)
            {
                var leftInsn = left.Instructions[alignment[removedStart + extra].Left!.Value];
                Add(result, new InstructionDifference
                {
                    Kind = "removed",
                    LeftRva = leftInsn.Rva,
                    LeftText = leftInsn.Text,
                    LeftReference = leftInsn.References?.FirstOrDefault(),
                });
            }

            for (int extra = paired; extra < addedCount; extra++)
            {
                var rightInsn = right.Instructions[alignment[addedStart + extra].Right!.Value];
                Add(result, new InstructionDifference
                {
                    Kind = "added",
                    RightRva = rightInsn.Rva,
                    RightText = rightInsn.Text,
                    RightReference = rightInsn.References?.FirstOrDefault(),
                });
            }

            index--; // the loop's own increment moves past the run we just consumed
        }

        int longer = Math.Max(result.LeftCount, result.RightCount);
        result.Score = longer == 0 ? 1.0 : (double)result.Equal / longer;
        return result;
    }

    private static void Add(FunctionDiffResult result, InstructionDifference difference)
    {
        result.DifferenceCount++;
        if (result.Differences.Count < MaxReportedDifferences)
        {
            result.Differences.Add(difference);
        }
    }

    /// <summary>
    /// Pairs of instruction indices. A pair with both sides equal is an alignment; a pair with one
    /// side null is an instruction the other side does not have at that point.
    /// </summary>
    private static List<(int? Left, int? Right)> Align(string[] left, string[] right)
    {
        var result = new List<(int?, int?)>(left.Length + right.Length);

        if (left.Length == 0 || right.Length == 0 || (long)left.Length * right.Length > MaxAlignmentCells)
        {
            // Nothing to align, or too large to align exactly: fall back to position.
            int common = Math.Min(left.Length, right.Length);
            for (int i = 0; i < common; i++)
            {
                if (string.Equals(left[i], right[i], StringComparison.Ordinal))
                {
                    result.Add((i, i));
                }
                else
                {
                    result.Add((i, null));
                    result.Add((null, i));
                }
            }

            for (int i = common; i < left.Length; i++)
            {
                result.Add((i, null));
            }

            for (int i = common; i < right.Length; i++)
            {
                result.Add((null, i));
            }

            return result;
        }

        var lengths = new int[left.Length + 1, right.Length + 1];
        for (int i = left.Length - 1; i >= 0; i--)
        {
            for (int j = right.Length - 1; j >= 0; j--)
            {
                lengths[i, j] = string.Equals(left[i], right[j], StringComparison.Ordinal)
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        int a = 0;
        int b = 0;
        while (a < left.Length && b < right.Length)
        {
            if (string.Equals(left[a], right[b], StringComparison.Ordinal))
            {
                result.Add((a, b));
                a++;
                b++;
            }
            else if (lengths[a + 1, b] >= lengths[a, b + 1])
            {
                result.Add((a, null));
                a++;
            }
            else
            {
                result.Add((null, b));
                b++;
            }
        }

        while (a < left.Length)
        {
            result.Add((a++, null));
        }

        while (b < right.Length)
        {
            result.Add((null, b++));
        }

        return result;
    }

    /// <summary>What changed between two instructions that occupy the same place in the two bodies.</summary>
    public static string Classify(NormalizedInstruction left, NormalizedInstruction right)
    {
        if (left.Mnemonic != right.Mnemonic)
        {
            return "opcode";
        }

        if (!(left.References ?? []).SequenceEqual(right.References ?? [], StringComparer.Ordinal))
        {
            // The code is the same but points at something else. This is the difference a rebuild
            // must not produce, and the one a relocation must not hide.
            return "reference";
        }

        string[] leftOperands = Operands(left.Text);
        string[] rightOperands = Operands(right.Text);
        if (leftOperands.Length == rightOperands.Length)
        {
            for (int index = 0; index < leftOperands.Length; index++)
            {
                if (string.Equals(leftOperands[index], rightOperands[index], StringComparison.Ordinal))
                {
                    continue;
                }

                string? kind = ClassifyOperands(leftOperands[index], rightOperands[index]);
                if (kind is not null)
                {
                    return kind;
                }
            }
        }

        return "operand";
    }

    /// <summary>Operands of a formatted instruction: everything after the mnemonic, split on commas.</summary>
    private static string[] Operands(string text)
    {
        int space = text.IndexOf(' ');
        if (space < 0)
        {
            return [];
        }

        return text[(space + 1)..].Split(", ", StringSplitOptions.TrimEntries);
    }

    private static string? ClassifyOperands(string left, string right)
    {
        string[] leftWords = left.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] rightWords = right.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (leftWords.Length != rightWords.Length)
        {
            return left.Contains('[') || right.Contains('[') ? "addressing" : null;
        }

        for (int index = 0; index < leftWords.Length; index++)
        {
            if (string.Equals(leftWords[index], rightWords[index], StringComparison.Ordinal))
            {
                continue;
            }

            string leftWord = leftWords[index].Trim('[', ']', ',', '+', '*');
            string rightWord = rightWords[index].Trim('[', ']', ',', '+', '*');

            if (Registers.Contains(leftWord) && Registers.Contains(rightWord))
            {
                return "register";
            }

            if (IsNumber(leftWord) && IsNumber(rightWord))
            {
                return "immediate";
            }

            if (left.Contains('[') || right.Contains('['))
            {
                return "addressing";
            }
        }

        return null;
    }

    private static bool IsNumber(string word)
        => NumberPattern().IsMatch(word);

    [GeneratedRegex("^(0x[0-9A-Fa-f]+|[0-9][0-9A-Fa-f]*h?|offset)$")]
    private static partial Regex NumberPattern();

    /// <summary>Registers iced prints, used only to name what a difference is about.</summary>
    private static readonly HashSet<string> Registers = new(StringComparer.OrdinalIgnoreCase)
    {
        "eax", "ebx", "ecx", "edx", "esi", "edi", "ebp", "esp", "eip", "eflags",
        "ax", "bx", "cx", "dx", "si", "di", "bp", "sp", "ip",
        "al", "ah", "bl", "bh", "cl", "ch", "dl", "dh",
        "cs", "ds", "es", "fs", "gs", "ss",
        "st", "st0", "st1", "st2", "st3", "st4", "st5", "st6", "st7",
        "mm0", "mm1", "mm2", "mm3", "mm4", "mm5", "mm6", "mm7",
        "xmm0", "xmm1", "xmm2", "xmm3", "xmm4", "xmm5", "xmm6", "xmm7",
        "cr0", "cr2", "cr3", "cr4", "dr0", "dr1", "dr2", "dr3", "dr6", "dr7",
    };
}
