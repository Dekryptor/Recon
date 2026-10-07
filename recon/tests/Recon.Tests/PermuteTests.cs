using Recon.Config;
using Recon.Permute;
using Recon.Tests.Fixtures;
using Recon.Toolchains;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// The permuter's variant generator (M7's first slice). These tests are about the one property the
/// generator cannot compromise on — a variant must mean the same thing as the source it came from —
/// plus the property that makes a search checkable: the same source gives the same variants in the
/// same order, every time.
/// </summary>
public partial class PermuteTests
{
    private static string Body(string statements) => "int f(int a, int b, int c)\n{\n" + statements + "\n}\n";

    private static List<SourceVariant> OfKind(string source, string kind)
        => VariantGenerator.Generate(source).Where(v => v.Kind == kind).ToList();

    private static SourceVariant? One(string source, string kind)
        => Assert.Single(OfKind(source, kind));

    [Fact]
    public void Two_independent_statements_can_change_places()
    {
        var variant = One(Body("    int x = a + b;\n    int y = c;"), "statement-swap")!;

        Assert.Equal("statement-swap", variant.Kind);
        Assert.Contains("int y = c;", variant.Text);
        Assert.Contains("int x = a + b;", variant.Text);
        Assert.True(variant.Text.IndexOf("int y = c;", StringComparison.Ordinal) < variant.Text.IndexOf("int x = a + b;", StringComparison.Ordinal));
    }

    [Fact]
    public void Statements_that_touch_the_same_variable_are_never_swapped()
    {
        // Writing x and then reading it is not the other way round.
        Assert.Empty(OfKind(Body("    int x = a + b;\n    int y = x;"), "statement-swap"));

        // Nor are two writes to the same variable.
        Assert.Empty(OfKind(Body("    x = a;\n    x = b;"), "statement-swap"));
    }

    [Fact]
    public void A_call_is_never_swapped_with_anything()
    {
        // Two calls may reach the same global state through a name this generator cannot see.
        Assert.Empty(OfKind(Body("    g(a);\n    h(b);"), "statement-swap"));
    }

    [Fact]
    public void Control_flow_statements_are_left_alone()
    {
        Assert.Empty(OfKind(Body("    return a;\n    int y = c;"), "statement-swap"));
    }

    [Fact]
    public void The_operands_of_a_commutative_operator_can_change_places()
    {
        var variant = One(Body("    return a + b;"), "operand-swap")!;

        Assert.Contains("b + a", variant.Text);
        Assert.DoesNotContain("a + b", variant.Text);
    }

    [Fact]
    public void Swapping_operands_keeps_precedence_inside_each_operand()
    {
        // Both operators are commutative, so there are two variants; the one for '+' has to keep
        // 'b * c' whole rather than splitting it.
        var variants = OfKind(Body("    return a + b * c;"), "operand-swap");

        Assert.Equal(2, variants.Count);
        Assert.Contains(variants, v => v.Text.Contains("b * c + a", StringComparison.Ordinal));
        Assert.Contains(variants, v => v.Text.Contains("a + c * b", StringComparison.Ordinal));
    }

    [Fact]
    public void Operators_that_are_not_commutative_are_left_alone()
    {
        Assert.Empty(OfKind(Body("    return a - b;"), "operand-swap"));
        Assert.Empty(OfKind(Body("    return a / b;"), "operand-swap"));
        Assert.Empty(OfKind(Body("    return a < b;"), "operand-swap"));
    }

    [Fact]
    public void A_unary_operator_is_not_treated_as_a_binary_one()
    {
        Assert.Empty(OfKind(Body("    int *p = &a;\n    return 1;"), "operand-swap"));
    }

    [Fact]
    public void Operands_inside_parentheses_are_the_inner_expression_business()
    {
        // The '+' is inside the parentheses, so the statement's own operator is '=='.
        var variants = OfKind(Body("    return (a + b) == c;"), "operand-swap");

        Assert.Contains(variants, v => v.Text.Contains("c == (a + b)", StringComparison.Ordinal));
    }

    [Fact]
    public void An_increment_is_offered_in_the_other_two_forms()
    {
        Assert.Equal("++i;", One(Body("    i++;"), "increment-form")!.Text.Trim().Split('\n').Last(x => x.Contains("++")).Trim());
        Assert.Contains("i += 1;", One(Body("    ++i;"), "increment-form")!.Text);
        Assert.Contains("i++;", One(Body("    i += 1;"), "increment-form")!.Text);
    }

    [Fact]
    public void Two_declarations_of_the_same_type_can_be_declared_in_the_other_order()
    {
        var variant = One(Body("    int x;\n    int y;"), "declaration-swap");

        Assert.NotNull(variant);
        Assert.Contains("declare y before x", variant!.Description);
        Assert.True(variant.Text.IndexOf("int y;", StringComparison.Ordinal) < variant.Text.IndexOf("int x;", StringComparison.Ordinal));
    }

    [Fact]
    public void Declarations_with_initializers_are_not_moved()
    {
        Assert.Empty(OfKind(Body("    int x = a;\n    int y = x;"), "declaration-swap"));
    }

    [Fact]
    public void An_if_with_an_else_can_be_inverted()
    {
        string source = Body("    if (a > b)\n    {\n        return a;\n    }\n    else\n    {\n        return b;\n    }");
        var variant = One(source, "branch-inversion")!;

        Assert.Contains("if (!(a > b))", variant.Text);
        Assert.True(
            variant.Text.IndexOf("return b;", StringComparison.Ordinal) < variant.Text.IndexOf("return a;", StringComparison.Ordinal));
    }

    [Fact]
    public void An_if_without_an_else_is_left_alone()
    {
        string source = Body("    if (a > b)\n    {\n        return a;\n    }\n\n    return b;");
        Assert.Empty(OfKind(source, "branch-inversion"));
    }

    [Fact]
    public void A_condition_is_never_negated_twice_by_being_rewritten_from_a_variant()
    {
        string source = Body("    if (a > b)\n    {\n        return a;\n    }\n    else\n    {\n        return b;\n    }");
        var first = One(source, "branch-inversion")!;

        // Inverting an inverted branch would be a different program with the same bytes; the generator
        // only ever finds one inversion per 'if'.
        Assert.Empty(OfKind(first.Text, "branch-inversion"));
    }

    [Fact]
    public void The_same_source_gives_the_same_variants_in_the_same_order()
    {
        string source = Body("    int x = a + b;\n    int y = c * d;\n\n    if (x > y)\n    {\n        return x;\n    }\n    else\n    {\n        return y;\n    }");
        var first = VariantGenerator.Generate(source);
        var second = VariantGenerator.Generate(source);

        Assert.NotEmpty(first);
        Assert.Equal(first.Select(v => v.Id), second.Select(v => v.Id));
        Assert.Equal(first.Select(v => v.Text), second.Select(v => v.Text));
    }

    [Fact]
    public void A_variant_always_differs_from_its_source()
    {
        string source = Body("    int x = a + b;\n    int y = c * d;\n    x++;\n\n    return x + y;");

        foreach (var variant in VariantGenerator.Generate(source))
        {
            Assert.NotEqual(source, variant.Text);
            Assert.False(string.IsNullOrWhiteSpace(variant.Id));
            Assert.False(string.IsNullOrWhiteSpace(variant.Description));
        }
    }

    [Fact]
    public void Nothing_is_offered_when_nothing_can_be_proved_safe()
    {
        Assert.Empty(VariantGenerator.Generate(Body("    return a;")));
        Assert.Empty(VariantGenerator.Generate("int f(void)\n{\n}\n"));
    }

    [Fact]
    public void The_number_of_variants_can_be_capped()
    {
        string source = Body("    int x = a + b;\n    int y = c * d;\n    int z = x + y;\n    int w = z * z;");

        Assert.True(VariantGenerator.Generate(source).Count > 2);
        Assert.Equal(2, VariantGenerator.Generate(source, limit: 2).Count);

        // The cap takes the first ones, not an arbitrary subset, so a rerun with a bigger budget
        // extends the same search rather than starting another one.
        Assert.Equal(
            VariantGenerator.Generate(source).Take(2).Select(v => v.Id),
            VariantGenerator.Generate(source, limit: 2).Select(v => v.Id));
    }

    // ------------------------------------------------------------------ optimization levels (--flags)

    /// <summary>
    /// Which levels a search may try is a fact about the compiler, so it lives in the profile — and a
    /// profile that extends another inherits them rather than repeating them.
    /// </summary>
    [Fact]
    public void The_levels_a_profile_declares_reach_the_profiles_that_extend_it()
    {
        var registry = BuiltInRegistry(out var diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("; ", diagnostics.Errors));

        var gcc = registry.GetResolved("gcc-14-elf64");
        Assert.NotNull(gcc);
        Assert.Equal(["-O0", "-O1", "-O2", "-O3", "-Os", "-Og"], gcc!.OptimizationLevels);

        // Clang spells them the same way, and its profiles extend the GCC base, so they inherit them.
        var clang = registry.GetResolved("clang-19-elf64");
        Assert.NotNull(clang);
        Assert.Contains("-O2", clang!.OptimizationLevels);

        // MSVC's are separate flags with a different spelling, which is exactly why they are data.
        var msvc = registry.GetResolved("msvc-2008");
        Assert.NotNull(msvc);
        Assert.Equal(["/Od", "/O1", "/O2", "/Ox"], msvc!.OptimizationLevels);
    }

    /// <summary>
    /// A profile that says nothing about optimization offers nothing. That is a fact about the
    /// toolchain — a Visual Basic 6 profile has no [compile] section at all — and inventing levels
    /// for it would turn a search into a guess with a compiler invocation wrapped around it.
    /// </summary>
    [Fact]
    public void A_profile_that_declares_no_levels_offers_none_and_is_left_alone()
    {
        var profile = ProfileOf("""
            schema_version = 1
            id = "no-levels"
            display_name = "a compiler with no optimization flag"
            family = "other"

            [[targets]]
            format = "elf64"
            arch = "x64"

            [compile]
            exe = "cc"
            output_flag = "-o {obj}"

            [link]
            exe = "cc"
            output_flag = "-o {exe}"
            """);

        Assert.Empty(profile.OptimizationLevels);
        Assert.Null(profile.OptimizationLevelOf(["-c", "-O2"]));

        // Asked for a level anyway, it does not invent one.
        Assert.Equal(["-c", "-O2"], profile.WithOptimizationLevel(["-c", "-O2"], "-O0"));
    }

    /// <summary>
    /// The level a flag list asks for, and the one a search replaces: the last one, because that is
    /// the one a compiler acts on. A project whose [defaults] say -O0 over a profile that says -O2 is
    /// built at -O0, and a search that reported -O2 would describe a build that is not happening —
    /// and then change a flag that was already overridden, which changes nothing at all.
    /// </summary>
    [Fact]
    public void When_flags_ask_for_two_levels_the_last_one_is_the_answer()
    {
        var gcc = BuiltInRegistry(out _).GetResolved("gcc-14-elf64")!;

        string[] overridden = ["-c", "-g", "-O2", "-O0"];
        Assert.Equal("-O0", gcc.OptimizationLevelOf(overridden));
        Assert.Equal(["-c", "-g", "-O2", "-O1"], gcc.WithOptimizationLevel(overridden, "-O1"));

        // In place: the order the profile wrote them in is kept, so the diff is one flag.
        Assert.Equal(["-c", "-g", "-O3"], gcc.WithOptimizationLevel(["-c", "-g", "-O2"], "-O3"));

        // No level at all: appended, which is what the compiler would do with two anyway.
        Assert.Equal(["-c", "-g", "-Os"], gcc.WithOptimizationLevel(["-c", "-g"], "-Os"));
    }

    private static ToolchainProfile ProfileOf(string toml)
    {
        using var temp = new TempDir("recon-profile");
        string directory = Path.Combine(temp.Path, "toolchains");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "profile.toml"), toml);

        var diagnostics = new Diagnostics();
        var registry = ToolchainRegistry.Load([directory], diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("; ", diagnostics.Errors));
        return registry.GetResolved("no-levels")!;
    }

    private static ToolchainRegistry BuiltInRegistry(out Diagnostics diagnostics)
    {
        using var temp = new TempDir("recon-profiles");
        string directory = Path.Combine(temp.Path, "toolchains");
        Directory.CreateDirectory(directory);
        BuiltInProfiles.WriteTo(directory);
        diagnostics = new Diagnostics();
        return ToolchainRegistry.Load([directory], diagnostics);
    }
}
