using Craterboy;
using Xunit;

namespace Craterboy.Core.Tests;

public sealed class KernelTests
{
    [Fact]
    public void HeaderParserValidatesChecksumAndMetadata()
    {
        var rom = MakeRom(type: 0, romSizeCode: 0, ramSizeCode: 0);
        "CRATERBOY"u8.CopyTo(rom.AsSpan(0x134));
        FixChecksum(rom);

        var header = RomHeader.Parse(rom);

        Assert.Equal("CRATERBOY", header.Title);
        Assert.Equal(32 * 1024, header.RomSize);
        Assert.True(header.HeaderChecksumValid);
    }

    [Fact]
    public void ResetUsesDocumentedDmgPostBootState()
    {
        var emulator = NewEmulator(MakeRom());
        var registers = emulator.Registers;

        Assert.Equal((ushort)0x100, registers.ProgramCounter);
        Assert.Equal((ushort)0xFFFE, registers.StackPointer);
        Assert.Equal((byte)0x01, registers.A);
        Assert.Equal((byte)0xB0, registers.F);
    }

    [Fact]
    public void CpuVerticalSliceExecutesLoadsStoreAndXor()
    {
        var rom = MakeRom();
        new byte[] { 0x21, 0x00, 0xC0, 0x3E, 0x42, 0x77, 0xAF }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);

        emulator.RunCycles(36);

        Assert.Equal((byte)0x42, emulator.PeekMemory(0xC000));
        Assert.Equal((byte)0, emulator.Registers.A);
        Assert.Equal((byte)CpuFlags.Zero, emulator.Registers.F);
        Assert.Equal(36, emulator.CycleCount);
    }

    [Fact]
    public void Mbc1SwitchesRomBanksAndProtectsDisabledRam()
    {
        var rom = MakeRom(type: 0x03, romSizeCode: 1, ramSizeCode: 2);
        rom[0x4000] = 1;
        rom[0x8000] = 2;
        var emulator = NewEmulator(rom);

        Assert.Equal((byte)1, emulator.PeekMemory(0x4000));
        emulator.WriteMemory(0x2000, 2);
        Assert.Equal((byte)2, emulator.PeekMemory(0x4000));
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xA000));
        emulator.WriteMemory(0, 0x0A);
        emulator.WriteMemory(0xA000, 0x5A);
        Assert.Equal((byte)0x5A, emulator.PeekMemory(0xA000));
        Assert.True(emulator.BatteryDirty);
    }

    [Fact]
    public void EchoRamMirrorsWorkRam()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0xC123, 0xA5);
        Assert.Equal((byte)0xA5, emulator.PeekMemory(0xE123));
    }

    private static Emulator NewEmulator(byte[] rom)
    {
        var emulator = new Emulator(GameBoyModel.DmgB);
        emulator.LoadRom(rom);
        return emulator;
    }

    private static byte[] MakeRom(byte type = 0, byte romSizeCode = 0, byte ramSizeCode = 0)
    {
        var size = 32 * 1024 << romSizeCode;
        var rom = new byte[size];
        rom[0x147] = type;
        rom[0x148] = romSizeCode;
        rom[0x149] = ramSizeCode;
        FixChecksum(rom);
        return rom;
    }

    private static void FixChecksum(byte[] rom)
    {
        byte checksum = 0;
        for (var i = 0x134; i <= 0x14C; i++)
            checksum = unchecked((byte)(checksum - rom[i] - 1));
        rom[0x14D] = checksum;
    }
}
