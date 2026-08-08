using System.Buffers.Binary;
using System.Text;

namespace Craterboy;

public readonly record struct BessBlock(string Identifier, ReadOnlyMemory<byte> Payload);
public readonly record struct BessBufferDescriptor(uint Size, uint Offset);

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
        var core = Read(source).FirstOrDefault(block => block.Identifier == "CORE");
        if (core.Identifier is null)
            throw new InvalidDataException("BESS CORE block is missing.");
        return ParseCore(core.Payload.Span);
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

    private static BessBufferDescriptor ReadDescriptor(ReadOnlySpan<byte> payload, int offset) =>
        new(BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[(offset + 4)..]));
}
