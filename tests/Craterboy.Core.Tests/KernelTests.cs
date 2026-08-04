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
    public void EnableInterruptsTakesEffectAfterTheFollowingInstruction()
    {
        var rom = MakeRom();
        new byte[] { 0xFB, 0x00 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);

        emulator.StepInstruction();
        Assert.False(emulator.Registers.InterruptMasterEnable);
        emulator.StepInstruction();
        Assert.True(emulator.Registers.InterruptMasterEnable);
    }

    [Fact]
    public void DisableInterruptsCancelsPendingEnable()
    {
        var rom = MakeRom();
        new byte[] { 0xFB, 0xF3, 0x00 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);

        emulator.StepInstruction();
        emulator.StepInstruction();
        emulator.StepInstruction();
        Assert.False(emulator.Registers.InterruptMasterEnable);
    }

    [Fact]
    public void StopConsumesPaddingByteAndHalts()
    {
        var rom = MakeRom();
        new byte[] { 0x10, 0x00 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);

        Assert.Equal(4, emulator.StepInstruction());
        Assert.Equal((ushort)0x102, emulator.Registers.ProgramCounter);
        Assert.True(emulator.Registers.Halted);
    }

    [Fact]
    public void PendingInterruptServicesHighestPriorityVector()
    {
        var rom = MakeRom();
        new byte[] { 0xFB, 0x00, 0x76 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.StepInstruction();
        emulator.StepInstruction();
        emulator.StepInstruction();
        Assert.True(emulator.Registers.InterruptMasterEnable);

        emulator.WriteMemory(0xFFFF, 0x1F);
        emulator.WriteMemory(0xFF0F, 0x1F);
        Assert.Equal(20, emulator.StepInstruction());
        Assert.Equal((ushort)0x0040, emulator.Registers.ProgramCounter);
        Assert.Equal((ushort)0xFFFC, emulator.Registers.StackPointer);
        Assert.Equal((byte)0x03, emulator.PeekMemory(0xFFFC));
        Assert.Equal((byte)0x01, emulator.PeekMemory(0xFFFD));
        Assert.Equal((byte)0x1E, (byte)(emulator.PeekMemory(0xFF0F) & 0x1F));
        Assert.False(emulator.Registers.InterruptMasterEnable);
        Assert.False(emulator.Registers.Halted);
    }

    [Fact]
    public void PendingInterruptWakesHaltedCpuWhenImeIsDisabled()
    {
        var rom = MakeRom();
        new byte[] { 0x76, 0x00 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.StepInstruction();
        emulator.WriteMemory(0xFFFF, 0x01);
        emulator.WriteMemory(0xFF0F, 0x01);

        Assert.Equal(4, emulator.StepInstruction());
        Assert.Equal((ushort)0x0102, emulator.Registers.ProgramCounter);
        Assert.False(emulator.Registers.Halted);
    }

    [Fact]
    public void InterruptRegisterHighBitsDoNotWakeOrDispatchCpu()
    {
        var rom = MakeRom();
        new byte[] { 0xFB, 0x00, 0x76 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.StepInstruction();
        emulator.StepInstruction();
        emulator.StepInstruction();

        emulator.WriteMemory(0xFFFF, 0xE0);
        emulator.WriteMemory(0xFF0F, 0xE0);

        Assert.Equal(4, emulator.StepInstruction());
        Assert.Equal((ushort)0x0103, emulator.Registers.ProgramCounter);
        Assert.True(emulator.Registers.Halted);
        Assert.True(emulator.Registers.InterruptMasterEnable);
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
        Assert.Equal((byte)0x00, (byte)(emulator.PeekMemory(0xFF0F) & 0x04));
        emulator.RunCycles(4);
        Assert.Equal((byte)0x04, (byte)(emulator.PeekMemory(0xFF0F) & 0x04));
    }

    [Fact]
    public void TimerReloadWindowHonorsTmaAndTimaWrites()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0xFF06, 0x3C);
        emulator.WriteMemory(0xFF05, 0xFF);
        emulator.WriteMemory(0xFF07, 0x05);
        emulator.RunCycles(16);

        emulator.WriteMemory(0xFF06, 0xA5);
        Assert.Equal((byte)0xA5, emulator.PeekMemory(0xFF05));
        emulator.WriteMemory(0xFF05, 0x5A);
        Assert.Equal((byte)0x5A, emulator.PeekMemory(0xFF05));

        emulator.RunCycles(4);
        Assert.Equal((byte)0x5A, emulator.PeekMemory(0xFF05));

        emulator.WriteMemory(0xFF05, 0xC3);
        Assert.Equal((byte)0x5A, emulator.PeekMemory(0xFF05));
    }

    [Fact]
    public void TimerTacWriteCausesFallingEdgeIncrement()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0xFF05, 0x10);
        emulator.RunCycles(8); // divider bit 3 is high
        emulator.WriteMemory(0xFF07, 0x05); // enable, select divider bit 3
        emulator.WriteMemory(0xFF07, 0x00); // disabling the timer creates a falling edge

        Assert.Equal((byte)0x11, emulator.PeekMemory(0xFF05));
    }

    [Fact]
    public void TimerTacWriteDoesNotIncrementWhenTheSelectedBitWasLow()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0xFF05, 0x10);
        emulator.RunCycles(4); // divider bit 3 is low
        emulator.WriteMemory(0xFF07, 0x05);
        emulator.WriteMemory(0xFF07, 0x00);

        Assert.Equal((byte)0x10, emulator.PeekMemory(0xFF05));
    }

    [Fact]
    public void TimerDivWriteCausesFallingEdgeIncrement()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0xFF05, 0x10);
        emulator.RunCycles(8); // divider bit 3 is high
        emulator.WriteMemory(0xFF07, 0x05); // enable, select divider bit 3
        emulator.WriteMemory(0xFF04, 0x00); // DIV reset creates a falling edge

        Assert.Equal((byte)0x11, emulator.PeekMemory(0xFF05));
        Assert.Equal((byte)0x00, emulator.PeekMemory(0xFF04));
    }

    [Fact]
    public void TimerDivWriteDoesNotIncrementWhenTheSelectedBitWasLow()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0xFF05, 0x10);
        emulator.RunCycles(4); // divider bit 3 is low
        emulator.WriteMemory(0xFF07, 0x05);
        emulator.WriteMemory(0xFF04, 0x00);

        Assert.Equal((byte)0x10, emulator.PeekMemory(0xFF05));
    }

    [Fact]
    public void OamDmaCopiesOneHundredSixtyBytesInSixHundredFortyTCycles()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        for (var i = 0; i < 0xA0; i++) emulator.WriteMemory((ushort)(0xC000 + i), (byte)(i ^ 0x5A));

        emulator.WriteMemory(0xFF46, 0xC0);
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFE00));
        emulator.RunCycles(639);
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFE9F));
        emulator.RunCycles(1);

        for (var i = 0; i < 0xA0; i++)
            Assert.Equal((byte)(i ^ 0x5A), emulator.PeekMemory((ushort)(0xFE00 + i)));
    }

    [Fact]
    public void OamDmaBlocksCpuBusExceptHighRamAndInterruptEnable()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0xC000, 0xA5);
        emulator.WriteMemory(0xFF80, 0x5A);
        emulator.WriteMemory(0xFF46, 0xC0);

        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xC000));
        Assert.Equal((byte)0x5A, emulator.PeekMemory(0xFF80));
        emulator.WriteMemory(0xC000, 0x3C);
        emulator.WriteMemory(0xFF80, 0xC3);
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xC000));
        Assert.Equal((byte)0xC3, emulator.PeekMemory(0xFF80));

        emulator.WriteMemory(0xFFFF, 0x1F);
        Assert.Equal((byte)0x1F, emulator.PeekMemory(0xFFFF));
        emulator.RunCycles(640);
        Assert.Equal((byte)0xA5, emulator.PeekMemory(0xC000));
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
    public void SerialEndpointCompletesExternalClockTransfer()
    {
        var endpoint = new TestSerialEndpoint { Response = 0x3C };
        var emulator = new Emulator(GameBoyModel.DmgB, new EmulatorOptions { SerialEndpoint = endpoint });
        emulator.LoadRom(MakeRom());
        emulator.WriteMemory(0xFF01, 0xA5);
        emulator.WriteMemory(0xFF02, 0x80); // transfer start, external clock

        for (var bit = 0; bit < 7; bit++)
        {
            emulator.ClockSerialBit();
            Assert.Equal((byte)0xA5, emulator.PeekMemory(0xFF01));
            Assert.Equal((byte)0x80, (byte)(emulator.PeekMemory(0xFF02) & 0x80));
        }

        emulator.ClockSerialBit();
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
    public void JoypadSelectionSwitchIsDelayedOnDmg()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0xFF00, 0x10);
        emulator.SetButtonState(GameBoyButton.Right, true);
        emulator.WriteMemory(0xFF00, 0x20); // action to direction row: 24 T-cycles

        Assert.Equal((byte)0x20, (byte)(emulator.PeekMemory(0xFF00) & 0x30));
        Assert.Equal((byte)0x0F, (byte)(emulator.PeekMemory(0xFF00) & 0x0F));
        emulator.RunCycles(23);
        Assert.Equal((byte)0x0F, (byte)(emulator.PeekMemory(0xFF00) & 0x0F));
        emulator.RunCycles(1);
        Assert.Equal((byte)0x0E, (byte)(emulator.PeekMemory(0xFF00) & 0x0F));
    }

    [Fact]
    public void JoypadFiltersOpposingDirectionInputs()
    {
        var emulator = NewEmulator(MakeRom(), GameBoyModel.CgbE);
        emulator.WriteMemory(0xFF00, 0x20);
        emulator.SetButtonState(GameBoyButton.Right, true);
        emulator.SetButtonState(GameBoyButton.Left, true);
        Assert.Equal((byte)0x0E, (byte)(emulator.PeekMemory(0xFF00) & 0x0F));

        emulator.SetButtonState(GameBoyButton.Right, false);
        emulator.SetButtonState(GameBoyButton.Left, false);
        Assert.Equal((byte)0x0F, (byte)(emulator.PeekMemory(0xFF00) & 0x0F));
        emulator.SetButtonState(GameBoyButton.Up, true);
        emulator.SetButtonState(GameBoyButton.Down, true);
        Assert.Equal((byte)0x0B, (byte)(emulator.PeekMemory(0xFF00) & 0x0F));
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
    public void PpuFineScrollExtendsDmgModeThree()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0xFF43, 3); // SCX fine-scroll penalty
        emulator.WriteMemory(0xFF40, 0x80);

        emulator.RunCycles(80 + 174);
        Assert.Equal((byte)3, (byte)(emulator.PeekMemory(0xFF41) & 0x03));
        emulator.RunCycles(1);
        Assert.Equal((byte)0, (byte)(emulator.PeekMemory(0xFF41) & 0x03));
    }

    [Fact]
    public void PpuWindowAtZeroAddsDmgFineScrollFetchCycle()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0xFF43, 1); // nonzero fine scroll
        emulator.WriteMemory(0xFF4A, 0); // WY
        emulator.WriteMemory(0xFF4B, 0); // WX = 0
        emulator.WriteMemory(0xFF40, 0xB1); // LCD, window, BG, unsigned tile data

        emulator.RunCycles(80 + 173);
        Assert.Equal((byte)3, (byte)(emulator.PeekMemory(0xFF41) & 0x03));
    }

    [Fact]
    public void CgbPpuPaletteRegistersAutoIncrementIndexedPaletteRam()
    {
        var emulator = NewEmulator(MakeRom(), GameBoyModel.CgbE);
        emulator.WriteMemory(0xFF68, 0x80); // BG palette index 0, auto-increment
        emulator.WriteMemory(0xFF69, 0x12);
        emulator.WriteMemory(0xFF69, 0x34);

        Assert.Equal((byte)0xC2, emulator.PeekMemory(0xFF68));
        emulator.WriteMemory(0xFF68, 0x00);
        Assert.Equal((byte)0x12, emulator.PeekMemory(0xFF69));
        emulator.WriteMemory(0xFF68, 0x01);
        Assert.Equal((byte)0x34, emulator.PeekMemory(0xFF69));

        emulator.WriteMemory(0xFF6A, 0x3F);
        emulator.WriteMemory(0xFF6B, 0xA5);
        Assert.Equal((byte)0xA5, emulator.PeekMemory(0xFF6B));

        emulator.WriteMemory(0xFF68, 0xBF); // final byte with auto-increment
        emulator.WriteMemory(0xFF69, 0x5A);
        Assert.Equal((byte)0xC0, emulator.PeekMemory(0xFF68));
    }

    [Fact]
    public void DmgDoesNotExposeCgbPaletteRam()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0xFF68, 0x80);
        emulator.WriteMemory(0xFF69, 0xA5);

        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF68));
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF69));
    }

    [Fact]
    public void CgbVramBankRegisterSelectsTheSecondVramBank()
    {
        var emulator = NewEmulator(MakeRom(), GameBoyModel.CgbE);
        emulator.WriteMemory(0x8000, 0x12);
        emulator.WriteMemory(0xFF4F, 0x01);
        emulator.WriteMemory(0x8000, 0x34);

        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF4F));
        Assert.Equal((byte)0x34, emulator.PeekMemory(0x8000));
        emulator.WriteMemory(0xFF4F, 0x00);
        Assert.Equal((byte)0x12, emulator.PeekMemory(0x8000));
    }

    [Fact]
    public void DmgIgnoresVramBankSelection()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0x8000, 0x12);
        emulator.WriteMemory(0xFF4F, 0x01);
        emulator.WriteMemory(0x8000, 0x34);

        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF4F));
        Assert.Equal((byte)0x34, emulator.PeekMemory(0x8000));
    }

    [Fact]
    public void CgbWramBankRegisterSelectsD000AndEchoBank()
    {
        var emulator = NewEmulator(MakeRom(), GameBoyModel.CgbE);
        emulator.WriteMemory(0xD000, 0x12); // default bank 1
        emulator.WriteMemory(0xFF70, 0x02);
        emulator.WriteMemory(0xD000, 0x34);

        Assert.Equal((byte)0xFA, emulator.PeekMemory(0xFF70));
        Assert.Equal((byte)0x34, emulator.PeekMemory(0xD000));
        emulator.WriteMemory(0xF000, 0x56); // echo of the selected bank
        Assert.Equal((byte)0x56, emulator.PeekMemory(0xD000));

        emulator.WriteMemory(0xFF70, 0x00); // bank zero selects bank 1
        Assert.Equal((byte)0x12, emulator.PeekMemory(0xD000));
        Assert.Equal((byte)0xF9, emulator.PeekMemory(0xFF70));
    }

    [Fact]
    public void DmgIgnoresWramBankSelection()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0xD000, 0x12);
        emulator.WriteMemory(0xFF70, 0x07);
        emulator.WriteMemory(0xD000, 0x34);

        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF70));
        Assert.Equal((byte)0x34, emulator.PeekMemory(0xD000));
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyKey1ExposesSpeedPreparationWithoutChangingCurrentSpeed(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);

        Assert.Equal((byte)0x7E, emulator.PeekMemory(0xFF4D));
        emulator.WriteMemory(0xFF4D, 0x01);
        Assert.Equal((byte)0x7F, emulator.PeekMemory(0xFF4D));
        emulator.WriteMemory(0xFF4D, 0x00);
        Assert.Equal((byte)0x7E, emulator.PeekMemory(0xFF4D));
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyStopWithPreparedKey1TogglesSpeedAndDoesNotHalt(GameBoyModel model)
    {
        var rom = MakeRom();
        new byte[] { 0x10, 0x00, 0x10, 0x00 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom, model);

        emulator.WriteMemory(0xFF4D, 0x01);
        emulator.StepInstruction();
        Assert.False(emulator.Registers.Halted);
        Assert.Equal((byte)0xFE, emulator.PeekMemory(0xFF4D));

        emulator.WriteMemory(0xFF4D, 0x01);
        emulator.StepInstruction();
        Assert.False(emulator.Registers.Halted);
        Assert.Equal((byte)0x7E, emulator.PeekMemory(0xFF4D));
    }

    [Fact]
    public void CgbDoubleSpeedHalvesFollowingCpuInstructionCadence()
    {
        var rom = MakeRom();
        new byte[] { 0x10, 0x00, 0x00, 0x00, 0x00 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom, GameBoyModel.CgbE);

        emulator.WriteMemory(0xFF4D, 0x01);
        Assert.Equal(4, emulator.StepInstruction()); // speed-switch STOP uses normal cadence
        Assert.Equal(4, emulator.CycleCount);
        Assert.Equal(2, emulator.StepInstruction()); // NOP at double speed
        Assert.Equal(6, emulator.CycleCount);

        emulator.RunCycles(4); // two more NOPs at two hardware T-cycles each
        Assert.Equal((ushort)0x105, emulator.Registers.ProgramCounter);
        Assert.Equal(10, emulator.CycleCount);
    }

    [Fact]
    public void DmgDoesNotExposeCgbKey1()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0xFF4D, 0x01);

        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF4D));
    }

    [Fact]
    public void CgbObjectPriorityRegisterStoresOnlyItsModeBit()
    {
        var emulator = NewEmulator(MakeRom(), GameBoyModel.CgbE);

        Assert.Equal((byte)0xFE, emulator.PeekMemory(0xFF6C));
        emulator.WriteMemory(0xFF6C, 0xFF);
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF6C));
        emulator.WriteMemory(0xFF6C, 0x00);
        Assert.Equal((byte)0xFE, emulator.PeekMemory(0xFF6C));
    }

    [Fact]
    public void DmgDoesNotExposeCgbObjectPriorityRegister()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0xFF6C, 0x01);

        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF6C));
    }

    [Fact]
    public void CgbObjectPriorityModeControlsOverlappingSpriteOrder()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom, GameBoyModel.CgbE);
        emulator.WriteMemory(0x8000, 0xC0); // tile pixels 0 and 1 are color 1
        emulator.WriteMemory(0xFF48, 0x04); // OAM index 0 maps color 1 to shade 1
        emulator.WriteMemory(0xFF49, 0x08); // OAM index 1 maps color 1 to shade 2
        emulator.WriteMemory(0xFE00, 16); // OAM index 0: screen X = 1
        emulator.WriteMemory(0xFE01, 9);
        emulator.WriteMemory(0xFE02, 0);
        emulator.WriteMemory(0xFE04, 16); // OAM index 1: screen X = 0
        emulator.WriteMemory(0xFE05, 8);
        emulator.WriteMemory(0xFE06, 0);
        emulator.WriteMemory(0xFE07, 0x10); // use OBP1
        emulator.WriteMemory(0xFF40, 0x92); // LCD and sprites on, BG off
        emulator.RunCycles(252);

        var frame = new byte[160 * 144];
        emulator.CopyFrame(frame);
        Assert.Equal((byte)1, frame[1]); // default CGB OPRI: OAM index priority

        var xPriority = NewEmulator(rom, GameBoyModel.CgbE);
        xPriority.WriteMemory(0x8000, 0xC0);
        xPriority.WriteMemory(0xFF48, 0x04);
        xPriority.WriteMemory(0xFF49, 0x08);
        xPriority.WriteMemory(0xFE00, 16);
        xPriority.WriteMemory(0xFE01, 9);
        xPriority.WriteMemory(0xFE02, 0);
        xPriority.WriteMemory(0xFE04, 16);
        xPriority.WriteMemory(0xFE05, 8);
        xPriority.WriteMemory(0xFE06, 0);
        xPriority.WriteMemory(0xFE07, 0x10);
        xPriority.WriteMemory(0xFF6C, 1);
        xPriority.WriteMemory(0xFF40, 0x92);
        xPriority.RunCycles(252);
        xPriority.CopyFrame(frame);
        Assert.Equal((byte)2, frame[1]);
    }

    [Fact]
    public void CgbColorFrameObjectPriorityModeControlsOverlappingSpriteOrder()
    {
        var emulator = NewEmulator(MakeRom(), GameBoyModel.CgbE);
        emulator.WriteMemory(0x8000, 0xC0); // tile pixels 0 and 1 are color 1
        emulator.WriteMemory(0xFF6A, 2); // object palette 0, color 1
        emulator.WriteMemory(0xFF6B, 0x11);
        emulator.WriteMemory(0xFF6A, 3);
        emulator.WriteMemory(0xFF6B, 0x11);
        emulator.WriteMemory(0xFF6A, 10); // object palette 1, color 1
        emulator.WriteMemory(0xFF6B, 0x22);
        emulator.WriteMemory(0xFF6A, 11);
        emulator.WriteMemory(0xFF6B, 0x22);
        emulator.WriteMemory(0xFE00, 16); // OAM index 0: screen X = 1
        emulator.WriteMemory(0xFE01, 9);
        emulator.WriteMemory(0xFE02, 0);
        emulator.WriteMemory(0xFE04, 16); // OAM index 1: screen X = 0
        emulator.WriteMemory(0xFE05, 8);
        emulator.WriteMemory(0xFE06, 0);
        emulator.WriteMemory(0xFE07, 0x01); // CGB object palette 1
        emulator.WriteMemory(0xFF40, 0x92); // LCD and sprites on, BG off
        emulator.RunCycles(252);

        var frame = new ushort[160 * 144];
        emulator.CopyColorFrame(frame);
        Assert.Equal((ushort)0x1111, frame[1]); // default CGB OPRI: OAM index priority

        var xPriority = NewEmulator(MakeRom(), GameBoyModel.CgbE);
        xPriority.WriteMemory(0x8000, 0xC0);
        xPriority.WriteMemory(0xFF6A, 2);
        xPriority.WriteMemory(0xFF6B, 0x11);
        xPriority.WriteMemory(0xFF6A, 3);
        xPriority.WriteMemory(0xFF6B, 0x11);
        xPriority.WriteMemory(0xFF6A, 10);
        xPriority.WriteMemory(0xFF6B, 0x22);
        xPriority.WriteMemory(0xFF6A, 11);
        xPriority.WriteMemory(0xFF6B, 0x22);
        xPriority.WriteMemory(0xFE00, 16);
        xPriority.WriteMemory(0xFE01, 9);
        xPriority.WriteMemory(0xFE02, 0);
        xPriority.WriteMemory(0xFE04, 16);
        xPriority.WriteMemory(0xFE05, 8);
        xPriority.WriteMemory(0xFE06, 0);
        xPriority.WriteMemory(0xFE07, 0x01);
        xPriority.WriteMemory(0xFF6C, 1);
        xPriority.WriteMemory(0xFF40, 0x92);
        xPriority.RunCycles(252);
        xPriority.CopyColorFrame(frame);
        Assert.Equal((ushort)0x2222, frame[1]);
    }

    [Fact]
    public void CgbBackgroundUsesTileAttributesForBankAndFlip()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom, GameBoyModel.CgbE);
        emulator.WriteMemory(0x9800, 0x00); // tile 0 from bank 0
        emulator.WriteMemory(0x9801, 0x00); // tile 0 from bank 0
        emulator.WriteMemory(0xFF4F, 1);
        emulator.WriteMemory(0x9800, 0x08); // tile 0 uses bank 1 data
        emulator.WriteMemory(0x9801, 0x20); // X-flip tile 0 at x=8
        emulator.WriteMemory(0x8000, 0x01); // bank 1 pixel 7
        emulator.WriteMemory(0xFF4F, 0);
        emulator.WriteMemory(0x8000, 0x01); // bank 0 pixel 7
        emulator.WriteMemory(0xFF47, 0xE4);
        emulator.WriteMemory(0xFF40, 0x91); // LCD, BG, unsigned tile data
        emulator.RunCycles(252);

        var frame = new byte[160 * 144];
        emulator.CopyFrame(frame);
        Assert.Equal((byte)0, frame[0]);
        Assert.Equal((byte)1, frame[7]);
        Assert.Equal((byte)1, frame[8]); // flipped bank-0 tile pixel 0 at screen x=8
    }

    [Fact]
    public void CgbSpriteUsesOamTileBankAttribute()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom, GameBoyModel.CgbE);
        emulator.WriteMemory(0xFF4F, 1);
        emulator.WriteMemory(0x8000, 0x80); // bank 1 sprite pixel 0
        emulator.WriteMemory(0xFF4F, 0);
        emulator.WriteMemory(0xFF48, 0x04);
        emulator.WriteMemory(0xFE00, 16); // screen Y = 0
        emulator.WriteMemory(0xFE01, 8); // screen X = 0
        emulator.WriteMemory(0xFE02, 0);
        emulator.WriteMemory(0xFE03, 0x08); // tile data bank 1
        emulator.WriteMemory(0xFF40, 0x92); // LCD and sprites on, BG off
        emulator.RunCycles(252);

        var frame = new byte[160 * 144];
        emulator.CopyFrame(frame);
        Assert.Equal((byte)1, frame[0]);
    }

    [Fact]
    public void CgbBackgroundPriorityAttributeHidesOverlappingSprite()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom, GameBoyModel.CgbE);
        emulator.WriteMemory(0x8000, 0x80); // background tile 0, pixel 0
        emulator.WriteMemory(0x8010, 0x80); // sprite tile 1, pixel 0
        emulator.WriteMemory(0x9800, 0x00);
        emulator.WriteMemory(0xFF4F, 1);
        emulator.WriteMemory(0x9800, 0x80); // CGB BG priority attribute
        emulator.WriteMemory(0xFF4F, 0);
        emulator.WriteMemory(0xFF47, 0xE4); // background color 1 -> shade 1
        emulator.WriteMemory(0xFF48, 0x08); // sprite color 1 -> shade 2
        emulator.WriteMemory(0xFE00, 16);
        emulator.WriteMemory(0xFE01, 8);
        emulator.WriteMemory(0xFE02, 1);
        emulator.WriteMemory(0xFF40, 0x93); // LCD, BG, and sprites on
        emulator.RunCycles(252);

        var frame = new byte[160 * 144];
        emulator.CopyFrame(frame);
        Assert.Equal((byte)1, frame[0]);
    }

    [Fact]
    public void CgbColorFrameAppliesBackgroundPriorityToSprites()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom, GameBoyModel.CgbE);
        emulator.WriteMemory(0x8000, 0x80); // background tile 0, color 1
        emulator.WriteMemory(0x8010, 0x80); // sprite tile 1, color 1
        emulator.WriteMemory(0x9800, 0x00);
        emulator.WriteMemory(0xFF4F, 1);
        emulator.WriteMemory(0x9800, 0x80); // CGB BG priority attribute
        emulator.WriteMemory(0xFF4F, 0);
        emulator.WriteMemory(0xFF68, 2); // background palette 0, color 1
        emulator.WriteMemory(0xFF69, 0x11);
        emulator.WriteMemory(0xFF68, 3);
        emulator.WriteMemory(0xFF69, 0x11);
        emulator.WriteMemory(0xFF6A, 10); // object palette 1, color 1
        emulator.WriteMemory(0xFF6B, 0x22);
        emulator.WriteMemory(0xFF6A, 11);
        emulator.WriteMemory(0xFF6B, 0x22);
        emulator.WriteMemory(0xFE00, 16);
        emulator.WriteMemory(0xFE01, 8);
        emulator.WriteMemory(0xFE02, 1);
        emulator.WriteMemory(0xFF40, 0x93); // LCD, BG, and sprites on
        emulator.RunCycles(252);

        var frame = new ushort[160 * 144];
        emulator.CopyColorFrame(frame);
        Assert.Equal((ushort)0x1111, frame[0]);
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyColorFrameHidesSpriteBehindNonzeroBackground(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0x8000, 0x80); // background tile 0, color 1
        emulator.WriteMemory(0x8010, 0x80); // sprite tile 1, color 1
        emulator.WriteMemory(0xFF68, 2); // background palette 0, color 1
        emulator.WriteMemory(0xFF69, 0x11);
        emulator.WriteMemory(0xFF68, 3);
        emulator.WriteMemory(0xFF69, 0x11);
        emulator.WriteMemory(0xFF6A, 10); // object palette 1, color 1
        emulator.WriteMemory(0xFF6B, 0x22);
        emulator.WriteMemory(0xFF6A, 11);
        emulator.WriteMemory(0xFF6B, 0x22);
        emulator.WriteMemory(0xFE00, 16); // y=0
        emulator.WriteMemory(0xFE01, 8); // x=0
        emulator.WriteMemory(0xFE02, 1);
        emulator.WriteMemory(0xFE03, 0x81); // behind BG, CGB object palette 1
        emulator.WriteMemory(0xFF40, 0x93); // LCD, BG, and sprites on
        emulator.RunCycles(252);

        var frame = new ushort[160 * 144];
        emulator.CopyColorFrame(frame);
        Assert.Equal((ushort)0x1111, frame[0]);
    }

    [Fact]
    public void CgbColorFrameClearsWhenLcdIsDisabled()
    {
        var emulator = NewEmulator(MakeRom(), GameBoyModel.CgbE);
        emulator.WriteMemory(0x8000, 0x80); // background tile 0, color 1
        emulator.WriteMemory(0xFF68, 2); // background palette 0, color 1
        emulator.WriteMemory(0xFF69, 0x34);
        emulator.WriteMemory(0xFF68, 3);
        emulator.WriteMemory(0xFF69, 0x12);
        emulator.WriteMemory(0xFF40, 0x91); // LCD, BG, unsigned tiles
        emulator.RunCycles(252);

        var frame = new ushort[160 * 144];
        emulator.CopyColorFrame(frame);
        Assert.Equal((ushort)0x1234, frame[0]);

        emulator.WriteMemory(0xFF40, 0x00);
        emulator.CopyColorFrame(frame);
        Assert.All(frame, pixel => Assert.Equal((ushort)0, pixel));
    }

    [Fact]
    public void CgbColorFramePreservesBackgroundThroughTransparentSprite()
    {
        var emulator = NewEmulator(MakeRom(), GameBoyModel.CgbE);
        emulator.WriteMemory(0x8000, 0x80); // background tile 0, color 1
        emulator.WriteMemory(0x8010, 0x00); // sprite tile 1, transparent color 0
        emulator.WriteMemory(0xFF68, 2); // background palette 0, color 1
        emulator.WriteMemory(0xFF69, 0x78);
        emulator.WriteMemory(0xFF68, 3);
        emulator.WriteMemory(0xFF69, 0x56);
        emulator.WriteMemory(0xFE00, 16);
        emulator.WriteMemory(0xFE01, 8);
        emulator.WriteMemory(0xFE02, 1);
        emulator.WriteMemory(0xFF40, 0x93); // LCD, BG, and sprites on
        emulator.RunCycles(252);

        var frame = new ushort[160 * 144];
        emulator.CopyColorFrame(frame);
        Assert.Equal((ushort)0x5678, frame[0]);
    }

    [Theory]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyModelsExposeIndexedColorFrames(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0x8000, 0x80); // background tile 0, color 1
        emulator.WriteMemory(0xFF68, 2); // background palette 0, color 1
        emulator.WriteMemory(0xFF69, 0xBC);
        emulator.WriteMemory(0xFF68, 3);
        emulator.WriteMemory(0xFF69, 0x0A);
        emulator.WriteMemory(0xFF40, 0x91); // LCD, BG, unsigned tiles
        emulator.RunCycles(252);

        var frame = new ushort[160 * 144];
        emulator.CopyColorFrame(frame);
        Assert.Equal((ushort)0x0ABC, frame[0]);
    }

    [Theory]
    [InlineData(GameBoyModel.Cgb0)]
    [InlineData(GameBoyModel.CgbA)]
    [InlineData(GameBoyModel.CgbB)]
    [InlineData(GameBoyModel.CgbC)]
    [InlineData(GameBoyModel.CgbD)]
    [InlineData(GameBoyModel.CgbE)]
    public void CgbRevisionsExposeIndexedColorFrames(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0x8000, 0x80); // background tile 0, color 1
        emulator.WriteMemory(0xFF68, 2); // background palette 0, color 1
        emulator.WriteMemory(0xFF69, 0x5A);
        emulator.WriteMemory(0xFF68, 3);
        emulator.WriteMemory(0xFF69, 0x01);
        emulator.WriteMemory(0xFF40, 0x91); // LCD, BG, unsigned tiles
        emulator.RunCycles(252);

        var frame = new ushort[160 * 144];
        emulator.CopyColorFrame(frame);
        Assert.Equal((ushort)0x015A, frame[0]);
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyColorFrameUsesBackgroundPaletteIndexAndRgb15Data(GameBoyModel model)
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom, model);
        emulator.WriteMemory(0x8000, 0x80); // tile 0, color 1 at pixel 0
        emulator.WriteMemory(0x9800, 0x00); // tile 0 from bank 0
        emulator.WriteMemory(0xFF4F, 1);
        emulator.WriteMemory(0x9800, 0x02); // CGB background palette 2
        emulator.WriteMemory(0xFF4F, 0);
        emulator.WriteMemory(0xFF68, 18); // palette 2, color 1 low byte
        emulator.WriteMemory(0xFF69, 0x34);
        emulator.WriteMemory(0xFF68, 19);
        emulator.WriteMemory(0xFF69, 0x12);
        emulator.WriteMemory(0xFF40, 0x91); // LCD, BG, unsigned tile data
        emulator.RunCycles(252);

        var frame = new ushort[160 * 144];
        emulator.CopyColorFrame(frame);
        Assert.Equal((ushort)0x1234, frame[0]);
        Assert.Equal((ushort)0, frame[1]);
        Assert.Throws<ArgumentException>(() => emulator.CopyColorFrame(new ushort[10]));
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyColorFrameUsesBackgroundTileDataBankAttribute(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0xFF4F, 1);
        emulator.WriteMemory(0x8000, 0x80); // bank 1 tile 0, color 1 at pixel 0
        emulator.WriteMemory(0x9800, 0x08); // tile 0 uses bank 1 data
        emulator.WriteMemory(0xFF4F, 0);
        emulator.WriteMemory(0x9800, 0x00); // tile 0 from bank 0
        emulator.WriteMemory(0xFF68, 2); // background palette 0, color 1
        emulator.WriteMemory(0xFF69, 0x56);
        emulator.WriteMemory(0xFF68, 3);
        emulator.WriteMemory(0xFF69, 0x34);
        emulator.WriteMemory(0xFF40, 0x91); // LCD, BG, unsigned tile data
        emulator.RunCycles(252);

        var frame = new ushort[160 * 144];
        emulator.CopyColorFrame(frame);
        Assert.Equal((ushort)0x3456, frame[0]);
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyColorFrameUsesBackgroundTileFlipAttributes(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0xFF4F, 1);
        emulator.WriteMemory(0x8000 + 14, 0x01); // bank 1 tile 0, source row 7 pixel 7
        emulator.WriteMemory(0x9800, 0x68); // bank 1, X-flip, and Y-flip
        emulator.WriteMemory(0xFF4F, 0);
        emulator.WriteMemory(0x9800, 0x00); // tile 0 from bank 0
        emulator.WriteMemory(0xFF68, 2); // background palette 0, color 1
        emulator.WriteMemory(0xFF69, 0x9A);
        emulator.WriteMemory(0xFF68, 3);
        emulator.WriteMemory(0xFF69, 0x01);
        emulator.WriteMemory(0xFF40, 0x91); // LCD, BG, unsigned tile data
        emulator.RunCycles(252);

        var frame = new ushort[160 * 144];
        emulator.CopyColorFrame(frame);
        Assert.Equal((ushort)0x019A, frame[0]);
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyColorFrameUsesSpritePaletteIndexAndObjectPaletteRam(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0x8000, 0x80); // sprite tile 0, color 1 at pixel 0
        emulator.WriteMemory(0xFE00, 16); // y=0
        emulator.WriteMemory(0xFE01, 8); // x=0
        emulator.WriteMemory(0xFE02, 0);
        emulator.WriteMemory(0xFE03, 0x03); // object palette 3
        emulator.WriteMemory(0xFF6A, 26); // palette 3, color 1 low byte
        emulator.WriteMemory(0xFF6B, 0xCD);
        emulator.WriteMemory(0xFF6A, 27);
        emulator.WriteMemory(0xFF6B, 0x0A);
        emulator.WriteMemory(0xFF40, 0x92); // LCD and sprites on, BG off
        emulator.RunCycles(252);

        var frame = new ushort[160 * 144];
        emulator.CopyColorFrame(frame);
        Assert.Equal((ushort)0x0ACD, frame[0]);
        Assert.Equal((ushort)0, frame[1]);
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyColorFrameUsesSpriteTileDataBankAttribute(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0xFF4F, 1);
        emulator.WriteMemory(0x8000, 0x80); // bank 1 sprite tile 0, color 1 at pixel 0
        emulator.WriteMemory(0xFF4F, 0);
        emulator.WriteMemory(0xFE00, 16); // y=0
        emulator.WriteMemory(0xFE01, 8); // x=0
        emulator.WriteMemory(0xFE02, 0);
        emulator.WriteMemory(0xFE03, 0x0A); // tile data bank 1, object palette 2
        emulator.WriteMemory(0xFF6A, 18); // object palette 2, color 1
        emulator.WriteMemory(0xFF6B, 0x78);
        emulator.WriteMemory(0xFF6A, 19);
        emulator.WriteMemory(0xFF6B, 0x56);
        emulator.WriteMemory(0xFF40, 0x92); // LCD and sprites on, BG off
        emulator.RunCycles(252);

        var frame = new ushort[160 * 144];
        emulator.CopyColorFrame(frame);
        Assert.Equal((ushort)0x5678, frame[0]);
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyColorFrameUsesSpriteTileFlipAttributes(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0xFF4F, 1);
        emulator.WriteMemory(0x8000 + 14, 0x01); // bank 1 tile 0, source row 7 pixel 7
        emulator.WriteMemory(0xFF4F, 0);
        emulator.WriteMemory(0xFE00, 16); // y=0
        emulator.WriteMemory(0xFE01, 8); // x=0
        emulator.WriteMemory(0xFE02, 0);
        emulator.WriteMemory(0xFE03, 0x68); // tile bank 1, X-flip, and Y-flip
        emulator.WriteMemory(0xFF6A, 2); // object palette 0, color 1
        emulator.WriteMemory(0xFF6B, 0xBC);
        emulator.WriteMemory(0xFF6A, 3);
        emulator.WriteMemory(0xFF6B, 0x0A);
        emulator.WriteMemory(0xFF40, 0x92); // LCD and sprites on, BG off
        emulator.RunCycles(252);

        var frame = new ushort[160 * 144];
        emulator.CopyColorFrame(frame);
        Assert.Equal((ushort)0x0ABC, frame[0]);
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyColorFrameUsesWindowPaletteAttribute(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0x8000, 0x80); // window tile 0, color 1 at pixel 0
        emulator.WriteMemory(0x9C00, 0x00); // window map tile 0
        emulator.WriteMemory(0xFF4F, 1);
        emulator.WriteMemory(0x9C00, 0x04); // window palette 4
        emulator.WriteMemory(0xFF4F, 0);
        emulator.WriteMemory(0xFF4A, 0); // WY=0
        emulator.WriteMemory(0xFF4B, 7); // WX=7, window starts at x=0
        emulator.WriteMemory(0xFF68, 34); // palette 4, color 1 low byte
        emulator.WriteMemory(0xFF69, 0xEF);
        emulator.WriteMemory(0xFF68, 35);
        emulator.WriteMemory(0xFF69, 0xBE);
        emulator.WriteMemory(0xFF40, 0xF1); // LCD, window map 1, window, BG, unsigned tiles
        emulator.RunCycles(252);

        var frame = new ushort[160 * 144];
        emulator.CopyColorFrame(frame);
        Assert.Equal((ushort)0xBEEF, frame[0]);
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyColorFrameUsesWindowTileBankAndFlipAttributes(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0xFF4F, 1);
        emulator.WriteMemory(0x8000 + 14, 0x01); // bank 1 tile 0, source row 7 pixel 7
        emulator.WriteMemory(0x9C00, 0x68); // bank 1, X-flip, and Y-flip
        emulator.WriteMemory(0xFF4F, 0);
        emulator.WriteMemory(0x9C00, 0x00); // window tile 0 from bank 0
        emulator.WriteMemory(0xFF4A, 0); // WY=0
        emulator.WriteMemory(0xFF4B, 7); // WX=7, window starts at x=0
        emulator.WriteMemory(0xFF68, 2); // background palette 0, color 1
        emulator.WriteMemory(0xFF69, 0xCD);
        emulator.WriteMemory(0xFF68, 3);
        emulator.WriteMemory(0xFF69, 0x0B);
        emulator.WriteMemory(0xFF40, 0xF1); // LCD, window map 1, window, BG, unsigned tiles
        emulator.RunCycles(252);

        var frame = new ushort[160 * 144];
        emulator.CopyColorFrame(frame);
        Assert.Equal((ushort)0x0BCD, frame[0]);
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyGeneralDmaCopiesMaskedBlocksIntoSelectedVramBank(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        for (var index = 0; index < 0x10; index++)
            emulator.WriteMemory((ushort)(0xC000 + index), (byte)(index + 1));

        emulator.WriteMemory(0xFF4F, 1);
        emulator.WriteMemory(0xFF51, 0xC0);
        emulator.WriteMemory(0xFF52, 0x03);
        emulator.WriteMemory(0xFF53, 0x80);
        emulator.WriteMemory(0xFF54, 0x07);
        emulator.WriteMemory(0xFF55, 0x00); // one immediate 16-byte block

        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF55));
        for (var index = 0; index < 0x10; index++)
            Assert.Equal((byte)(index + 1), emulator.PeekMemory((ushort)(0x8000 + index)));
        Assert.Equal((byte)0xC0, emulator.PeekMemory(0xFF51));
        Assert.Equal((byte)0x10, emulator.PeekMemory(0xFF52));
        Assert.Equal((byte)0xE0, emulator.PeekMemory(0xFF53));
        Assert.Equal((byte)0x10, emulator.PeekMemory(0xFF54));
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyHblankDmaCopiesOneBlockPerVisibleHblank(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        for (var index = 0; index < 0x20; index++)
            emulator.WriteMemory((ushort)(0xC000 + index), (byte)(index + 1));
        emulator.WriteMemory(0xFF51, 0xC0);
        emulator.WriteMemory(0xFF52, 0x00);
        emulator.WriteMemory(0xFF53, 0x80);
        emulator.WriteMemory(0xFF54, 0x00);
        emulator.WriteMemory(0xFF40, 0x80); // enable LCD timing
        emulator.WriteMemory(0xFF55, 0x81); // two blocks, one per HBlank

        Assert.Equal((byte)0x01, emulator.PeekMemory(0xFF55));
        Assert.Equal((byte)0xE0, emulator.PeekMemory(0xFF53));
        emulator.RunCycles(252); // line 0 HBlank
        for (var index = 0; index < 0x10; index++)
            Assert.Equal((byte)(index + 1), emulator.PeekMemory((ushort)(0x8000 + index)));
        Assert.Equal((byte)0x00, emulator.PeekMemory(0xFF55));

        emulator.RunCycles(456 + 252); // line 1 HBlank
        for (var index = 0; index < 0x10; index++)
            Assert.Equal((byte)(index + 0x11), emulator.PeekMemory((ushort)(0x8010 + index)));
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF55));
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyHblankDmaCancelsOnRequestOrLcdDisable(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        for (var index = 0; index < 0x10; index++)
            emulator.WriteMemory((ushort)(0xC000 + index), (byte)(index + 1));
        emulator.WriteMemory(0xFF51, 0xC0);
        emulator.WriteMemory(0xFF52, 0x00);
        emulator.WriteMemory(0xFF53, 0x80);
        emulator.WriteMemory(0xFF54, 0x00);
        emulator.WriteMemory(0xFF40, 0x80);
        emulator.WriteMemory(0xFF55, 0x80);
        emulator.WriteMemory(0xFF55, 0x00); // cancel an active HBlank request
        emulator.RunCycles(252);
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF55));
        Assert.Equal((byte)0x00, emulator.PeekMemory(0x8000));

        emulator.WriteMemory(0xFF55, 0x80);
        emulator.WriteMemory(0xFF40, 0x00); // LCD disable cancels pending HBlank DMA
        emulator.RunCycles(456);
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF55));
        Assert.Equal((byte)0x00, emulator.PeekMemory(0x8000));
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
        Assert.Equal((byte)0x70, emulator.PeekMemory(0xFF26));

        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF12, 0xF3);
        Assert.Equal((byte)0xF3, emulator.PeekMemory(0xFF12));
        Assert.Equal((byte)0xF0, emulator.PeekMemory(0xFF26));

        emulator.WriteMemory(0xFF26, 0);
        Assert.Equal((byte)0, emulator.PeekMemory(0xFF12));
        Assert.Equal((byte)0x70, emulator.PeekMemory(0xFF26));
    }

    [Fact]
    public void ApuRegisterReadsApplyHardwareMasks()
    {
        var emulator = NewEmulator(MakeRom());
        Assert.Equal((byte)0x80, emulator.PeekMemory(0xFF10));
        Assert.Equal((byte)0x3F, emulator.PeekMemory(0xFF11));
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF15));
        Assert.Equal((byte)0xBF, emulator.PeekMemory(0xFF1E));

        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF10, 0x7F);
        emulator.WriteMemory(0xFF11, 0xC0);
        emulator.WriteMemory(0xFF14, 0x00);
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF10));
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF11));
        Assert.Equal((byte)0xBF, emulator.PeekMemory(0xFF14));
        Assert.Equal((byte)0xF0, emulator.PeekMemory(0xFF26));
    }

    [Fact]
    public void ApuPowerCycleRestartsFrameSequencerTiming()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF12, 0x59); // volume 5, increase every envelope tick
        emulator.WriteMemory(0xFF14, 0x80);

        emulator.RunCycles(6 * 8192);
        emulator.WriteMemory(0xFF26, 0);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF12, 0x59);
        emulator.WriteMemory(0xFF14, 0x80);
        emulator.RunCycles(6 * 8192);

        Assert.Equal((byte)0x59, emulator.PeekMemory(0xFF12));
        emulator.RunCycles(8192);
        Assert.Equal((byte)0x69, emulator.PeekMemory(0xFF12));
    }

    [Fact]
    public void ApuDisablesChannelsWhenTheirDacsAreCleared()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFF26, 0x80);

        emulator.WriteMemory(0xFF12, 0xF0);
        emulator.WriteMemory(0xFF14, 0x80);
        emulator.WriteMemory(0xFF17, 0xF0);
        emulator.WriteMemory(0xFF19, 0x80);
        emulator.WriteMemory(0xFF1A, 0x80);
        emulator.WriteMemory(0xFF1E, 0x80);
        emulator.WriteMemory(0xFF21, 0xF0);
        emulator.WriteMemory(0xFF23, 0x80);
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF26));

        emulator.WriteMemory(0xFF12, 0x00);
        emulator.WriteMemory(0xFF17, 0x00);
        emulator.WriteMemory(0xFF1A, 0x00);
        emulator.WriteMemory(0xFF21, 0x00);

        Assert.Equal((byte)0xF0, emulator.PeekMemory(0xFF26));
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
        Assert.Equal((byte)0xF1, emulator.PeekMemory(0xFF26));

        emulator.RunCycles(8191);
        Assert.Equal((byte)0xF1, emulator.PeekMemory(0xFF26));
        emulator.RunCycles(1);
        Assert.Equal((byte)0xF1, emulator.PeekMemory(0xFF26));
        emulator.RunCycles(8192);
        Assert.Equal((byte)0xF0, emulator.PeekMemory(0xFF26));
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

        emulator.RunCycles(6 * 8192);
        Assert.Equal((byte)0x59, emulator.PeekMemory(0xFF12));
        emulator.RunCycles(8192);
        Assert.Equal((byte)0x69, emulator.PeekMemory(0xFF12));
    }

    [Fact]
    public void ApuChannelOneSweepUpdatesFrequencyOnFrameSequencerSweepStep()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF10, 0x11); // period 1, upward shift 1
        emulator.WriteMemory(0xFF12, 0xF0);
        emulator.WriteMemory(0xFF13, 100);
        emulator.WriteMemory(0xFF14, 0x80); // trigger at frequency 100

        emulator.RunCycles(8192);
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF13));
        emulator.RunCycles(8192);
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF13));
    }

    [Fact]
    public void ApuChannelOneSweepPeriodControlsTheUpdateCadence()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF10, 0x21); // period 2, upward shift 1
        emulator.WriteMemory(0xFF12, 0xF0);
        emulator.WriteMemory(0xFF13, 100);
        emulator.WriteMemory(0xFF14, 0x80);

        emulator.RunCycles(5 * 8192);
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF13));
        emulator.RunCycles(8192);
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF13));
    }

    [Fact]
    public void ApuChannelOneSweepDisablesOnTriggerOverflow()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF10, 0x11); // upward shift 1
        emulator.WriteMemory(0xFF12, 0xF0);
        emulator.WriteMemory(0xFF13, 0x00);
        emulator.WriteMemory(0xFF14, 0x87); // frequency 1792, trigger overflow

        Assert.Equal((byte)0xF0, emulator.PeekMemory(0xFF26));
    }

    [Fact]
    public void ApuChannelOneSweepDisablesOnNegateToAddOverflow()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF10, 0x19); // period 1, negate, shift 1
        emulator.WriteMemory(0xFF12, 0xF0);
        emulator.WriteMemory(0xFF13, 0xDC); // frequency 1500
        emulator.WriteMemory(0xFF14, 0x85); // trigger without initial overflow

        emulator.WriteMemory(0xFF10, 0x10); // switch to addition with shift 0

        Assert.Equal((byte)0xF0, emulator.PeekMemory(0xFF26));
    }

    [Fact]
    public void ApuChannelOneSweepUsesTriggerFrequencyAfterLiveFrequencyWrite()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF10, 0x11); // period 1, upward shift 1
        emulator.WriteMemory(0xFF12, 0xF0);
        emulator.WriteMemory(0xFF13, 100);
        emulator.WriteMemory(0xFF14, 0x80); // trigger at frequency 100

        emulator.RunCycles(8192);
        emulator.WriteMemory(0xFF13, 200); // change playback frequency only
        emulator.RunCycles(8192);

        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF13));
    }

    [Fact]
    public void ApuChannelOneSweepRegisterWritesReconfigureActiveSweep()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF10, 0x11); // period 1, upward shift 1
        emulator.WriteMemory(0xFF12, 0xF0);
        emulator.WriteMemory(0xFF13, 100);
        emulator.WriteMemory(0xFF14, 0x80);

        emulator.RunCycles(2 * 8192);
        emulator.WriteMemory(0xFF10, 0x00); // disable sweep while active
        emulator.RunCycles(4 * 8192);

        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF13));
        Assert.Equal((byte)0xF1, emulator.PeekMemory(0xFF26));
    }

    [Fact]
    public void ApuEmitsDeterministicSamplesIntoCallerOwnedBuffer()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF11, 0x00);
        emulator.WriteMemory(0xFF12, 0xF0);
        emulator.WriteMemory(0xFF14, 0x80);
        emulator.RunCycles(95);

        var samples = new short[2];
        Assert.Equal(1, emulator.CopyAudioSamples(samples));
        Assert.NotEqual((short)0, samples[0]);
        Assert.Equal(0, emulator.CopyAudioSamples(samples));
    }

    [Fact]
    public void ApuPulseFrequencyAdvancesDutyPhase()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var lowFrequency = NewEmulator(rom);
        var highFrequency = NewEmulator(rom);
        foreach (var emulator in new[] { lowFrequency, highFrequency })
        {
            emulator.WriteMemory(0xFF26, 0x80);
            emulator.WriteMemory(0xFF11, 0x00); // duty 0
            emulator.WriteMemory(0xFF12, 0xF0); // DAC and volume
        }
        lowFrequency.WriteMemory(0xFF13, 0x00);
        lowFrequency.WriteMemory(0xFF14, 0x80);
        highFrequency.WriteMemory(0xFF13, 0x00);
        highFrequency.WriteMemory(0xFF14, 0x87);

        lowFrequency.RunCycles(95 * 2);
        highFrequency.RunCycles(95 * 2);
        var lowSamples = new short[2];
        var highSamples = new short[2];
        Assert.Equal(2, lowFrequency.CopyAudioSamples(lowSamples));
        Assert.Equal(2, highFrequency.CopyAudioSamples(highSamples));
        Assert.NotEqual(lowSamples[1], highSamples[1]);
    }

    [Fact]
    public void ApuChannelTwoTriggerReportsStatusAndExpiresByLength()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF17, 0xF0);
        emulator.WriteMemory(0xFF16, 0x3F); // length = 1 tick
        emulator.WriteMemory(0xFF19, 0xC0); // length enable + trigger
        Assert.Equal((byte)0xF2, emulator.PeekMemory(0xFF26));

        emulator.RunCycles(8192);
        Assert.Equal((byte)0xF2, emulator.PeekMemory(0xFF26));
        emulator.RunCycles(8192);
        Assert.Equal((byte)0xF0, emulator.PeekMemory(0xFF26));
    }

    [Fact]
    public void ApuActivePulseRespondsToFrequencyWriteWithoutRetrigger()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF16, 0x00); // duty 0
        emulator.WriteMemory(0xFF17, 0xF0); // DAC and volume
        emulator.WriteMemory(0xFF19, 0x80); // trigger at low frequency
        emulator.RunCycles(95);
        var first = new short[1];
        Assert.Equal(1, emulator.CopyAudioSamples(first));

        emulator.WriteMemory(0xFF19, 0x87); // update frequency, no trigger
        emulator.RunCycles(95 * 2);
        var following = new short[2];
        Assert.Equal(2, emulator.CopyAudioSamples(following));
        Assert.NotEqual(first[0], following[1]);
        Assert.Equal((byte)0xF2, emulator.PeekMemory(0xFF26));
    }

    [Fact]
    public void ApuChannelThreePlaysWaveRamAndReportsStatus()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF30, 0xF0);
        emulator.WriteMemory(0xFF1A, 0x80); // DAC on
        emulator.WriteMemory(0xFF1B, 0xFF); // length = 1 tick
        emulator.WriteMemory(0xFF1C, 0x60); // full wave volume
        emulator.WriteMemory(0xFF1E, 0xC0); // length enable + trigger
        Assert.Equal((byte)0xF4, emulator.PeekMemory(0xFF26));

        emulator.RunCycles(95);
        var samples = new short[1];
        Assert.Equal(1, emulator.CopyAudioSamples(samples));
        Assert.NotEqual((short)0, samples[0]);
        emulator.RunCycles(8192 - 95);
        Assert.Equal((byte)0xF4, emulator.PeekMemory(0xFF26));
        emulator.RunCycles(8192);
        Assert.Equal((byte)0xF0, emulator.PeekMemory(0xFF26));
    }

    [Fact]
    public void ApuChannelFourNoiseTriggersAndExpiresByLength()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF21, 0xF0); // DAC and volume
        emulator.WriteMemory(0xFF20, 0x3F); // length = 1 tick
        emulator.WriteMemory(0xFF22, 0x00);
        emulator.WriteMemory(0xFF23, 0xC0); // length enable + trigger
        Assert.Equal((byte)0xF8, emulator.PeekMemory(0xFF26));
        Assert.Equal((byte)0x00, emulator.PeekMemory(0xFF22));

        emulator.RunCycles(95);
        var samples = new short[1];
        Assert.Equal(1, emulator.CopyAudioSamples(samples));
        Assert.NotEqual((short)0, samples[0]);
        emulator.RunCycles(8192 - 95);
        Assert.Equal((byte)0xF8, emulator.PeekMemory(0xFF26));
        emulator.RunCycles(8192);
        Assert.Equal((byte)0xF0, emulator.PeekMemory(0xFF26));
    }

    [Fact]
    public void ApuMixerHonorsNr51RoutingAndNr50Volume()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF12, 0xF0);
        emulator.WriteMemory(0xFF14, 0x80);
        emulator.WriteMemory(0xFF25, 0x00); // explicit mute
        emulator.WriteMemory(0xFF24, 0x77);
        emulator.RunCycles(95);
        var samples = new short[1];
        emulator.CopyAudioSamples(samples);
        Assert.Equal((short)0, samples[0]);

        emulator.WriteMemory(0xFF25, 0x11); // channel 1 to both sides
        emulator.RunCycles(95);
        emulator.CopyAudioSamples(samples);
        Assert.NotEqual((short)0, samples[0]);
    }

    [Fact]
    public void ApuChannelTwoEnvelopeUpdatesOnTheEnvelopeFrameStep()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF17, 0x59); // volume 5, increase every envelope tick
        emulator.WriteMemory(0xFF19, 0x80);

        emulator.RunCycles(6 * 8192);
        Assert.Equal((byte)0x59, emulator.PeekMemory(0xFF17));
        emulator.RunCycles(8192);
        Assert.Equal((byte)0x69, emulator.PeekMemory(0xFF17));
    }

    [Fact]
    public void ApuChannelFourEnvelopeUpdatesOnTheEnvelopeFrameStep()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF21, 0x59); // volume 5, increase every envelope tick
        emulator.WriteMemory(0xFF23, 0x80);

        emulator.RunCycles(6 * 8192);
        Assert.Equal((byte)0x59, emulator.PeekMemory(0xFF21));
        emulator.RunCycles(8192);
        Assert.Equal((byte)0x69, emulator.PeekMemory(0xFF21));
    }

    [Fact]
    public void ApuChannelThreeTracksFrequencyRegistersOnTrigger()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF1A, 0x80);
        emulator.WriteMemory(0xFF1D, 0x34);
        emulator.WriteMemory(0xFF1E, 0x85);

        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF1D));
        Assert.Equal((byte)0xBF, emulator.PeekMemory(0xFF1E));
        Assert.Equal((byte)0xF4, emulator.PeekMemory(0xFF26));
    }

    [Fact]
    public void ApuChannelThreeVolumeCodeZeroMutesWaveOutput()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF30, 0xF0);
        emulator.WriteMemory(0xFF1A, 0x80);
        emulator.WriteMemory(0xFF1C, 0x00); // volume code 0: mute
        emulator.WriteMemory(0xFF1E, 0x80);
        emulator.RunCycles(95);

        var samples = new short[1];
        Assert.Equal(1, emulator.CopyAudioSamples(samples));
        Assert.Equal((short)0, samples[0]);
    }

    [Fact]
    public void ApuActiveWaveRamIsInaccessibleOnDmg()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF30, 0xF0);
        emulator.WriteMemory(0xFF31, 0x0F);
        emulator.WriteMemory(0xFF1A, 0x80);
        emulator.WriteMemory(0xFF1E, 0x80);

        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF30));
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF31));

        emulator.WriteMemory(0xFF31, 0xAA);
        emulator.WriteMemory(0xFF1A, 0x00);
        Assert.Equal((byte)0x0F, emulator.PeekMemory(0xFF31));
    }

    [Fact]
    public void ApuActiveWaveRamUsesCurrentByteOnCgb()
    {
        var emulator = NewEmulator(MakeRom(), GameBoyModel.CgbE);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF30, 0xF0);
        emulator.WriteMemory(0xFF31, 0x0F);
        emulator.WriteMemory(0xFF1A, 0x80);
        emulator.WriteMemory(0xFF1E, 0x80);

        Assert.Equal((byte)0xF0, emulator.PeekMemory(0xFF31));
        emulator.WriteMemory(0xFF31, 0xAA);
        Assert.Equal((byte)0xAA, emulator.PeekMemory(0xFF30));

        emulator.WriteMemory(0xFF1A, 0x00);
        Assert.Equal((byte)0x0F, emulator.PeekMemory(0xFF31));
    }

    [Fact]
    public void ApuPcmRegistersExposeCgbChannelAmplitudes()
    {
        var emulator = NewEmulator(MakeRom(), GameBoyModel.CgbE);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF11, 0x40); // duty 1: first sample is high
        emulator.WriteMemory(0xFF12, 0xF0);
        emulator.WriteMemory(0xFF14, 0x80);
        emulator.WriteMemory(0xFF16, 0x40); // duty 1: first sample is high
        emulator.WriteMemory(0xFF17, 0xA0);
        emulator.WriteMemory(0xFF19, 0x80);
        emulator.WriteMemory(0xFF30, 0xF0);
        emulator.WriteMemory(0xFF1A, 0x80);
        emulator.WriteMemory(0xFF1C, 0x60);
        emulator.WriteMemory(0xFF1E, 0x80);

        Assert.Equal((byte)0xAF, emulator.PeekMemory(0xFF76));
        Assert.Equal((byte)0x03, emulator.PeekMemory(0xFF77));
    }

    [Fact]
    public void ApuPcmRegistersReadAsOpenBusOnDmg()
    {
        var emulator = NewEmulator(MakeRom());

        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF76));
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF77));
    }

    [Fact]
    public void InputRecordingRoundTripsOrderedEventsAndRejectsMalformedData()
    {
        var recording = new InputRecording();
        recording.Add(new InputEvent(12, GameBoyButton.A, true));
        recording.Add(new InputEvent(12, GameBoyButton.A, false));
        recording.Add(new InputEvent(40, GameBoyButton.Start, true));
        using var stream = new MemoryStream();
        recording.Write(stream);
        stream.Position = 0;
        var restored = InputRecording.Read(stream);
        Assert.Equal(recording.Events, restored.Events);
        Assert.Throws<ArgumentException>(() => recording.Add(new InputEvent(1, GameBoyButton.B, true)));

        using var malformed = new MemoryStream(new byte[] { (byte)'C', (byte)'B', (byte)'I', (byte)'N', 1, 0, 1, 0, 0, 0 });
        Assert.Throws<EndOfStreamException>(() => InputRecording.Read(malformed));
    }

    [Fact]
    public void EmulatorReplaysInputEventsAtTheirRecordedCycles()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFF00, 0x10); // action-button row
        var recording = new InputRecording();
        recording.Add(new InputEvent(20, GameBoyButton.A, true));
        recording.Add(new InputEvent(40, GameBoyButton.A, false));

        emulator.ReplayInputRecording(recording);

        Assert.Equal(40, emulator.CycleCount);
        Assert.Equal((byte)0xDF, emulator.PeekMemory(0xFF00));
        Assert.Throws<InvalidOperationException>(() => emulator.ReplayInputRecording(recording));
    }

    [Fact]
    public void StateHashIsStableForEquivalentRunsAndChangesWithState()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var first = NewEmulator(rom);
        var second = NewEmulator(rom);
        first.RunCycles(64);
        second.RunCycles(64);
        Assert.Equal(first.ComputeStateHash(), second.ComputeStateHash());

        second.WriteMemory(0xC000, 0xA5);
        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Fact]
    public void StateHashIncludesCartridgeBatteryState()
    {
        var rom = MakeRom(type: 0x03, romSizeCode: 1, ramSizeCode: 2);
        var first = NewEmulator(rom);
        var second = NewEmulator(rom);
        first.WriteMemory(0, 0x0A);
        second.WriteMemory(0, 0x0A);
        first.WriteMemory(0xA000, 0x5A);
        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Fact]
    public void StateHashIncludesCgbPaletteRam()
    {
        var first = NewEmulator(MakeRom(), GameBoyModel.CgbE);
        var second = NewEmulator(MakeRom(), GameBoyModel.CgbE);

        second.WriteMemory(0xFF68, 0x00);
        second.WriteMemory(0xFF69, 0x7F);

        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Fact]
    public void StateHashIncludesPpuWindowProgress()
    {
        var first = NewEmulator(MakeRom());
        var second = NewEmulator(MakeRom());
        first.WriteMemory(0xFF40, 0xB1); // LCD, BG, and window enabled
        first.RunCycles(456);
        first.WriteMemory(0xFF40, 0x91); // match second's final LCD control
        second.WriteMemory(0xFF40, 0x91);
        second.RunCycles(456);

        Assert.Equal(first.CycleCount, second.CycleCount);
        Assert.Equal(first.PeekMemory(0xFF40), second.PeekMemory(0xFF40));
        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Fact]
    public void StateHashIncludesTimerDividerPrecision()
    {
        var first = NewEmulator(MakeRom());
        var second = NewEmulator(MakeRom());
        first.RunCycles(1000);
        second.RunCycles(64);
        second.WriteMemory(0xFF04, 0); // reset divider without changing cycle count
        second.RunCycles(936);

        Assert.Equal(first.CycleCount, second.CycleCount);
        Assert.Equal(first.PeekMemory(0xFF04), second.PeekMemory(0xFF04));
        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Fact]
    public void StateHashIncludesApuPhaseAndSampleState()
    {
        var first = NewEmulator(MakeRom());
        var second = NewEmulator(MakeRom());
        ConfigurePulseChannel(first);
        ConfigurePulseChannel(second);
        first.RunCycles(200);
        second.RunCycles(100);
        second.WriteMemory(0xFF14, 0x80); // retrigger at a different phase
        second.RunCycles(100);

        Assert.Equal(first.CycleCount, second.CycleCount);
        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Fact]
    public void StateHashIncludesSerialTransferProgress()
    {
        var first = NewEmulator(MakeRom());
        var second = NewEmulator(MakeRom());
        first.WriteMemory(0xFF02, 0x81); // internal-clock transfer
        second.WriteMemory(0xFF02, 0x81);
        first.RunCycles(100);
        second.RunCycles(50);
        second.WriteMemory(0xFF02, 0x81); // restart without changing visible registers
        second.RunCycles(50);

        Assert.Equal(first.CycleCount, second.CycleCount);
        Assert.Equal(first.PeekMemory(0xFF02), second.PeekMemory(0xFF02));
        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Fact]
    public void StateHashIncludesOamDmaProgress()
    {
        var first = NewEmulator(MakeRom());
        var second = NewEmulator(MakeRom());
        first.WriteMemory(0xFF46, 0xC0);
        second.WriteMemory(0xFF46, 0xC0);
        first.RunCycles(10);
        second.RunCycles(5);
        second.WriteMemory(0xFF46, 0xC0); // restart with zero-filled source/OAM
        second.RunCycles(5);

        Assert.Equal(first.CycleCount, second.CycleCount);
        Assert.Equal(first.PeekMemory(0xFF46), second.PeekMemory(0xFF46));
        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Fact]
    public void StateHashIncludesJoypadSelectionProgress()
    {
        var first = NewEmulator(MakeRom());
        var second = NewEmulator(MakeRom());
        first.WriteMemory(0xFF00, 0x10);
        second.WriteMemory(0xFF00, 0x10);
        first.WriteMemory(0xFF00, 0x20);
        second.WriteMemory(0xFF00, 0x20);
        first.RunCycles(10);
        second.RunCycles(5);
        second.WriteMemory(0xFF00, 0x20); // restart the delayed selection
        second.RunCycles(5);

        Assert.Equal(first.CycleCount, second.CycleCount);
        Assert.Equal(first.PeekMemory(0xFF00), second.PeekMemory(0xFF00));
        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Fact]
    public void StateHashIncludesCartridgeMapperState()
    {
        var rom = MakeRom(type: 0x01, romSizeCode: 1, ramSizeCode: 2);
        var first = NewEmulator(rom);
        var second = NewEmulator(rom);

        second.WriteMemory(0x2000, 0x02); // select a different MBC1 ROM bank

        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Fact]
    public void StateHashIncludesRomIdentity()
    {
        var firstRom = MakeRom();
        var secondRom = MakeRom();
        secondRom[0x200] = 0xA5;
        FixChecksum(secondRom);

        var first = NewEmulator(firstRom);
        var second = NewEmulator(secondRom);

        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Fact]
    public void StateHashIncludesBootRomIdentity()
    {
        var first = new Emulator(GameBoyModel.DmgB, new EmulatorOptions { SkipBootRom = false });
        var second = new Emulator(GameBoyModel.DmgB, new EmulatorOptions { SkipBootRom = false });
        var rom = MakeRom();
        first.LoadRom(rom);
        second.LoadRom(rom);
        first.LoadBootRom(new byte[0x100]);
        var bootRom = new byte[0x100];
        bootRom[0xFF] = 0xA5;
        second.LoadBootRom(bootRom);

        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
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

    private static void ConfigurePulseChannel(Emulator emulator)
    {
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF11, 0x80);
        emulator.WriteMemory(0xFF12, 0xF3);
        emulator.WriteMemory(0xFF13, 0x40);
        emulator.WriteMemory(0xFF14, 0x80);
    }

    private static Emulator NewEmulator(byte[] rom, GameBoyModel model = GameBoyModel.DmgB)
    {
        var emulator = new Emulator(model);
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
