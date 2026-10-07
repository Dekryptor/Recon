using Recon.Config;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// How one <c>[[unit.covers]]</c> entry resolves, which is the same answer whether it is being asked
/// by the progress report (how much have I done) or by the delinker (what is this unit allowed to
/// provide). A cover is written by a human against whatever name they happened to see — mangled,
/// demangled, or half of one — so it resolves leniently; the test is that lenient is not the same as
/// careless, and that a glob means several where a name means one.
/// </summary>
public class CoversTests
{
    private static readonly Covers.CoverCandidate[] Functions =
    [
        new("_ZN5Shape4areaEv", 0x1000, 32) { Aliases = [] },
        new("_ZN5Shape8describeEv", 0x1020, 48) { Aliases = [] },
        new("log_reader", 0x1050, 16),
        new("log_reader_close", 0x1060, 16),
    ];

    [Fact]
    public void A_cover_named_exactly_claims_that_one_function()
    {
        var resolved = Covers.Resolve(Cover("log_reader"), Functions);

        Assert.Equal([2], resolved);
    }

    [Fact]
    public void A_cover_written_as_the_demangled_name_claims_the_mangled_function()
    {
        var resolved = Covers.Resolve(Cover("Shape::area()"), Functions);

        Assert.Equal([0], resolved);
    }

    [Fact]
    public void A_glob_claims_every_function_it_matches()
    {
        // The star is a request for several: a class's worth of methods, or one family of helpers.
        Assert.Equal([0, 1], Covers.Resolve(Cover("_ZN5Shape*"), Functions));
        Assert.Equal([2, 3], Covers.Resolve(Cover("log_reader*"), Functions));
    }

    [Fact]
    public void A_glob_pinned_at_the_end_does_not_match_a_name_that_continues()
    {
        // `*_reader` is not `log_reader_close`: the end of the pattern is the end of the name.
        Assert.Equal([2], Covers.Resolve(Cover("*_reader"), Functions));
    }

    [Fact]
    public void A_glob_that_matches_nothing_claims_nothing()
    {
        Assert.Empty(Covers.Resolve(Cover("NoSuchThing*"), Functions));
    }

    [Fact]
    public void A_range_cover_claims_what_it_overlaps()
    {
        // 0x1028 lands inside Shape::describe (0x1020 + 48) and in nothing else; a range that spans
        // both method bodies claims both.
        Assert.Equal([1], Covers.Resolve(Range(0x1028, 4), Functions));
        Assert.Equal([0, 1], Covers.Resolve(Range(0x1000, 0x40), Functions));
        Assert.Empty(Covers.Resolve(Range(0x9000, 4), Functions));
    }

    [Fact]
    public void A_partial_name_only_claims_a_function_when_it_is_unambiguous()
    {
        // "log_reader" is the whole of one name and the head of another; a bare substring would be a
        // guess, so it is refused unless exactly one function could be meant.
        Assert.Empty(Covers.Resolve(Cover("reader"), Functions));
        Assert.Equal([0], Covers.Resolve(Cover("Shape4area"), Functions));
    }

    private static CoverSpec Cover(string symbol) => new() { Symbol = symbol };

    private static CoverSpec Range(uint rva, uint size) => new() { Rva = rva, Size = size };
}
