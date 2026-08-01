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
    public void PpuRaisesStatInterruptWhenAnEnabledSourceBecomesActive()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0xFF0F, 0);
        emulator.WriteMemory(0xFF45, 0); // current LY already matches
        emulator.WriteMemory(0xFF41, 0x40); // enable LYC after match
        Assert.Equal((byte)0x02, (byte)(emulator.PeekMemory(0xFF0F) & 0x02));

        emulator.WriteMemory(0xFF0F, 0);
        emulator.WriteMemory(0xFF41, 0x20); // mode 2 source, LCD is still off
        emulator.WriteMemory(0xFF40, 0x80); // enabling LCD enters mode 2
        Assert.Equal((byte)0x02, (byte)(emulator.PeekMemory(0xFF0F) & 0x02));
    }

    [Fact]
    public void PpuDisablingLcdResetsTimingAndBlanksTheFrame()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0x8000, 0x80);
        emulator.WriteMemory(0x9800, 0);
        emulator.WriteMemory(0xFF47, 0xE4);
        emulator.WriteMemory(0xFF40, 0x91); // LCD and background on
        emulator.RunCycles(252);

        var frame = new byte[160 * 144];
        emulator.CopyFrame(frame);
        Assert.Equal((byte)1, frame[0]);

        emulator.WriteMemory(0xFF40, 0);
        Assert.Equal((byte)0, emulator.PeekMemory(0xFF44));
        Assert.Equal((byte)0, (byte)(emulator.PeekMemory(0xFF41) & 0x03));
        emulator.CopyFrame(frame);
        Assert.All(frame, pixel => Assert.Equal((byte)0, pixel));
    }

    [Fact]
    public void ApuPowerGateControlsRegisterVisibilityAndReset()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0xFF12, 0xF3);
        Assert.Equal((byte)0, emulator.PeekMemory(0xFF12));
        Assert.Equal((byte)0, emulator.PeekMemory(0xFF26));

        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF12, 0xF3);
        Assert.Equal((byte)0xF3, emulator.PeekMemory(0xFF12));
        Assert.Equal((byte)0x80, emulator.PeekMemory(0xFF26));

        emulator.WriteMemory(0xFF26, 0);
        Assert.Equal((byte)0, emulator.PeekMemory(0xFF12));
        Assert.Equal((byte)0, emulator.PeekMemory(0xFF26));
    }

    [Fact]
    public void ApuChannelOneTriggerReportsStatusUntilLengthExpires()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF12, 0xF0); // DAC enabled
        emulator.WriteMemory(0xFF11, 0x3F); // length = 1 frame-sequencer tick
        emulator.WriteMemory(0xFF14, 0xC0); // length enable + trigger
        Assert.Equal((byte)0x81, emulator.PeekMemory(0xFF26));

        emulator.RunCycles(8191);
        Assert.Equal((byte)0x81, emulator.PeekMemory(0xFF26));
        emulator.RunCycles(1);
        Assert.Equal((byte)0x80, emulator.PeekMemory(0xFF26));
    }

    [Fact]
    public void ApuChannelOneEnvelopeUpdatesOnTheEnvelopeFrameStep()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF12, 0x59); // volume 5, increase every envelope tick
        emulator.WriteMemory(0xFF11, 0x00);
        emulator.WriteMemory(0xFF14, 0x80);
        Assert.Equal((byte)0x59, emulator.PeekMemory(0xFF12));

        emulator.RunCycles(7 * 8192);
        Assert.Equal((byte)0x59, emulator.PeekMemory(0xFF12));
        emulator.RunCycles(8192);
        Assert.Equal((byte)0x69, emulator.PeekMemory(0xFF12));
    }

    [Fact]
    public void ApuChannelOneSweepUpdatesFrequencyEveryFourFrameSteps()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF10, 0x11); // period 1, upward shift 1
        emulator.WriteMemory(0xFF12, 0xF0);
        emulator.WriteMemory(0xFF13, 100);
        emulator.WriteMemory(0xFF14, 0x80); // trigger at frequency 100

        emulator.RunCycles(3 * 8192);
        Assert.Equal((byte)100, emulator.PeekMemory(0xFF13));
        emulator.RunCycles(8192);
        Assert.Equal((byte)150, emulator.PeekMemory(0xFF13));
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
    public void PpuRendersBackgroundTilePixelsWithScrollAndDmgPalette()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0x8000, 0x80); // tile 0 row 0: color 1 at x=0
        emulator.WriteMemory(0x8001, 0x00);
        emulator.WriteMemory(0x9800, 0x00); // top-left map entry
        emulator.WriteMemory(0xFF47, 0xE4); // identity DMG palette
        emulator.WriteMemory(0xFF40, 0x91); // LCD on, BG on, unsigned tile data
        emulator.RunCycles(252); // end of visible line 0 / mode 3

        var frame = new byte[160 * 144];
        emulator.CopyFrame(frame);
        Assert.Equal((byte)1, frame[0]);
        Assert.Equal((byte)0, frame[1]);
    }

    [Fact]
    public void PpuWindowUsesIndependentMapAndWxWyPosition()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0x8000, 0x80); // tile 0: color 1 at x=0
        emulator.WriteMemory(0x8020, 0x00); // tile 2: color 2 at x=0
        emulator.WriteMemory(0x8021, 0x80);
        emulator.WriteMemory(0x9800, 0x00); // background map tile 0
        emulator.WriteMemory(0x9C00, 0x02); // window map tile 2
        emulator.WriteMemory(0xFF47, 0xE4);
        emulator.WriteMemory(0xFF4A, 0); // WY
        emulator.WriteMemory(0xFF4B, 7); // WX: window starts at x=0
        emulator.WriteMemory(0xFF40, 0xF1); // LCD, BG, window, window map, unsigned data
        emulator.RunCycles(252);

        var frame = new byte[160 * 144];
        emulator.CopyFrame(frame);
        Assert.Equal((byte)2, frame[0]);
        Assert.Equal((byte)0, frame[1]);
    }

    [Fact]
    public void PpuComposesTransparentAndPriorityAwareDmgSprites()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0x8000, 0x80); // sprite color 1 at x=0
        emulator.WriteMemory(0x8001, 0x00);
        emulator.WriteMemory(0xFE00, 16); // y=0
        emulator.WriteMemory(0xFE01, 8);  // x=0
        emulator.WriteMemory(0xFE02, 0);
        emulator.WriteMemory(0xFF48, 0x04); // OBP0 maps sprite color 1 to shade 1
        emulator.WriteMemory(0xFF40, 0x92); // LCD on, sprites on, BG off
        emulator.RunCycles(252);

        var frame = new byte[160 * 144];
        emulator.CopyFrame(frame);
        Assert.Equal((byte)1, frame[0]);
        Assert.Equal((byte)0, frame[1]);

        var priority = NewEmulator(rom);
        priority.WriteMemory(0x8000, 0x80);
        priority.WriteMemory(0xFE00, 16);
        priority.WriteMemory(0xFE01, 8);
        priority.WriteMemory(0xFE02, 0);
        priority.WriteMemory(0xFE03, 0x80); // sprite behind nonzero background
        priority.WriteMemory(0x9800, 0);
        priority.WriteMemory(0xFF47, 0xE4);
        priority.WriteMemory(0xFF40, 0x93); // BG and sprites on
        priority.RunCycles(252);
        priority.CopyFrame(frame);
        Assert.Equal((byte)1, frame[0]);
    }

    [Fact]
    public void PpuUsesBothTilesForDmgSixteenPixelSprites()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0x8000, 0x80); // top tile
        emulator.WriteMemory(0x8010, 0x80); // bottom tile
        emulator.WriteMemory(0xFE00, 16);
        emulator.WriteMemory(0xFE01, 8);
        emulator.WriteMemory(0xFE02, 1); // odd tile index is normalized to 0/1
        emulator.WriteMemory(0xFF48, 0x04);
        emulator.WriteMemory(0xFF40, 0x96); // LCD, 8x16 sprites, BG off
        emulator.RunCycles(8 * 456 + 252);

        var frame = new byte[160 * 144];
        emulator.CopyFrame(frame);
        Assert.Equal((byte)1, frame[0]);
        Assert.Equal((byte)1, frame[8 * 160]);
    }

    [Fact]
    public void PpuOrdersOverlappingSpritesByScreenXThenOamIndex()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0x8000, 0x80); // color 1 at pixel 0
        emulator.WriteMemory(0x8010, 0xC0); // colors at pixels 0 and 1
        emulator.WriteMemory(0xFE00, 16);
        emulator.WriteMemory(0xFE01, 9); // earlier OAM sprite, higher X loses overlap
        emulator.WriteMemory(0xFE02, 0);
        emulator.WriteMemory(0xFE04, 16);
        emulator.WriteMemory(0xFE05, 8); // later OAM sprite, lower X wins
        emulator.WriteMemory(0xFE06, 1);
        emulator.WriteMemory(0xFE07, 0x10); // use OBP1 to distinguish winner
        emulator.WriteMemory(0xFF48, 0x04);
        emulator.WriteMemory(0xFF49, 0x08);
        emulator.WriteMemory(0xFF40, 0x92); // LCD, sprites on, BG off
        emulator.RunCycles(252);

        var frame = new byte[160 * 144];
        emulator.CopyFrame(frame);
        Assert.Equal((byte)2, frame[1]);
    }

    [Fact]
    public void PpuBlocksCpuVramAndOamAccessDuringTransferModes()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0x8000, 0x12);
        emulator.WriteMemory(0xFE00, 0x34);
        emulator.WriteMemory(0xFF40, 0x80); // LCD on: mode 2

        Assert.Equal((byte)0x12, emulator.ReadMemory(0x8000));
        Assert.Equal((byte)0xFF, emulator.ReadMemory(0xFE00));
        emulator.WriteMemory(0x8000, 0x78); // VRAM is writable in mode 2
        emulator.WriteMemory(0xFE00, 0x56); // OAM is blocked in mode 2

        emulator.RunCycles(80); // mode 3
        Assert.Equal((byte)0xFF, emulator.ReadMemory(0x8000));
        Assert.Equal((byte)0xFF, emulator.ReadMemory(0xFE00));
        emulator.WriteMemory(0x8000, 0x9A);
        emulator.WriteMemory(0xFE00, 0xBC);

        emulator.RunCycles(172); // mode 0
        Assert.Equal((byte)0x78, emulator.ReadMemory(0x8000));
        Assert.Equal((byte)0x34, emulator.ReadMemory(0xFE00));
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
