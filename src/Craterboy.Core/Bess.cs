using System.Buffers.Binary;
using System.Text;

namespace Craterboy;

public readonly record struct BessBlock(string Identifier, ReadOnlyMemory<byte> Payload);

public static class BessReader
{
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
}
