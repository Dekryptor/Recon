using Recon.Pe;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// The calling convention and signature a name carries is evidence the inventory reports per
/// function, so the decoder is tested against real compiler output. Every MSVC expectation here was
/// checked against <c>llvm-undname</c> on symbols produced by
/// <c>clang --target=i686-pc-windows-msvc</c>; the recorded text is this tool's presentation
/// (signature without the return type, which is what the inventory shows).
/// </summary>
public class DemanglerTests
{
    [Theory]
    [InlineData("add", "add", "cdecl")]
    [InlineData("_add", "add", "cdecl")]
    [InlineData("_mul_std@8", "mul_std", "stdcall")]
    [InlineData("@sub_fast@8", "sub_fast", "fastcall")]
    public void Decodes_c_decorations(string symbol, string text, string convention)
    {
        var result = Demangler.Demangle(symbol);

        Assert.Equal(text, result.Text);
        Assert.Equal(convention, result.CallingConvention);
        Assert.Equal("c", result.Scheme);
    }

    [Fact]
    public void Leaves_a_plain_c_name_alone()
    {
        var result = Demangler.Demangle("fprintf.constprop.0");

        Assert.Equal("fprintf.constprop.0", result.Text);
        Assert.Equal("cdecl", result.CallingConvention);
    }

    [Theory]
    // Free functions: Y plus the convention letter (llvm-undname: "int __cdecl f(int)").
    [InlineData("?f@@YAHH@Z", "f(int)", "cdecl")]
    [InlineData("?f@@YGXXZ", "f()", "stdcall")]
    [InlineData("?f@@YIXXZ", "f()", "fastcall")]
    // Members: access, cv-qualifier, convention (llvm-undname: "public: int __thiscall C::gpub(int) const").
    [InlineData("?gpub@C@@QBEHH@Z", "C::gpub(int)", "thiscall")]
    [InlineData("?e@C@@AAEXXZ", "C::e()", "thiscall")]
    [InlineData("?virt@C@@UAEXXZ", "C::virt()", "thiscall")]
    // Static members carry no cv letter (llvm-undname: "public: static void __stdcall C::g(struct C *)").
    [InlineData("?stat@C@@SAXH@Z", "C::stat(int)", "cdecl")]
    [InlineData("?g@C@@SGXPAU1@@Z", "C::g(<type 1> *)", "stdcall")] // a back-reference: named but unresolved
    public void Decodes_msvc_function_names(string symbol, string text, string convention)
    {
        var result = Demangler.Demangle(symbol);

        Assert.Equal("msvc", result.Scheme);
        Assert.Equal(text, result.Text);
        Assert.Equal(convention, result.CallingConvention);
    }

    [Theory]
    [InlineData("?t1@@YAHPAH@Z", "t1(int *)")]
    [InlineData("?t2@@YAHPBH@Z", "t2(int const *)")]
    [InlineData("?t3@@YAHPCH@Z", "t3(int volatile *)")]
    [InlineData("?t4@@YAHAAH@Z", "t4(int &)")]
    [InlineData("?t5@@YAHABH@Z", "t5(int const &)")]
    [InlineData("?t6@@YAHPAPAH@Z", "t6(int * *)")]
    [InlineData("?t7@@YAHPAD@Z", "t7(char *)")]
    [InlineData("?t9@@YAHPAUS@@@Z", "t9(struct S *)")]
    [InlineData("?t11@@YAH_J@Z", "t11(__int64)")]
    [InlineData("?t14@@YAHQAH@Z", "t14(int *const)")]
    [InlineData("?t18@@YAHE@Z", "t18(unsigned char)")]
    [InlineData("?t20@@YAHM@Z", "t20(float)")]
    [InlineData("?t21@@YAHPAPBH@Z", "t21(int const * *)")]
    public void Decodes_msvc_argument_types(string symbol, string text)
    {
        var result = Demangler.Demangle(symbol);

        Assert.Equal(text, result.Text);
        Assert.False(result.Partial);
    }

    [Theory]
    // llvm-undname: "public: __thiscall Circle::Circle(int)" / "public: __thiscall NS::C::~C(void)".
    [InlineData("??0Circle@@QAE@H@Z", "Circle::Circle(int)", "thiscall")]
    [InlineData("??0C@NS@@QAE@XZ", "NS::C::C()", "thiscall")]
    [InlineData("??1C@NS@@QAE@XZ", "NS::C::~C()", "thiscall")]
    [InlineData("??1Circle@@UAE@XZ", "Circle::~Circle()", "thiscall")]
    // llvm-undname: "public: virtual void * __thiscall Circle::`scalar deleting dtor'(unsigned int)".
    [InlineData("??_GCircle@@UAEPAXI@Z", "Circle::`scalar deleting destructor'(unsigned int)", "thiscall")]
    [InlineData("??_ECircle@@UAEPAXI@Z", "Circle::`vector deleting destructor'(unsigned int)", "thiscall")]
    public void Gives_readable_names_to_compiler_generated_symbols(string symbol, string text, string convention)
    {
        var result = Demangler.Demangle(symbol);

        Assert.Equal(text, result.Text);
        Assert.Equal(convention, result.CallingConvention);
        Assert.True(result.IsMemberFunction);
    }

    [Theory]
    [InlineData("??_7C@@6B@", "C::`vftable'")]
    [InlineData("??_R0?AUC@@@8", "C::`RTTI Type Descriptor'")]
    [InlineData("??_R4C@@6B@", "C::`RTTI Complete Object Locator'")]
    public void Names_the_records_that_describe_classes(string symbol, string text)
    {
        var result = Demangler.Demangle(symbol);

        Assert.Equal(text, result.Text);
    }

    [Fact]
    public void Marks_vtables_and_rtti_as_data()
    {
        Assert.Equal(SymbolKind.Data, Demangler.Demangle("??_7C@@6B@").Kind);
        Assert.Equal(SymbolKind.Data, Demangler.Demangle("??_R3C@@8").Kind);
        Assert.Equal(SymbolKind.Function, Demangler.Demangle("??0C@@QAE@XZ").Kind);
        Assert.Equal(SymbolKind.Function, Demangler.Demangle("??_GCircle@@UAEPAXI@Z").Kind);
    }

    [Fact]
    public void Says_so_rather_than_guessing_when_it_cannot_decode()
    {
        // A function pointer argument uses constructs this decoder does not implement.
        var result = Demangler.Demangle("?t22@@YAHP6AHH@Z@Z");

        Assert.True(result.Partial);
        Assert.Equal("t22()", result.Text);
    }

    [Fact]
    public void Decodes_itanium_parameter_types()
    {
        var result = Demangler.Demangle("_Z3addii");

        Assert.Equal("itanium", result.Scheme);
        Assert.Equal("add(int, int)", result.Text);
    }

    [Theory]
    [InlineData("__real@3f800000")]
    [InlineData("_?g_table@@3PAHA")]
    [InlineData("?_g_table@@3PAHA")]
    public void Recognises_data_symbols(string symbol)
    {
        Assert.Equal(SymbolKind.Data, Demangler.Demangle(symbol).Kind);
    }
}
