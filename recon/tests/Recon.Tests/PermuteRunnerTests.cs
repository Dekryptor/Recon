using System.Text;
using Recon.Config;
using Recon.Pe;
using Recon.Permute;
using Recon.Project;
using Recon.Tests.Fixtures;
using Recon.Toolchains;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// M7 — the permutation loop. Where <see cref="PermuteTests"/> checks that the variants it dreams up
/// mean the same thing, these check the part that costs money: write a variant, build the whole
/// project, score the function against the original, and put the source back. They run over the ELF
/// corpus with the machine's own gcc, because the loop's only reason to exist is a real compiler
/// producing real bytes; with no compiler on PATH they pass having done nothing, the way the other
/// toolchain tests do.
///
/// They also need the corpus itself, which is not in a checkout: tagged so that a host which cannot
/// build <c>tools/build-elf-corpus.sh</c>'s output (Windows has no ELF compiler) can leave them out by
/// trait instead of watching them fail.
/// </summary>
[Trait("requires", "elf-corpus")]
public class PermuteRunnerTests
{
    /// <summary>The example project's reconstruction of the corpus' three functions.</summary>
    private const string Arith = """
        int __attribute__((noinline)) add(int a, int b)
        {
            return a + b;
        }

        int __attribute__((noinline)) sub_fast(int a, int b)
        {
            return a - b;
        }

        int __attribute__((noinline)) mul_std(int a, int b)
        {
            return a * b;
        }
        """;

    /// <summary>The same three functions with <c>add</c> written the other way round.</summary>
    private static readonly string ArithSwapped = Arith.Replace("return a + b;", "return b + a;");

    /// <summary>
    /// The reconstruction plus <c>fib</c>. A search for the right optimization level can only be
    /// checked against a function the level actually changes: <c>add</c> is one instruction at every
    /// level from -O1 up, and the only difference is padding, which the compare engine ignores — so
    /// at -O0 it looks equally wrong and at -O1 equally right. <c>fib</c> recurses, and gcc lays it
    /// out differently at -O0, -O1, -O2, -Os and -Og.
    /// </summary>
    private static readonly string ArithWithFib = Arith + """

        int __attribute__((noinline)) fib(int n)
        {
            if (n < 2) {
                return n;
            }

            return fib(n - 1) + fib(n - 2);
        }
        """;

    /// <summary>A unit that provides main, so the link step has somewhere to start.</summary>
    private const string Entry = """
        int __attribute__((noinline)) main(int argc, char **argv)
        {
            (void)argc;
            (void)argv;
            return 0;
        }
        """;

    // ------------------------------------------------------------ the baseline and the ranking

    [Fact]
    public void The_baseline_is_the_source_as_it_stands()
    {
        if (!GccIsAvailable())
        {
            return;
        }

        using var project = ElfProject(Arith);
        var document = project.Run("add");

        Assert.Equal("arith", document.Unit);
        Assert.Equal("src/arith.c", document.Source);
        Assert.True(document.BaselineExact, $"baseline score {document.BaselineScore}");
        Assert.Equal(1.0, document.BaselineScore);

        // And having said that, it stops: no variant can beat an exact baseline.
        Assert.Contains("already", document.StoppedBecause);
        Assert.Empty(document.Variants);
    }

    /// <summary>
    /// The reason to have a permuter at all: <c>a + b</c> and <c>b + a</c> are the same function, and
    /// the compiler still emits different bytes for them. One of the two scores 1.0 and the other
    /// does not, so the score — not a human reading the source — says which one the original used.
    /// </summary>
    [Fact]
    public void Two_sources_that_mean_the_same_thing_do_not_score_the_same()
    {
        if (!GccIsAvailable())
        {
            return;
        }

        using var good = ElfProject(Arith);
        using var swapped = ElfProject(ArithSwapped);

        double straight = good.Run("add", stopOnExact: false).BaselineScore;
        double reversed = swapped.Run("add", stopOnExact: false).BaselineScore;

        Assert.True(straight > reversed, $"'a + b' scored {straight}, 'b + a' scored {reversed}");
        Assert.True(good.Run("add").BaselineExact);
        Assert.False(swapped.Run("add").BaselineExact);
    }

    /// <summary>
    /// The search doing its job: from the source written the wrong way round, it finds the form that
    /// reproduces the original, marks it better than the baseline, and writes it where a human can
    /// adopt it — not over the unit's own source.
    /// </summary>
    [Fact]
    public void The_search_finds_the_form_the_original_used_and_writes_it_beside_the_source()
    {
        if (!GccIsAvailable())
        {
            return;
        }

        using var project = ElfProject(ArithSwapped);
        var document = project.Run("add");

        Assert.False(document.BaselineExact);
        Assert.NotNull(document.Best);
        Assert.True(document.BestBeatsBaseline, $"best {document.Best!.Score} vs baseline {document.BaselineScore}");
        Assert.True(document.Best!.Exact);
        Assert.Equal("operand-swap", document.Best!.Kind);

        string written = project.PathOf(Path.Combine("build", "permute", "arith.c"));
        Assert.True(File.Exists(written), $"expected the winning source at {written}");
        Assert.Contains("return a + b;", File.ReadAllText(written));

        // The unit's own source is untouched: adopting a variant is a decision for the human.
        Assert.Equal(ArithSwapped, project.Source("src/arith.c"));
    }

    // ----------------------------------------------------------------------------- being safe

    /// <summary>
    /// The property the whole loop is built around: it writes variants over the unit's source, so if
    /// it ever left one there it would have silently permuted someone's reconstruction.
    /// </summary>
    [Fact]
    public void The_source_is_put_back_when_the_run_ends()
    {
        if (!GccIsAvailable())
        {
            return;
        }

        using var project = ElfProject(ArithSwapped);
        string before = project.Source("src/arith.c");

        var document = project.Run("add", stopOnExact: false);

        Assert.True(document.Tried > 0, "the run has to have written something over the source");
        Assert.Equal(before, project.Source("src/arith.c"));
    }

    /// <summary>
    /// A project that does not build is a result, not a crash: the permuter reports it and stops
    /// before writing anything over the source. Reconstruction sources are mid-edit by nature, so
    /// this is the ordinary case, not the exceptional one.
    /// </summary>
    [Fact]
    public void A_project_that_does_not_build_as_it_stands_says_so_instead_of_permuting()
    {
        if (!GccIsAvailable())
        {
            return;
        }

        // Parses, and even offers a candidate — the other function is what will not compile, which
        // is what a reconstruction looks like while it is half written.
        const string broken = """
            int __attribute__((noinline)) add(int a, int b)
            {
                return a + b;
            }

            int __attribute__((noinline)) sub_fast(int a, int b)
            {
                return not_written_yet(a, b);
            }
            """;

        using var project = ElfProject(broken);
        var document = project.Run("add", stopOnExact: false);

        Assert.Contains("does not build", document.StoppedBecause);
        Assert.Empty(document.Variants);
        Assert.Equal(0, document.Tried);
        Assert.Null(document.Best);
        Assert.Equal(broken, project.Source("src/arith.c"));
    }

    // --------------------------------------------------------------------------- stopping early

    [Fact]
    public void A_budget_stops_the_run_before_every_candidate_is_tried()
    {
        if (!GccIsAvailable())
        {
            return;
        }

        using var project = ElfProject(ArithSwapped);
        var document = project.Run("add", budget: 1, stopOnExact: false);

        Assert.True(document.CandidateCount > 1, "the fixture needs more than one candidate to budget against");
        Assert.Equal(1, document.Tried);
        Assert.Single(document.Variants);
        Assert.Contains("budget", document.StoppedBecause);
    }

    [Fact]
    public void Stopping_on_an_exact_match_stops_before_the_budget_runs_out()
    {
        if (!GccIsAvailable())
        {
            return;
        }

        // From the source written the wrong way round: the first variant is the one that matches,
        // so the run ends before it runs out of candidates.
        using var project = ElfProject(ArithSwapped);
        var document = project.Run("add", budget: 8, stopOnExact: true);

        Assert.True(document.CandidateCount > 1);
        Assert.True(document.Tried < document.CandidateCount, "an exact variant should have ended the run");
        Assert.Contains("exact", document.StoppedBecause);
        Assert.True(document.Variants[^1].Exact);
    }

    // ------------------------------------------------------------------------------ the flags

    /// <summary>
    /// <c>--flags</c>: from a project that builds at the wrong level, the search finds the level the
    /// original was built at. This is the other half of "which build made these bytes" — no edit to
    /// the source can be exact while the whole image is optimized differently, so it is tried first,
    /// and it is found by the compare engine's own score rather than by a rule about what -O2 looks
    /// like.
    /// </summary>
    [Fact]
    public void The_search_finds_the_optimization_level_the_original_was_built_at()
    {
        if (!GccIsAvailable())
        {
            return;
        }

        // The corpus binary was compiled -O2 -g; the profile says -O2, and [defaults] overrides it
        // with -O0, which is the ordinary way a reconstruction is wrong about a build.
        using var project = ElfProject(
            ArithWithFib,
            defaults: "flags = [\"-O0\"]",
            covers: [.. DefaultCovers, "fib"]);
        var document = project.Run("fib", permuteFlags: true, budget: 12, stopOnExact: false);

        Assert.False(document.BaselineExact, "a -O0 build of this source is not the original's bytes");

        var best = document.Best;
        Assert.NotNull(best);

        // Optimized away, renamed, or still different: whatever happened, it is in the document.
        Assert.Null(best!.BuildError);
        Assert.Equal("optimization-level", best.Kind);
        Assert.Equal("-O2", best.Optimization);
        Assert.Equal("-O2", document.BestOptimization);

        // Every level the original was not built at scores clearly worse, which is what makes the
        // ranking an answer rather than a list. (-O3 emits the same code as -O2 for this function,
        // so it ties; the level named is the first of them, which is -O2.)
        foreach (string level in new[] { "-O1", "-Os", "-Og" })
        {
            var other = Assert.Single(document.Variants, v => v.VariantId == "flags:" + level);
            Assert.True(
                best.Score > other.Score + 0.1,
                $"{level} scored {other.Score}, but {best.VariantId} scored {best.Score}");
        }

        // The levels the toolchain offers are published, so the terms of the search can be read back.
        Assert.Contains("-O2", document.OptimizationLevels);

        // What won is a change to the build, not to the text: there is no source to adopt, and
        // writing the file out unchanged would look like a result.
        Assert.Null(document.BestSource);
        Assert.Contains("-O2", document.BestWriteError ?? string.Empty);
        Assert.Equal(ArithWithFib, project.Source("src/arith.c"));
    }

    /// <summary>
    /// A flag variant is still a build: it stops the run the same way an edit does, and the source
    /// goes back afterwards, whatever happened in between.
    /// </summary>
    [Fact]
    public void A_run_that_permuted_flags_leaves_the_source_as_it_found_it()
    {
        if (!GccIsAvailable())
        {
            return;
        }

        using var project = ElfProject(Arith, defaults: "flags = [\"-O0\"]");
        string before = project.Source("src/arith.c");

        var document = project.Run("add", permuteFlags: true);

        Assert.True(document.Tried > 0);
        Assert.Equal(before, project.Source("src/arith.c"));
        Assert.Contains("exact", document.StoppedBecause);
    }

    // ------------------------------------------------------------------------ configuration

    [Fact]
    public void A_function_no_unit_covers_is_a_configuration_error()
    {
        using var project = ElfProject(Arith);

        var error = Assert.Throws<ConfigException>(() => project.Run("no_such_function"));

        Assert.Contains("no unit", string.Join("; ", error.Diagnostics.Select(d => d.Message)));
    }

    [Fact]
    public void A_unit_name_that_is_not_in_the_project_is_a_configuration_error()
    {
        using var project = ElfProject(Arith);

        var error = Assert.Throws<ConfigException>(() => project.Run("add", unit: "nope"));

        Assert.Contains("no unit named", string.Join("; ", error.Diagnostics.Select(d => d.Message)));
    }

    // ---------------------------------------------------------------------------- the fixture

    /// <summary>The machine's gcc, which is the compiler the gcc-14-elf64 profile resolves to.</summary>
    private static bool GccIsAvailable()
    {
        string[] path = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator);
        return path.Any(directory => File.Exists(Path.Combine(directory, "gcc")))
            || File.Exists("/usr/bin/gcc");
    }

    /// <summary>
    /// The <c>[defaults]</c> table of project.toml, from the body alone: a test that says what the
    /// build's own flags are should not also have to spell the header right.
    /// </summary>
    private static string DefaultsSection(string? defaults)
        => defaults is null ? string.Empty : $"[defaults]\n{defaults}";

    private static ElfProjectFixture ElfProject(
        string arith,
        (string Name, string Source)? extraUnit = null,
        string? defaults = null,
        string[]? covers = null)
        => new(arith, extraUnit, defaults, covers);

    private static readonly string[] DefaultCovers = ["add", "sub_fast", "mul_std"];

    private static string Covers(string[]? covers)
        => string.Join("\n\n", (covers ?? DefaultCovers).Select(symbol => $"[[unit.covers]]\nsymbol = \"{symbol}\""));

    /// <summary>
    /// A project over the ELF corpus: the real binary as the original, two units of C that rebuild
    /// parts of it, and the built-in profiles so the toolchain is found from the binary itself.
    /// </summary>
    private sealed class ElfProjectFixture : IDisposable
    {
        private readonly TempDir _temp = new("recon-permute");

        public ElfProjectFixture(
            string arith,
            (string Name, string Source)? extraUnit,
            string? defaults,
            string[]? covers)
        {
            string binary = TestPaths.ElfCorpus("sample-elf64-release");
            Assert.True(File.Exists(binary), $"the ELF corpus is missing: {binary} (bash tools/build-elf-corpus.sh)");

            string profiles = _temp.PathOf("toolchains");
            Directory.CreateDirectory(profiles);
            BuiltInProfiles.WriteTo(profiles);

            var units = new StringBuilder($"""
                [[unit]]
                name = "arith"
                source = "src/arith.c"
                status = "wip"

                {Covers(covers)}

                [[unit]]
                name = "entry"
                source = "src/entry.c"
                status = "wip"

                [[unit.covers]]
                symbol = "main"

                """);

            _temp.Write("src/arith.c", arith);
            _temp.Write("src/entry.c", Entry);

            if (extraUnit is { } extra)
            {
                units.Append($"""
                    [[unit]]
                    name = "{extra.Name}"
                    source = "src/{extra.Name}.c"
                    status = "wip"

                    """);
                _temp.Write($"src/{extra.Name}.c", extra.Source);
            }

            _temp.Write("project.toml", $"""
                schema_version = 1

                [project]
                name = "elf-permute"

                [target]
                format = "elf64"
                arch = "x64"
                isa = "x64"

                [[input]]
                id = "main"
                role = "original"
                file = "sample-elf64-release"
                sha256 = "{PeImage.HashFile(binary)}"

                [paths]
                source = "src"
                include = []
                build = "build"
                profiles = ["{profiles.Replace('\\', '/')}"]

                {DefaultsSection(defaults)}

                {units}
                """);

            _temp.Write("local.toml", $"""
                schema_version = 1

                [inputs]
                dir = "{TestPaths.ElfCorpusDirectory.Replace('\\', '/')}"

                [toolchain.gcc-14-elf64]
                """);

            var diagnostics = new Diagnostics();
            Context = ProjectContext.Load(_temp.PathOf("project.toml"), diagnostics);
            Assert.False(diagnostics.HasErrors, string.Join("; ", diagnostics.Errors));
        }

        public ProjectContext Context { get; }

        public string PathOf(string name) => _temp.PathOf(name);

        public string Source(string name) => File.ReadAllText(_temp.PathOf(name));

        public PermuteDocument Run(
            string function,
            string? unit = null,
            int? budget = null,
            bool stopOnExact = true,
            bool permuteFlags = false)
        {
            var inputs = new PermuterInputs
            {
                Context = Context,
                Function = function,
                Options = new PermuteOptions
                {
                    Unit = unit,
                    StopOnExact = stopOnExact,
                    Budget = budget ?? 24,
                    PermuteFlags = permuteFlags,
                },
                // No log: these tests read the document, not the compiler's chatter.
                Log = null,
                ToolVersion = "test",
                OutputDirectory = _temp.PathOf(Path.Combine("build", "permute")),
            };

            return Permuter.Run(inputs);
        }

        public void Dispose() => _temp.Dispose();
    }
}
