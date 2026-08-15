using System.Buffers.Binary;
using System.Text;

namespace Craterboy;

public sealed record RomHeader(
    string Title,
    byte CartridgeType,
    int RomSize,
    int RamSize,
    bool SupportsColor,
    bool RequiresColor,
    bool SupportsSuperGameBoy,
    bool HeaderChecksumValid,
    ushort GlobalChecksum)
{
    public static RomHeader Parse(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < 0x150)
            throw new ArgumentException("A Game Boy ROM must contain the complete 0x150-byte header.", nameof(rom));

        var titleEnd = rom[0x143] is 0x80 or 0xC0 ? 0x143 : 0x144;
        var titleBytes = rom[0x134..titleEnd];
        var nul = titleBytes.IndexOf((byte)0);
        if (nul >= 0) titleBytes = titleBytes[..nul];
        var title = Encoding.ASCII.GetString(titleBytes).TrimEnd();

        var romSize = rom[0x148] <= 8
            ? 32 * 1024 << rom[0x148]
            : rom[0x148] switch { 0x52 => 72 * 16 * 1024, 0x53 => 80 * 16 * 1024, 0x54 => 96 * 16 * 1024, _ => 0 };
        var ramSize = rom[0x149] switch
        {
            0 => 0, 1 => 2 * 1024, 2 => 8 * 1024, 3 => 32 * 1024,
            4 => 128 * 1024, 5 => 64 * 1024, _ => 0,
        };

        byte checksum = 0;
        for (var i = 0x134; i <= 0x14C; i++)
            checksum = unchecked((byte)(checksum - rom[i] - 1));

        var cgb = rom[0x143];
        return new(title, rom[0x147], romSize, ramSize, cgb is 0x80 or 0xC0,
            cgb == 0xC0, rom[0x146] == 3, checksum == rom[0x14D],
            BinaryPrimitives.ReadUInt16BigEndian(rom[0x14E..0x150]));
    }
}
