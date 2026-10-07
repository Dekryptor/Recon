using Recon.Elf;
using Recon.Macho;
using Recon.Pe;

namespace Recon.Images;

/// <summary>
/// Loads a binary without being told what it is. The format is read from the file's own magic
/// rather than from <c>project.toml</c>, because the two can disagree and the file is the one that
/// is right; the project's <c>format</c> is checked afterwards, by <c>recon verify</c>.
/// </summary>
public static class ImageLoader
{
    public static LoadedImage Load(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            return new LoadedImage { Problems = [$"cannot read {path}: {ex.Message}"] };
        }

        return LoadBytes(bytes, path);
    }

    public static LoadedImage LoadBytes(byte[] bytes, string path = "<memory>")
    {
        if (bytes.Length >= 4 && bytes[0] == 0x7F && bytes[1] == (byte)'E' && bytes[2] == (byte)'L' && bytes[3] == (byte)'F')
        {
            var elf = ElfLoader.LoadBytes(bytes, path);
            return new LoadedImage
            {
                Image = elf.Image is null ? null : new ElfBinaryImage(elf.Image, bytes),
                Problems = elf.Problems,
                Bytes = bytes,
                Format = elf.Image?.Format ?? "elf",
            };
        }

        // Mach-O: 0xFEEDFACF / 0xFEEDFACE, either byte order, or the fat container that holds one
        // of each. All four are in the first four bytes, which is all the sniffing there is to do.
        if (bytes.Length >= 28 && IsMacho(bytes))
        {
            var macho = MachoLoader.LoadBytes(bytes, path);
            return new LoadedImage
            {
                Image = macho.Image is null ? null : new MachoBinaryImage(macho.Image, bytes),
                Problems = macho.Problems,
                Bytes = bytes,
                Format = macho.Image?.Format ?? "macho",
            };
        }

        if (bytes.Length >= 0x40 && bytes[0] == 0x4D && bytes[1] == 0x5A)
        {
            var pe = PeLoader.LoadBytes(bytes, path);
            return new LoadedImage
            {
                Image = pe.Image is null ? null : new PeBinaryImage(pe.Image, bytes),
                Problems = pe.Problems,
                Bytes = bytes,
                Format = pe.Image is null ? "pe" : (pe.Image.Kind is PeKind.Pe32 ? "pe32" : "pe64"),
            };
        }

        // An archive is the one other thing that arrives in a reconstruction: the libraries beside
        // the binary, holding the objects a link pulls in. It is not a program and is not loaded as
        // one, so the message names the command that reads it rather than leaving the reader to guess.
        if (Recon.Archive.CoffArchive.Sniff(bytes))
        {
            return new LoadedImage
            {
                Problems = [$@"{path} is a COFF archive, not a program: `recon lib ""{path}""` lists its members"],
                Bytes = bytes,
                Format = "coff-archive",
            };
        }

        string magic = bytes.Length >= 2 ? $"0x{bytes[0]:X2}{bytes[1]:X2}" : "an empty file";
        return new LoadedImage
        {
            Problems = [$"cannot tell what {path} is: it starts with {magic}, which is neither ELF, MZ nor Mach-O"],
            Bytes = bytes,
        };
    }

    private static bool IsMacho(byte[] bytes)
    {
        uint magic = (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));
        return magic is 0xFEEDFACF or 0xFEEDFACE or 0xCFFAEDFE or 0xCEFAEDFE or 0xBEBAFECA or 0xBFBAFECA;
    }
}
