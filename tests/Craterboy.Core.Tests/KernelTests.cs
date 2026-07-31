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

    [Fact]
    public void SchedulerAdvancesDividerOnlyThroughEmulatedCycles()
    {
        var emulator = NewEmulator(MakeRom());

        Assert.Equal((byte)0, emulator.PeekMemory(0xFF04));
        emulator.RunCycles(255);
        Assert.Equal((byte)0, emulator.PeekMemory(0xFF04));
        emulator.RunCycles(1);
        Assert.Equal((byte)1, emulator.PeekMemory(0xFF04));
    }

    [Fact]
    public void TimerFallsOnSelectedDividerBitAndRequestsInterruptOnOverflow()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0xFF06, 0x3C);
        emulator.WriteMemory(0xFF05, 0xFF);
        emulator.WriteMemory(0xFF07, 0x05); // enable, divider bit 3 (16 T-cycles)

        emulator.RunCycles(16);

        Assert.Equal((byte)0x3C, emulator.PeekMemory(0xFF05));
        Assert.Equal((byte)0x04, (byte)(emulator.PeekMemory(0xFF0F) & 0x04));
    }

    [Fact]
    public void OamDmaCopiesOneHundredSixtyBytesInSixHundredFortyTCycles()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        for (var i = 0; i < 0xA0; i++) emulator.WriteMemory((ushort)(0xC000 + i), (byte)(i ^ 0x5A));

        emulator.WriteMemory(0xFF46, 0xC0);
        Assert.Equal((byte)0, emulator.PeekMemory(0xFE00));
        emulator.RunCycles(639);
        Assert.Equal((byte)0, emulator.PeekMemory(0xFE9F));
        emulator.RunCycles(1);

        for (var i = 0; i < 0xA0; i++)
            Assert.Equal((byte)(i ^ 0x5A), emulator.PeekMemory((ushort)(0xFE00 + i)));
    }

    [Fact]
    public void SerialEndpointCompletesInternalClockTransferAndRequestsInterrupt()
    {
        var endpoint = new TestSerialEndpoint { Response = 0x3C };
        var emulator = new Emulator(GameBoyModel.DmgB, new EmulatorOptions { SerialEndpoint = endpoint });
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        emulator.LoadRom(rom);
        emulator.WriteMemory(0xFF01, 0xA5);
        emulator.WriteMemory(0xFF02, 0x81);
        emulator.RunCycles(4095);
        Assert.Equal((byte)0xA5, emulator.PeekMemory(0xFF01));
        emulator.RunCycles(1);

        Assert.Equal((byte)0x3C, emulator.PeekMemory(0xFF01));
        Assert.Equal((byte)0, (byte)(emulator.PeekMemory(0xFF02) & 0x80));
        Assert.Equal((byte)0x08, (byte)(emulator.PeekMemory(0xFF0F) & 0x08));
        Assert.Equal((byte)0xA5, endpoint.Outgoing);
    }

    [Fact]
    public void JoypadSelectionAndButtonPressUseActiveLowLines()
    {
        var emulator = NewEmulator(MakeRom());
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF00));

        emulator.WriteMemory(0xFF00, 0x10); // select action buttons
        emulator.SetButtonState(GameBoyButton.A, true);

        Assert.Equal((byte)0xDE, emulator.PeekMemory(0xFF00));
        Assert.Equal((byte)0x10, (byte)(emulator.PeekMemory(0xFF0F) & 0x10));
        emulator.SetButtonState(GameBoyButton.A, false);
        Assert.Equal((byte)0xDF, emulator.PeekMemory(0xFF00));
    }

    [Fact]
    public void JoypadRejectsNonPrimaryPlayersUntilSgbSupportExists()
    {
        var emulator = NewEmulator(MakeRom());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            emulator.SetButtonState(GameBoyButton.Start, true, player: 1));
    }

    [Fact]
    public void PpuCyclesThroughDmgVisibleLineModesAndIncrementsLy()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFF40, 0x80);

        Assert.Equal((byte)2, (byte)(emulator.PeekMemory(0xFF41) & 0x03));
        emulator.RunCycles(80);
        Assert.Equal((byte)3, (byte)(emulator.PeekMemory(0xFF41) & 0x03));
        emulator.RunCycles(172);
        Assert.Equal((byte)0, (byte)(emulator.PeekMemory(0xFF41) & 0x03));
        emulator.RunCycles(204);
        Assert.Equal((byte)1, emulator.PeekMemory(0xFF44));
        Assert.Equal((byte)2, (byte)(emulator.PeekMemory(0xFF41) & 0x03));
    }

    [Fact]
    public void PpuRaisesStatInterruptsForLyCompareAndVblank()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFF41, 0x40); // LYC interrupt
        emulator.WriteMemory(0xFF45, 1);
        emulator.WriteMemory(0xFF40, 0x80);
        emulator.WriteMemory(0xFF0F, 0);
        emulator.RunCycles(456);
        Assert.Equal((byte)0x02, (byte)(emulator.PeekMemory(0xFF0F) & 0x02));

        emulator.WriteMemory(0xFF41, 0x10); // VBlank STAT interrupt
        emulator.WriteMemory(0xFF0F, 0);
        emulator.RunCycles(456 * 143);
        Assert.Equal((byte)144, emulator.PeekMemory(0xFF44));
        Assert.Equal((byte)0x02, (byte)(emulator.PeekMemory(0xFF0F) & 0x02));
    }

    [Fact]
    public void RawFrameBufferHasStableManagedSize()
    {
        var emulator = NewEmulator(MakeRom());
        var frame = new byte[160 * 144];
        emulator.CopyFrame(frame);
        Assert.All(frame, pixel => Assert.Equal((byte)0, pixel));
        Assert.Throws<ArgumentException>(() => emulator.CopyFrame(new byte[10]));
    }

    [Fact]
    public void Mbc3RtcLatchesDeterministicallyAndPersistsThroughStreams()
    {
        var clock = new TestTimeProvider();
        var rom = MakeRom(type: 0x10, romSizeCode: 1, ramSizeCode: 3);
        var emulator = new Emulator(GameBoyModel.DmgB, new EmulatorOptions { TimeProvider = clock });
        emulator.LoadRom(rom);
        emulator.WriteMemory(0, 0x0A);
        emulator.WriteMemory(0x4000, 0x08);
        emulator.WriteMemory(0x6000, 0);
        emulator.WriteMemory(0x6000, 1);
        Assert.Equal((byte)0, emulator.PeekMemory(0xA000));

        clock.Advance(TimeSpan.FromSeconds(65));
        emulator.WriteMemory(0x6000, 0);
        emulator.WriteMemory(0x6000, 1);
        Assert.Equal((byte)5, emulator.PeekMemory(0xA000));
        emulator.WriteMemory(0x4000, 0x09);
        Assert.Equal((byte)1, emulator.PeekMemory(0xA000));

        using var battery = new MemoryStream();
        emulator.SaveBattery(battery);
        Assert.Equal(32 * 1024 + 5, battery.Length);
        battery.Position = 0;
        var restored = new Emulator(GameBoyModel.DmgB, new EmulatorOptions { TimeProvider = clock });
        restored.LoadRom(rom);
        restored.LoadBattery(battery);
        restored.WriteMemory(0, 0x0A);
        restored.WriteMemory(0x4000, 0x08);
        restored.WriteMemory(0x6000, 0);
        restored.WriteMemory(0x6000, 1);
        Assert.Equal((byte)5, restored.PeekMemory(0xA000));
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

    private sealed class TestTimeProvider : ITimeProvider
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
        public void Advance(TimeSpan elapsed) => UtcNow += elapsed;
    }

    private sealed class TestSerialEndpoint : ISerialEndpoint
    {
        public byte Response { get; init; }
        public byte Outgoing { get; private set; }
        public byte Exchange(byte outgoing) { Outgoing = outgoing; return Response; }
    }
}
