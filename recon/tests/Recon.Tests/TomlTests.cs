using Recon.Toml;
using Xunit;

namespace Recon.Tests;

public class TomlParserTests
{
    private static TomlTable Parse(string text) => TomlParser.Parse(text, "test.toml").Root;

    [Fact]
    public void Reads_scalars_with_their_types()
    {
        var root = Parse("""
            name = "sample"
            calls = 42
            ratio = 0.5
            enabled = true
            disabled = false
            when = 1979-05-27T07:32:00Z
            """);

        Assert.Equal("sample", ((TomlString)root.Get("name")!).Value);
        Assert.Equal(42, ((TomlInteger)root.Get("calls")!).Value);
        Assert.Equal(0.5, ((TomlFloat)root.Get("ratio")!).Value, 6);
        Assert.True(((TomlBoolean)root.Get("enabled")!).Value);
        Assert.False(((TomlBoolean)root.Get("disabled")!).Value);
        Assert.Equal(TomlKind.DateTime, root.Get("when")!.Kind);
    }

    [Fact]
    public void Reads_integers_in_every_base_and_with_separators()
    {
        var root = Parse("""
            hex = 0x401000
            octal = 0o755
            binary = 0b1010_1010
            separated = 1_000_000
            negative = -16
            """);

        Assert.Equal(0x401000, ((TomlInteger)root.Get("hex")!).Value);
        Assert.Equal(493, ((TomlInteger)root.Get("octal")!).Value);
        Assert.Equal(170, ((TomlInteger)root.Get("binary")!).Value);
        Assert.Equal(1_000_000, ((TomlInteger)root.Get("separated")!).Value);
        Assert.Equal(-16, ((TomlInteger)root.Get("negative")!).Value);
    }

    [Fact]
    public void Reads_tables_dotted_keys_and_arrays_of_tables()
    {
        var root = Parse("""
            paths.profiles = ["toolchains"]

            [project]
            name = "sample"

            [target]
            format = "pe32"
            arch = "x86"

            [[unit]]
            name = "main"
            source = "src/main.c"

            [[unit]]
            name = "second"
            source = "src/second.c"
            flags = ["-O2", "-Wall"]

            [unit.extra]
            note = "a child table of the last unit"
            """);

        var project = (TomlTable)root.Get("project")!;
        Assert.Equal("sample", ((TomlString)project.Get("name")!).Value);

        var units = (TomlArray)root.Get("unit")!;
        Assert.True(units.IsArrayOfTables);
        Assert.Equal(2, units.Count);
        var second = (TomlTable)units.Items[1];
        Assert.Equal("second", ((TomlString)second.Get("name")!).Value);
        Assert.Equal(["-O2", "-Wall"], ((TomlArray)second.Get("flags")!).Items.Select(i => ((TomlString)i).Value));
        Assert.Equal("a child table of the last unit", ((TomlString)((TomlTable)second.Get("extra")!).Get("note")!).Value);

        var paths = (TomlTable)root.Get("paths")!;
        Assert.Equal("toolchains", ((TomlString)((TomlArray)paths.Get("profiles")!).Items[0]).Value);
    }

    [Fact]
    public void Reads_inline_tables_and_nested_arrays()
    {
        var root = Parse("""
            env = { INCLUDE = "C:/sdk/include", LIB = "C:/sdk/lib" }
            matrix = [[1, 2], [3, 4]]
            """);

        var env = (TomlTable)root.Get("env")!;
        Assert.Equal("C:/sdk/include", ((TomlString)env.Get("INCLUDE")!).Value);

        var matrix = (TomlArray)root.Get("matrix")!;
        Assert.Equal(2, matrix.Count);
        Assert.Equal(3, ((TomlInteger)((TomlArray)matrix.Items[1]).Items[0]).Value);
    }

    [Fact]
    public void Reads_all_four_string_forms()
    {
        // The TOML text contains triple quotes itself, hence the four-quote C# delimiter.
        var root = Parse(""""
            basic = "tab\there \u0041"
            literal = 'C:\tools\msvc'
            multi = """
            line one
            line two"""
            multiLiteral = '''
            raw \n stays raw'''
            """");

        Assert.Equal("tab\there A", ((TomlString)root.Get("basic")!).Value);
        Assert.Equal(@"C:\tools\msvc", ((TomlString)root.Get("literal")!).Value);
        Assert.Equal("line one\nline two", ((TomlString)root.Get("multi")!).Value);
        Assert.Equal("raw \\n stays raw", ((TomlString)root.Get("multiLiteral")!).Value);
    }

    [Fact]
    public void Multi_line_strings_join_on_a_trailing_backslash()
    {
        var root = Parse(""""
            text = """
            first \
                second"""
            """");

        Assert.Equal("first second", ((TomlString)root.Get("text")!).Value);
    }

    [Fact]
    public void Comments_and_blank_lines_are_ignored()
    {
        var root = Parse("""
            # leading comment

            value = 1 # trailing comment

            # another
            other = 2
            """);

        Assert.Equal(1, ((TomlInteger)root.Get("value")!).Value);
        Assert.Equal(2, ((TomlInteger)root.Get("other")!).Value);
    }

    [Fact]
    public void Keys_keep_the_order_and_line_they_were_written_on()
    {
        var root = Parse("""
            alpha = 1
            beta = 2
            gamma = 3
            """);

        Assert.Equal(["alpha", "beta", "gamma"], root.KeyOrder);
        Assert.Equal(2, root.KeyLine("beta"));
        Assert.Equal(3, root.Get("gamma")!.Line);
    }

    [Fact]
    public void A_duplicate_key_is_an_error_that_names_the_first_definition()
    {
        var error = Assert.Throws<TomlParseException>(() => Parse("""
            value = 1
            value = 2
            """));

        Assert.Equal(2, error.Line);
        Assert.Contains("duplicate key value", error.Detail);
        Assert.Contains("line 1", error.Detail);
    }

    [Fact]
    public void A_table_cannot_be_defined_twice()
    {
        var error = Assert.Throws<TomlParseException>(() => Parse("""
            [target]
            format = "pe32"

            [target]
            arch = "x86"
            """));

        Assert.Contains("already defined", error.Detail);
    }

    [Fact]
    public void An_inline_table_cannot_be_extended()
    {
        var error = Assert.Throws<TomlParseException>(() => Parse("""
            env = { A = "1" }
            env.B = "2"
            """));

        Assert.Contains("inline table", error.Detail);
    }

    [Fact]
    public void A_value_cannot_be_used_as_a_table()
    {
        var error = Assert.Throws<TomlParseException>(() => Parse("""
            value = 1
            value.child = 2
            """));

        Assert.Contains("cannot use value as a table", error.Detail);
    }

    [Fact]
    public void Unterminated_strings_are_reported_with_a_position()
    {
        var error = Assert.Throws<TomlParseException>(() => Parse("name = \"unfinished"));
        Assert.Contains("not closed", error.Detail);
        Assert.Equal(1, error.Line);
    }

    [Fact]
    public void Unknown_escapes_are_rejected()
    {
        var error = Assert.Throws<TomlParseException>(() => Parse("path = \"C:\\qools\""));
        Assert.Contains("unknown escape", error.Detail);
    }

    [Fact]
    public void Two_statements_on_one_line_are_rejected()
    {
        var error = Assert.Throws<TomlParseException>(() => Parse("a = 1 b = 2"));
        Assert.Contains("one statement per line", error.Detail);
    }

    [Fact]
    public void Empty_documents_and_empty_tables_are_fine()
    {
        var empty = Parse(string.Empty);
        Assert.True(empty.IsEmpty);

        var table = Parse("[nothing]\n");
        Assert.True(((TomlTable)table.Get("nothing")!).IsEmpty);
    }

    [Fact]
    public void Arrays_allow_trailing_commas_and_comments()
    {
        var root = Parse("""
            flags = [
                "-O2",   # speed
                "-Wall",
            ]
            """);

        Assert.Equal(2, ((TomlArray)root.Get("flags")!).Count);
    }
}
