using System.Buffers.Binary;
using System.Text;

namespace Craterboy;

public readonly record struct BessBlock(string Identifier, ReadOnlyMemory<byte> Payload);
public readonly record struct BessBufferDescriptor(uint Size, uint Offset);
public readonly record struct BessInfo(ReadOnlyMemory<byte> Title, ushort GlobalChecksum);
public readonly record struct BessMbcWrite(ushort Address, byte Value);
public readonly record struct BessRtc(
    byte Seconds,
    byte Minutes,
    byte Hours,
    byte Days,
    byte High,
    byte LatchedSeconds,
    byte LatchedMinutes,
    byte LatchedHours,
    byte LatchedDays,
    byte LatchedHigh,
    ulong LastUnixSecond);
public readonly record struct BessMbc7(
    byte Flags,
    byte ArgumentBitsLeft,
    ushort EepromCommand,
    ushort PendingReadBits,
    ushort LatchedGyroX,
    ushort LatchedGyroY);
public readonly record struct BessHuc3(
    ulong LastUnixSecond,
    ushort Minutes,
    ushort Days,
    ushort AlarmMinutes,
    ushort AlarmDays,
    bool AlarmEnabled);
public readonly record struct BessTpp1(
    ulong LastUnixSecond,
    ReadOnlyMemory<byte> RealRtcData,
    ReadOnlyMemory<byte> LatchedRtcData,
    byte Mr4);
public readonly record struct BessSgb(
    BessBufferDescriptor BorderTiles,
    BessBufferDescriptor BorderTilemap,
    BessBufferDescriptor BorderPalettes,
    BessBufferDescriptor ActivePalettes,
    BessBufferDescriptor RamPalettes,
    BessBufferDescriptor AttributeMap,
    BessBufferDescriptor AttributeFiles,
    byte MultiplayerState);

public readonly record struct BessCore(
    ushort MajorVersion,
    ushort MinorVersion,
    string ModelIdentifier,
    ushort Pc,
    ushort Af,
    ushort Bc,
    ushort De,
    ushort Hl,
    ushort Sp,
    bool Ime,
    byte Ie,
    byte ExecutionMode,
    ReadOnlyMemory<byte> IoRegisters,
    BessBufferDescriptor Ram,
    BessBufferDescriptor Vram,
    BessBufferDescriptor MbcRam,
    BessBufferDescriptor Oam,
    BessBufferDescriptor Hram,
    BessBufferDescriptor BackgroundPalettes,
    BessBufferDescriptor ObjectPalettes);

public static class BessWriter
{
    public static void Write(Stream destination, IReadOnlyList<BessBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(blocks);
        if (!destination.CanSeek)
            throw new NotSupportedException("BESS writing requires a seekable destination.");
        if (blocks.Count == 0)
            throw new ArgumentException("At least one BESS block is required.", nameof(blocks));

        ValidateBlocks(blocks);
        if (destination.Position is < 0 or > uint.MaxValue)
            throw new InvalidOperationException("BESS block offset exceeds the format limit.");
        var blockOffset = checked((uint)destination.Position);
        foreach (var block in blocks)
            WriteBlock(destination, block.Identifier, block.Payload.Span);
        WriteBlock(destination, "END ", ReadOnlySpan<byte>.Empty);

        Span<byte> footer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(footer, blockOffset);
        "BESS"u8.CopyTo(footer[4..]);
        destination.Write(footer);
    }

    private static void ValidateBlocks(IReadOnlyList<BessBlock> blocks)
    {
        var foundCore = false;
        foreach (var block in blocks)
        {
            if (block.Identifier.Length != 4 || block.Identifier.Any(character => character > 0x7F))
                throw new ArgumentException("BESS block identifiers must contain four ASCII characters.", nameof(blocks));
            if (block.Identifier == "END ")
                throw new ArgumentException("BESS END is written automatically.", nameof(blocks));
            if (block.Identifier == "CORE")
            {
                if (foundCore) throw new ArgumentException("BESS contains duplicate CORE blocks.", nameof(blocks));
                foundCore = true;
            }
            else if (block.Identifier is "NAME" or "INFO")
            {
                if (foundCore) throw new ArgumentException($"BESS {block.Identifier} must precede CORE.", nameof(blocks));
            }
            else if (block.Identifier is "XOAM" or "MBC " or "RTC " or "HUC3" or "MBC7" or "TPP1" or "SGB ")
            {
                if (!foundCore) throw new ArgumentException($"BESS {block.Identifier} must follow CORE.", nameof(blocks));
            }
            if ((ulong)block.Payload.Length > uint.MaxValue)
                throw new ArgumentException("BESS block payload is too large.", nameof(blocks));
        }
        if (!foundCore)
            throw new ArgumentException("BESS CORE block is required.", nameof(blocks));
    }

    private static void WriteBlock(Stream destination, string identifier, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[8];
        Encoding.ASCII.GetBytes(identifier).CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)payload.Length));
        destination.Write(header);
        destination.Write(payload);
    }
}

public static class BessReader
{
    private const int CoreMinimumLength = 0xD0;

    public static IReadOnlyList<BessBlock> Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        var data = buffer.ToArray();
        if (data.Length < 12 || !data.AsSpan(data.Length - 4, 4).SequenceEqual("BESS"u8))
            throw new InvalidDataException("BESS footer is missing or invalid.");

        var footerOffset = data.Length - 8;
        var blockOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(footerOffset, 4));
        if (blockOffset > footerOffset)
            throw new InvalidDataException("BESS block offset is outside the file.");

        var blocks = new List<BessBlock>();
        var position = checked((int)blockOffset);
        var foundCore = false;
        while (position < footerOffset)
        {
            if (footerOffset - position < 8)
                throw new InvalidDataException("BESS block header is truncated.");

            var identifierBytes = data.AsSpan(position, 4);
            if (identifierBytes.IndexOfAnyInRange((byte)0x80, byte.MaxValue) >= 0)
                throw new InvalidDataException("BESS block identifier is not ASCII.");
            var identifier = Encoding.ASCII.GetString(identifierBytes);
            var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 4, 4));
            position += 8;
            if (payloadLength > footerOffset - position)
                throw new InvalidDataException("BESS block extends beyond the footer.");

            var payload = data.AsMemory(position, checked((int)payloadLength));
            switch (identifier)
            {
                case "CORE":
                    if (foundCore) throw new InvalidDataException("BESS contains duplicate CORE blocks.");
                    foundCore = true;
                    break;
                case "NAME":
                case "INFO":
                    if (foundCore) throw new InvalidDataException($"BESS {identifier} block appears after CORE.");
                    break;
                case "XOAM":
                case "MBC ":
                case "RTC ":
                case "HUC3":
                case "MBC7":
                case "TPP1":
                case "SGB ":
                    if (!foundCore) throw new InvalidDataException($"BESS {identifier} block appears before CORE.");
                    break;
                case "END ":
                    if (!foundCore) throw new InvalidDataException("BESS END block appears before CORE.");
                    if (payloadLength != 0) throw new InvalidDataException("BESS END block must be empty.");
                    position += (int)payloadLength;
                    if (position != footerOffset)
                        throw new InvalidDataException("BESS END block is not the final block.");
                    blocks.Add(new BessBlock(identifier, payload));
                    return blocks;
            }

            blocks.Add(new BessBlock(identifier, payload));
            position += checked((int)payloadLength);
        }

        throw new InvalidDataException("BESS END block is missing.");
    }

    public static BessCore ReadCore(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        var data = buffer.ToArray();
        var core = Read(new MemoryStream(data)).FirstOrDefault(block => block.Identifier == "CORE");
        if (core.Identifier is null)
            throw new InvalidDataException("BESS CORE block is missing.");
        var parsed = ParseCore(core.Payload.Span);
        ValidateDescriptor(parsed.Ram, data.Length, "RAM");
        ValidateDescriptor(parsed.Vram, data.Length, "VRAM");
        ValidateDescriptor(parsed.MbcRam, data.Length, "MBC RAM");
        ValidateDescriptor(parsed.Oam, data.Length, "OAM");
        ValidateDescriptor(parsed.Hram, data.Length, "HRAM");
        ValidateDescriptor(parsed.BackgroundPalettes, data.Length, "background palettes");
        ValidateDescriptor(parsed.ObjectPalettes, data.Length, "object palettes");
        return parsed;
    }

    public static byte[] ReadBuffer(Stream source, BessBufferDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        var data = buffer.ToArray();
        _ = Read(new MemoryStream(data));
        ValidateDescriptor(descriptor, data.Length, "requested");
        return data.AsSpan(checked((int)descriptor.Offset), checked((int)descriptor.Size)).ToArray();
    }

    public static BessInfo? ReadInfo(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var info = Read(source).FirstOrDefault(block => block.Identifier == "INFO");
        return info.Identifier is null ? null : ParseInfo(info.Payload.Span);
    }

    public static string? ReadName(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var name = Read(source).FirstOrDefault(block => block.Identifier == "NAME");
        return name.Identifier is null ? null : ParseName(name.Payload.Span);
    }

    public static IReadOnlyList<BessMbcWrite>? ReadMbc(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var mbc = Read(source).FirstOrDefault(block => block.Identifier == "MBC ");
        return mbc.Identifier is null ? null : ParseMbc(mbc.Payload.Span);
    }

    public static BessRtc? ReadRtc(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var rtc = Read(source).FirstOrDefault(block => block.Identifier == "RTC ");
        return rtc.Identifier is null ? null : ParseRtc(rtc.Payload.Span);
    }

    public static byte[]? ReadExtraOam(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var xoam = Read(source).FirstOrDefault(block => block.Identifier == "XOAM");
        return xoam.Identifier is null ? null : ParseExtraOam(xoam.Payload.Span);
    }

    public static BessMbc7? ReadMbc7(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var mbc7 = Read(source).FirstOrDefault(block => block.Identifier == "MBC7");
        return mbc7.Identifier is null ? null : ParseMbc7(mbc7.Payload.Span);
    }

    public static BessHuc3? ReadHuc3(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var huc3 = Read(source).FirstOrDefault(block => block.Identifier == "HUC3");
        return huc3.Identifier is null ? null : ParseHuc3(huc3.Payload.Span);
    }

    public static BessTpp1? ReadTpp1(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var tpp1 = Read(source).FirstOrDefault(block => block.Identifier == "TPP1");
        return tpp1.Identifier is null ? null : ParseTpp1(tpp1.Payload.Span);
    }

    public static BessSgb? ReadSgb(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var sgb = Read(source).FirstOrDefault(block => block.Identifier == "SGB ");
        return sgb.Identifier is null ? null : ParseSgb(sgb.Payload.Span);
    }

    private static BessCore ParseCore(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < CoreMinimumLength)
            throw new InvalidDataException("BESS CORE block is truncated.");

        var major = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        var minor = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
        if (major != 1) throw new InvalidDataException("BESS CORE major version is unsupported.");

        var modelBytes = payload.Slice(4, 4);
        if (modelBytes.IndexOfAnyInRange((byte)0x80, byte.MaxValue) >= 0 || modelBytes[3] != (byte)' ')
            throw new InvalidDataException("BESS CORE model identifier is invalid.");
        var model = Encoding.ASCII.GetString(modelBytes);
        if (model[0] is not ('G' or 'S' or 'C'))
            throw new InvalidDataException("BESS CORE model family is unsupported.");
        if (model[0] == 'G' && model[1] is not (' ' or 'D' or 'M') ||
            model[0] == 'S' && model[1] is not (' ' or 'N' or 'P' or '2') ||
            model[0] == 'C' && model[1] is not (' ' or 'C' or 'A'))
            throw new InvalidDataException("BESS CORE model identifier is invalid.");
        if (payload[0x17] != 0 || payload[0x16] > 2)
            throw new InvalidDataException("BESS CORE execution state is invalid.");

        return new BessCore(
            major,
            minor,
            model,
            BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[0x0A..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[0x0C..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[0x0E..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[0x10..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[0x12..]),
            payload[0x14] != 0,
            payload[0x15],
            payload[0x16],
            payload.Slice(0x18, 0x80).ToArray(),
            ReadDescriptor(payload, 0x98),
            ReadDescriptor(payload, 0xA0),
            ReadDescriptor(payload, 0xA8),
            ReadDescriptor(payload, 0xB0),
            ReadDescriptor(payload, 0xB8),
            ReadDescriptor(payload, 0xC0),
            ReadDescriptor(payload, 0xC8));
    }

    private static BessInfo ParseInfo(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 0x12)
            throw new InvalidDataException("BESS INFO block length is invalid.");
        return new BessInfo(payload[..0x10].ToArray(), BinaryPrimitives.ReadUInt16LittleEndian(payload[0x10..]));
    }

    private static string ParseName(ReadOnlySpan<byte> payload)
    {
        if (payload.IndexOfAnyInRange((byte)0x80, byte.MaxValue) >= 0)
            throw new InvalidDataException("BESS NAME block is not ASCII.");
        return Encoding.ASCII.GetString(payload);
    }

    private static IReadOnlyList<BessMbcWrite> ParseMbc(ReadOnlySpan<byte> payload)
    {
        if (payload.Length % 3 != 0 || payload.Length > 0x1000)
            throw new InvalidDataException("BESS MBC block length is invalid.");

        var writes = new BessMbcWrite[payload.Length / 3];
        for (var index = 0; index < writes.Length; index++)
        {
            var offset = index * 3;
            var address = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
            if (address > 0x7FFF && (address < 0xA000 || address > 0xBFFF))
                throw new InvalidDataException("BESS MBC block contains an invalid address.");
            writes[index] = new BessMbcWrite(address, payload[offset + 2]);
        }
        return writes;
    }

    private static BessRtc ParseRtc(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 0x30)
            throw new InvalidDataException("BESS RTC block length is invalid.");
        return new BessRtc(
            payload[0],
            payload[4],
            payload[8],
            payload[0x0C],
            payload[0x10],
            payload[0x14],
            payload[0x18],
            payload[0x1C],
            payload[0x20],
            payload[0x24],
            BinaryPrimitives.ReadUInt64LittleEndian(payload[0x28..]));
    }

    private static byte[] ParseExtraOam(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 0x60)
            throw new InvalidDataException("BESS XOAM block length is invalid.");
        return payload.ToArray();
    }

    private static BessMbc7 ParseMbc7(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 0x0A)
            throw new InvalidDataException("BESS MBC7 block length is invalid.");
        if ((payload[0] & 0xC0) != 0)
            throw new InvalidDataException("BESS MBC7 flags contain reserved bits.");
        return new BessMbc7(
            payload[0],
            payload[1],
            BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]));
    }

    private static BessHuc3 ParseHuc3(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 0x11)
            throw new InvalidDataException("BESS HUC3 block length is invalid.");
        if (payload[0x10] > 1)
            throw new InvalidDataException("BESS HUC3 alarm flag is invalid.");
        return new BessHuc3(
            BinaryPrimitives.ReadUInt64LittleEndian(payload),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[0x0A..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[0x0C..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[0x0E..]),
            payload[0x10] != 0);
    }

    private static BessTpp1 ParseTpp1(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 0x11)
            throw new InvalidDataException("BESS TPP1 block length is invalid.");
        return new BessTpp1(
            BinaryPrimitives.ReadUInt64LittleEndian(payload),
            payload.Slice(8, 4).ToArray(),
            payload.Slice(0x0C, 4).ToArray(),
            payload[0x10]);
    }

    private static BessSgb ParseSgb(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 0x39)
            throw new InvalidDataException("BESS SGB block is truncated.");
        var multiplayerState = payload[0x38];
        var playerCount = multiplayerState >> 4;
        var currentPlayer = multiplayerState & 0x0F;
        if (playerCount is not (1 or 2 or 4) || currentPlayer >= playerCount)
            throw new InvalidDataException("BESS SGB multiplayer state is invalid.");
        return new BessSgb(
            ReadDescriptor(payload, 0),
            ReadDescriptor(payload, 8),
            ReadDescriptor(payload, 0x10),
            ReadDescriptor(payload, 0x18),
            ReadDescriptor(payload, 0x20),
            ReadDescriptor(payload, 0x28),
            ReadDescriptor(payload, 0x30),
            multiplayerState);
    }

    private static BessBufferDescriptor ReadDescriptor(ReadOnlySpan<byte> payload, int offset) =>
        new(BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[(offset + 4)..]));

    private static void ValidateDescriptor(BessBufferDescriptor descriptor, int fileLength, string name)
    {
        if (descriptor.Size == 0)
        {
            if (descriptor.Offset != 0)
                throw new InvalidDataException($"BESS {name} buffer has an offset without data.");
            return;
        }

        if (descriptor.Offset > (uint)fileLength || descriptor.Size > (ulong)fileLength - descriptor.Offset)
            throw new InvalidDataException($"BESS {name} buffer extends outside the file.");
    }
}
