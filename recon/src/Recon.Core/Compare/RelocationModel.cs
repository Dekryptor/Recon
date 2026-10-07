using Recon.Inventory;

namespace Recon.Compare;

/// <summary>
/// The one place that knows what a container's relocation kinds mean. Plan section 3.1 asks for the
/// address-bearing part of an instruction to have a single internal form: a PE <c>HIGHLOW</c>, an ELF
/// <c>R_386_32</c> and a Mach-O <c>X86_64_RELOC_UNSIGNED</c> are the same thing to the compare engine,
/// and this table is where that is decided. Adding a container means adding its names here, never
/// touching the engine.
/// </summary>
public static class RelocationModel
{
    /// <summary>The abstract class of one container relocation kind.</summary>
    public static ReferenceClass Classify(string kind) => kind switch
    {
        // PE32/PE32+ base relocations: the operand holds the target's address.
        "HIGHLOW" or "DIR64" or "HIGH" or "LOW" or "HIGHADJ" => ReferenceClass.Absolute,

        // PE ABSOLUTE is padding between fixups, not an address at all.
        "ABSOLUTE" => ReferenceClass.Unknown,

        // ELF i386/amd64: an absolute word, a PC-relative displacement, or the GOT/PLT slot that
        // stands in for an import.
        "R_386_32" or "R_386_RELATIVE" or "R_X86_64_64" or "R_X86_64_RELATIVE" => ReferenceClass.Absolute,
        "R_386_PC32" or "R_386_PLT32" or "R_X86_64_PC32" or "R_X86_64_PLT32" => ReferenceClass.Relative,
        "R_386_GOT32" or "R_386_GOTOFF" or "R_386_GOTPC" or "R_X86_64_GOTPCREL" => ReferenceClass.Import,

        // Mach-O: a plain pointer, a signed displacement, a branch, or a GOT slot.
        "X86_64_RELOC_UNSIGNED" or "GENERIC_RELOC_VANILLA" => ReferenceClass.Absolute,
        "X86_64_RELOC_SIGNED" or "X86_64_RELOC_SIGNED_1" or "X86_64_RELOC_SIGNED_2"
            or "X86_64_RELOC_SIGNED_4" or "X86_64_RELOC_BRANCH" => ReferenceClass.Relative,
        "X86_64_RELOC_GOT" or "X86_64_RELOC_GOT_LOAD" or "GENERIC_RELOC_PB_LA_PTR" => ReferenceClass.Import,

        _ => ReferenceClass.Unknown,
    };

    /// <summary>The class of a relocation the inventory recorded.</summary>
    public static ReferenceClass Classify(RelocationInfo relocation) => Classify(relocation.Kind);

    /// <summary>
    /// True when the class is an address the rebuild is free to move: the difference is then the
    /// toolchain's, not the reconstruction's, and the comparison must not report it.
    /// </summary>
    public static bool IsAddress(ReferenceClass @class)
        => @class is ReferenceClass.Absolute or ReferenceClass.Import or ReferenceClass.JumpTable;
}
