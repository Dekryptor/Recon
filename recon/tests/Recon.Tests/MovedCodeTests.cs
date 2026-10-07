using System.Text.Json;
using Recon.Pe;
using Recon.Tests.Fixtures;
using Recon.Toolchains;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// Two builds of one program, with the code identical and the layout not: the second is compiled with
/// `-falign-functions=32`, which moves functions and pads them without changing a single statement.
/// The comparison has to be about the code.
///
/// This is the test for a class of bug that hides behind a plausible number. Before it, the same source
/// compared against itself across that flag scored **0.8638**: 78 of 142 functions "changed", every one
/// of them because a branch target inside the function was named by where the function *sits* (`.text+0x657`)
/// rather than by where the branch goes within it, and because an immediate holding the address of a
/// named function was not recognised as an address at all when the linker left it unrelocated.
/// </summary>
public class MovedCodeTests
{
    private const string Source = """
        extern int other(int);

        int other(int x)
        {
            return x * 3 + 1;
        }

        /* Two loops and a branch inside a loop: the compiler's output has branches that stay inside
           this function, which is what has to be identified relatively. */
        int probe_loops(int n)
        {
            int total = 0;
            for (int i = 0; i < n; i++)
            {
                if (i & 1)
                {
                    total += i;
                }
                else
                {
                    total -= i;
                }

                while (total > 1000)
                {
                    total -= 7;
                }
            }

            return total + other(n);
        }

        /* A function that takes the address of another. The address ends up in the code as an
           immediate, which some linkers relocate and some do not. */
        int (*volatile probe_pointer)(int) = other;

        int main(void)
        {
            return probe_loops(11);
        }
        """;

    private static bool Ready => ToolDetection.Exists(BuildTests.MingwCcName);

    [Fact]
    public void Aligning_functions_does_not_make_them_differ()
    {
        if (!Ready)
        {
            return;
        }

        using var plain = new BuildTests.BuildProject(
            source: Source, mingw: true, unitFlags: new[] { "-O2", "-g", "-Wall" });
        using var aligned = new BuildTests.BuildProject(
            source: Source, mingw: true, unitFlags: new[] { "-O2", "-g", "-Wall", "-falign-functions=32" });

        Assert.Equal(0, CliRun.Run("build", "--project", plain.DirectoryPath).ExitCode);
        Assert.Equal(0, CliRun.Run("build", "--project", aligned.DirectoryPath).ExitCode);

        // Building the two binaries has to have moved the functions, or the comparison would pass for
        // the wrong reason.
        string left = plain.PathOf("build/sample.exe");
        string right = aligned.PathOf("build/sample.exe");
        Assert.NotEqual(PeImage.HashFile(left), PeImage.HashFile(right));

        // Every project declares an input, and the comparison command checks it: what the project was
        // asked to reconstruct is its own build here, since this test is about one source twice.
        plain.CopyInput(left);
        aligned.CopyInput(right);

        string comparison = Path.Combine(plain.DirectoryPath, "build", "moved.json");
        var diff = CliRun.Run(
            "diff", "--project", plain.DirectoryPath, right, "-o", comparison, "--summary");
        Assert.True(diff.ExitCode == 0, diff.All);

        using var document = JsonDocument.Parse(File.ReadAllText(comparison));
        var functions = document.RootElement.GetProperty("functions").EnumerateArray().ToList();
        Assert.NotEmpty(functions);

        // The test's own function: the same statements, in the same order, at another address.
        var probe = functions.Single(f => Name(f) == "probe_loops");
        Assert.Equal(
            "exact",
            probe.GetProperty("status").GetString());

        // And the compiler's own function that pushes the address of another one as an immediate.
        // `_matherr` is named by the symbol table; an immediate that lands on a name is an address
        // whatever the linker did or did not relocate.
        var preCInit = functions.FirstOrDefault(f => Name(f) == "pre_c_init");
        Assert.True(preCInit.ValueKind == JsonValueKind.Object, "the corpus binary should name pre_c_init");
        Assert.Equal("exact", preCInit.GetProperty("status").GetString());

        // The property, for every function in the image: a branch target named by its position in the
        // section is a fact about the layout, so two sides that only disagree about *where* something
        // is must not be reported as differing about it.
        foreach (var function in functions)
        {
            foreach (var difference in function.GetProperty("differences").EnumerateArray())
            {
                string? leftText = Text(difference, "left_text");
                string? rightText = Text(difference, "right_text");
                bool sectionRelative =
                    leftText?.Contains(".text+", StringComparison.Ordinal) == true
                    && rightText?.Contains(".text+", StringComparison.Ordinal) == true;
                Assert.False(
                    sectionRelative,
                    $"{Name(function)} differs only in where the code is: {leftText} vs {rightText}");
            }
        }
    }

    /// <summary>
    /// The complement of the test above: the *code* stays where it was and the data moves.
    ///
    /// The corpus's own source, with one thing added to it — a string constant that the second build
    /// has and the first one does not. That pushes every literal in `.rdata` along, and the functions
    /// that push a literal's address (`printf("%d %d %s\n", …)`) then differ in exactly one operand
    /// per literal. Nothing about those functions changed: the operand *is* the address of a literal,
    /// the literal has no name — a string is not a symbol — and comparing the two addresses says the
    /// function was rewritten.
    ///
    /// What is at the address is the evidence: the same text somewhere else is the same text. This
    /// pair scored 0.9934 with fifteen functions "changed" before that was done, and every difference
    /// was two `.rdata` addresses.
    /// </summary>
    [Fact]
    public void Moving_the_literals_does_not_make_the_functions_that_use_them_differ()
    {
        if (!Ready)
        {
            return;
        }

        string source = File.ReadAllText(TestPaths.CorpusSource("sample.c"));
        string shiftedSource = source.Replace(
            "const char *g_message = \"corpus\";",
            "const char *g_message = \"corpus\";\nconst char g_extra[32] = \"one literal the second build has and the first does not\";");

        Assert.NotEqual(source, shiftedSource);

        using var plain = new BuildTests.BuildProject(
            source: source, mingw: true, unitFlags: new[] { "-O2", "-g", "-Wall" });
        using var shifted = new BuildTests.BuildProject(
            source: shiftedSource, mingw: true, unitFlags: new[] { "-O2", "-g", "-Wall" });

        Assert.Equal(0, CliRun.Run("build", "--project", plain.DirectoryPath).ExitCode);
        Assert.Equal(0, CliRun.Run("build", "--project", shifted.DirectoryPath).ExitCode);

        string left = plain.PathOf("build/sample.exe");
        string right = shifted.PathOf("build/sample.exe");
        Assert.NotEqual(PeImage.HashFile(left), PeImage.HashFile(right));

        plain.CopyInput(left);
        shifted.CopyInput(right);

        string comparison = Path.Combine(plain.DirectoryPath, "build", "shifted.json");
        var diff = CliRun.Run(
            "diff", "--project", plain.DirectoryPath, right, "-o", comparison, "--summary");
        Assert.True(diff.ExitCode == 0, diff.All);

        using var document = JsonDocument.Parse(File.ReadAllText(comparison));
        var functions = document.RootElement.GetProperty("functions").EnumerateArray().ToList();

        // The functions whose operands are the addresses of the moved literals are exact: the same
        // text at another address is the same text.
        foreach (string name in new[] { "printf.constprop.0", "fprintf.constprop.0", "fail", "main" })
        {
            var function = functions.SingleOrDefault(f => Name(f) == name);
            if (function.ValueKind != JsonValueKind.Object)
            {
                continue;   // the compiler named it differently in this build; the score below still holds
            }

            Assert.Equal("exact", function.GetProperty("status").GetString());
        }

        double score = document.RootElement.GetProperty("summary").GetProperty("score").GetDouble();
        Assert.True(score > 0.999, $"score was {score}, and the two builds differ in one constant");

        // And the comparison says *how* it got there: operands with no name were resolved by what is at
        // them rather than by where they are. Without that count the test could pass on a pair where
        // nothing moved at all, which is the way this kind of test goes quiet.
        int identified = document.RootElement
            .GetProperty("model").GetProperty("references_identified").GetInt32();
        Assert.True(identified > 0, "no operand was resolved by its contents");
    }

    private static string? Name(JsonElement function)
        => function.TryGetProperty("left", out var left) && left.ValueKind == JsonValueKind.Object
            ? left.TryGetProperty("name", out var name) ? name.GetString() : null
            : function.TryGetProperty("name", out var direct) ? direct.GetString() : null;

    private static string? Text(JsonElement difference, string key)
        => difference.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
