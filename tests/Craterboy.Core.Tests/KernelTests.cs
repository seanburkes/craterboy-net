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

    [Theory]
    [InlineData(0xD3)]
    [InlineData(0xDB)]
    [InlineData(0xDD)]
    [InlineData(0xE3)]
    [InlineData(0xE4)]
    [InlineData(0xEB)]
    [InlineData(0xEC)]
    [InlineData(0xED)]
    [InlineData(0xF4)]
    [InlineData(0xFC)]
    [InlineData(0xFD)]
    public void IllegalSm83OpcodesHaltTheCpuLikeSameBoy(byte opcode)
    {
        var rom = MakeRom();
        rom[0x0100] = opcode;
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFFFF, 0x1F);

        Assert.Equal(4, emulator.StepInstruction());
        Assert.True(emulator.Registers.Halted);
        Assert.Equal((byte)0, emulator.PeekMemory(0xFFFF));
        Assert.Equal((ushort)0x0101, emulator.Registers.ProgramCounter);
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
    public void StopPreservesPaddingByteWhenInterruptIsPending()
    {
        var rom = MakeRom();
        new byte[] { 0x10, 0x00 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFFFF, 0x01);
        emulator.WriteMemory(0xFF0F, 0x01);

        Assert.Equal(4, emulator.StepInstruction());
        Assert.Equal((ushort)0x101, emulator.Registers.ProgramCounter);
        Assert.True(emulator.Registers.Halted);

        emulator.StepInstruction();
        Assert.Equal((ushort)0x102, emulator.Registers.ProgramCounter);
        Assert.False(emulator.Registers.Halted);
    }

    [Fact]
    public void StopExitsImmediatelyWhenJoypadLineIsLow()
    {
        var rom = MakeRom();
        new byte[] { 0x10, 0x00 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom, GameBoyModel.CgbE);

        emulator.WriteMemory(0xFF00, 0x10); // select action buttons
        emulator.SetButtonState(GameBoyButton.A, true);

        Assert.Equal(4, emulator.StepInstruction());
        Assert.Equal((ushort)0x102, emulator.Registers.ProgramCounter);
        Assert.False(emulator.Registers.Halted);
    }

    [Fact]
    public void JoypadExitTakesPrecedenceOverCgbSpeedSwitch()
    {
        var rom = MakeRom();
        rom[0x100] = 0x10;
        var emulator = NewEmulator(rom, GameBoyModel.CgbE);

        emulator.WriteMemory(0xFF00, 0x10); // select action buttons
        emulator.SetButtonState(GameBoyButton.A, true);
        emulator.WriteMemory(0xFF4D, 0x01);

        emulator.StepInstruction();

        Assert.Equal((byte)0x7F, emulator.PeekMemory(0xFF4D));
        Assert.False(emulator.Registers.Halted);
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
    public void HaltBugSuppressesTheFollowingOpcodePcIncrement()
    {
        var rom = MakeRom();
        new byte[] { 0x76, 0x3E, 0x12 }.CopyTo(rom, 0x100); // HALT, LD A,d8
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFFFF, 0x01);
        emulator.WriteMemory(0xFF0F, 0x01);

        emulator.StepInstruction();
        Assert.False(emulator.Registers.Halted);
        Assert.Equal((ushort)0x101, emulator.Registers.ProgramCounter);

        emulator.StepInstruction();
        Assert.Equal((byte)0x3E, emulator.Registers.A);
        Assert.Equal((ushort)0x102, emulator.Registers.ProgramCounter);
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
    public void Mbc1MulticartUsesFourBitBankWiring()
    {
        var rom = MakeRom(type: 0x03, romSizeCode: 5, ramSizeCode: 2);
        rom.AsSpan(0x104, 0x30).CopyTo(rom.AsSpan(0x40104, 0x30));
        rom[0x12 * 0x4000] = 0x12;
        rom[0x10 * 0x4000] = 0x10;
        var emulator = NewEmulator(rom);

        emulator.WriteMemory(0x2000, 2);
        emulator.WriteMemory(0x4000, 1);
        Assert.Equal((byte)0x12, emulator.PeekMemory(0x4000));
        emulator.WriteMemory(0x6000, 1);
        Assert.Equal((byte)0x10, emulator.PeekMemory(0x0000));
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

        Assert.Equal((byte)0x00, emulator.PeekMemory(0xFF05));
        Assert.Equal((byte)0x00, (byte)(emulator.PeekMemory(0xFF0F) & 0x04));
        emulator.RunCycles(4);
        Assert.Equal((byte)0x3C, emulator.PeekMemory(0xFF05));
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
        Assert.Equal((byte)0x00, emulator.PeekMemory(0xFF05));
        emulator.WriteMemory(0xFF05, 0x5A);
        Assert.Equal((byte)0x00, emulator.PeekMemory(0xFF05));

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
    public void TimerStopsAdvancingDuringStop()
    {
        var rom = MakeRom();
        new byte[] { 0x10, 0x00, 0x00 }.CopyTo(rom, 0x100); // STOP, then NOP
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFF05, 0x00);
        emulator.RunCycles(8); // selected divider bit is high
        emulator.WriteMemory(0xFF07, 0x05);

        emulator.StepInstruction();
        emulator.RunCycles(8); // the selected divider bit would fall here

        Assert.Equal((byte)0x00, emulator.PeekMemory(0xFF04));
        Assert.Equal((byte)0x00, emulator.PeekMemory(0xFF05));
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
    public void SerialControlWriteResetsPartialTransferProgress()
    {
        var first = NewEmulator(MakeRom());
        var second = NewEmulator(MakeRom());

        first.WriteMemory(0xFF02, 0x80); // start external-clock transfer
        first.ClockSerialBit();
        first.ClockSerialBit();
        first.ClockSerialBit();
        first.WriteMemory(0xFF02, 0x00); // stop and reset the transfer
        second.WriteMemory(0xFF02, 0x00);

        Assert.Equal(second.ComputeStateHash(), first.ComputeStateHash());
    }

    [Theory]
    [InlineData(GameBoyModel.DmgB, (byte)0xFE)]
    [InlineData(GameBoyModel.CgbE, (byte)0xFC)]
    public void SerialControlReadbackAppliesModelSpecificFixedBits(GameBoyModel model, byte expected)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0xFF02, 0x80); // external-clock transfer

        Assert.Equal(expected, emulator.PeekMemory(0xFF02));
    }

    [Fact]
    public void CgbSerialFastInternalClockCompletesInTwoHundredFiftySixTCycles()
    {
        var endpoint = new TestSerialEndpoint { Response = 0x3C };
        var emulator = new Emulator(GameBoyModel.CgbE, new EmulatorOptions { SerialEndpoint = endpoint });
        emulator.LoadRom(MakeRom());
        emulator.WriteMemory(0xFF01, 0xA5);
        emulator.WriteMemory(0xFF02, 0x83); // start, internal clock, CGB fast clock

        emulator.RunCycles(255);
        Assert.Equal((byte)0x80, (byte)(emulator.PeekMemory(0xFF02) & 0x80));
        emulator.RunCycles(1);

        Assert.Equal((byte)0x3C, emulator.PeekMemory(0xFF01));
        Assert.Equal((byte)0x00, (byte)(emulator.PeekMemory(0xFF02) & 0x80));
        Assert.Equal((byte)0x08, (byte)(emulator.PeekMemory(0xFF0F) & 0x08));
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
    public void JoypadSelectionSwitchUsesShorterMgbDelay()
    {
        var emulator = NewEmulator(MakeRom(), GameBoyModel.Mgb);
        emulator.WriteMemory(0xFF00, 0x10);
        emulator.SetButtonState(GameBoyButton.Right, true);
        emulator.WriteMemory(0xFF00, 0x20); // action to direction row: 8 T-cycles on MGB

        Assert.Equal((byte)0x0F, (byte)(emulator.PeekMemory(0xFF00) & 0x0F));
        emulator.RunCycles(7);
        Assert.Equal((byte)0x0F, (byte)(emulator.PeekMemory(0xFF00) & 0x0F));
        emulator.RunCycles(1);
        Assert.Equal((byte)0x0E, (byte)(emulator.PeekMemory(0xFF00) & 0x0F));
    }

    [Fact]
    public void JoypadSelectionSwitchIsImmediateOnCgbFamily()
    {
        var emulator = NewEmulator(MakeRom(), GameBoyModel.CgbE);
        emulator.WriteMemory(0xFF00, 0x10);
        emulator.SetButtonState(GameBoyButton.Right, true);
        emulator.WriteMemory(0xFF00, 0x20);

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
    public void OptionalJoypadBouncingSettlesAfterTheSameBoyWindow()
    {
        var emulator = new Emulator(GameBoyModel.DmgB, new EmulatorOptions { EmulateJoypadBouncing = true });
        emulator.LoadRom(MakeRom());
        emulator.WriteMemory(0xFF00, 0x10);
        emulator.SetButtonState(GameBoyButton.A, true);
        Assert.Equal(0, emulator.ReadMemory(0xFF00) & 1);

        var bounced = false;
        for (var cycle = 0; cycle < 0x0FFF; cycle++)
        {
            emulator.RunCycles(1);
            bounced |= (emulator.ReadMemory(0xFF00) & 1) != 0;
        }

        Assert.True(bounced);
        Assert.Equal(0, emulator.ReadMemory(0xFF00) & 1);
    }

    [Fact]
    public void FauxAnalogInputUsesDigitalOverrideAndDirectionalStrength()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0xFF00, 0x20);
        emulator.SetButtonState(GameBoyButton.Right, true);
        emulator.SetFauxAnalogInput(0, 0);
        Assert.Equal(0x0F, emulator.ReadMemory(0xFF00) & 0x0F);

        emulator.SetFauxAnalogInput(1, 0);
        Assert.Equal(0x0E, emulator.ReadMemory(0xFF00) & 0x0F);
        emulator.DisableFauxAnalogInput();
        Assert.Equal(0x0F, emulator.ReadMemory(0xFF00) & 0x0F);
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

    [Theory]
    [InlineData(GameBoyModel.CgbD)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyPpuPaletteRegistersAutoIncrementIndexedPaletteRam(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
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

    [Theory]
    [InlineData(GameBoyModel.CgbD)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbPpuBlocksPaletteDataAfterModeThreeStarts(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0xFF68, 0x80);
        emulator.WriteMemory(0xFF69, 0x12);
        emulator.WriteMemory(0xFF68, 0x00);
        emulator.WriteMemory(0xFF40, 0x80); // LCD on: mode 2

        emulator.RunCycles(80); // mode 3 begins
        emulator.RunCycles(4);  // first five mode-3 cycles remain accessible
        Assert.Equal((byte)0x12, emulator.PeekMemory(0xFF69));

        emulator.RunCycles(1);
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF69));
        emulator.WriteMemory(0xFF68, 0x80);
        emulator.WriteMemory(0xFF69, 0x34);
        Assert.Equal((byte)0x01, (byte)(emulator.PeekMemory(0xFF68) & 0x3F));

        emulator.WriteMemory(0xFF40, 0x00);
        emulator.WriteMemory(0xFF68, 0x00);
        Assert.Equal((byte)0x12, emulator.PeekMemory(0xFF69));
    }

    [Theory]
    [InlineData(GameBoyModel.CgbD)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyDelaysPaletteDataUntilHblankOpens(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0xFF68, 0x00);
        emulator.WriteMemory(0xFF69, 0x12);
        emulator.WriteMemory(0xFF40, 0x80);

        emulator.RunCycles(252); // enter HBlank
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF69));

        emulator.RunCycles(4);
        Assert.Equal((byte)0x12, emulator.PeekMemory(0xFF69));
    }

    [Theory]
    [InlineData(GameBoyModel.CgbD)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyAllowsPaletteDataAtFirstDoubleSpeedHblank(GameBoyModel model)
    {
        var rom = MakeRom();
        new byte[] { 0x10, 0x00 }.CopyTo(rom, 0x100); // STOP for speed switch
        var emulator = NewEmulator(rom, model);
        emulator.WriteMemory(0xFF68, 0x00);
        emulator.WriteMemory(0xFF69, 0x12);
        emulator.WriteMemory(0xFF4D, 0x01);
        emulator.StepInstruction();
        emulator.WriteMemory(0xFF40, 0x80);

        emulator.RunCycles(253); // enter HBlank at double speed

        Assert.Equal((byte)0x12, emulator.PeekMemory(0xFF69));
    }

    [Theory]
    [InlineData(GameBoyModel.CgbD)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyAllowsPaletteWritesAtFirstDoubleSpeedHblank(GameBoyModel model)
    {
        var rom = MakeRom();
        new byte[] { 0x10, 0x00 }.CopyTo(rom, 0x100); // STOP for speed switch
        var emulator = NewEmulator(rom, model);
        emulator.WriteMemory(0xFF68, 0x80); // BG palette byte 0, auto-increment
        emulator.WriteMemory(0xFF69, 0x12);
        emulator.WriteMemory(0xFF4D, 0x01);
        emulator.StepInstruction();
        emulator.WriteMemory(0xFF40, 0x80);

        emulator.RunCycles(253); // enter HBlank at double speed
        emulator.WriteMemory(0xFF69, 0x34);

        Assert.Equal((byte)0xC2, emulator.PeekMemory(0xFF68));
        emulator.WriteMemory(0xFF68, 0x01);
        Assert.Equal((byte)0x34, emulator.PeekMemory(0xFF69));
    }

    [Theory]
    [InlineData(GameBoyModel.CgbD)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyBlocksPaletteWritesDuringInitialHblankButAdvancesIndex(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0xFF68, 0x80); // BG palette byte 0, auto-increment
        emulator.WriteMemory(0xFF69, 0x12);
        emulator.WriteMemory(0xFF40, 0x80);

        emulator.RunCycles(252); // enter HBlank
        emulator.WriteMemory(0xFF69, 0x34); // blocked, but index still advances

        Assert.Equal((byte)0xC2, emulator.PeekMemory(0xFF68));
        emulator.WriteMemory(0xFF68, 0x00);

        emulator.RunCycles(4);
        Assert.Equal((byte)0x12, emulator.PeekMemory(0xFF69));
        emulator.WriteMemory(0xFF68, 0x81);
        emulator.WriteMemory(0xFF69, 0x56);
        emulator.WriteMemory(0xFF68, 0x01);
        Assert.Equal((byte)0x56, emulator.PeekMemory(0xFF69));
    }

    [Theory]
    [InlineData(GameBoyModel.CgbD)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void LaterCgbFamilyDelaysOamReadsAtHblank(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0xFE00, 0x12);
        emulator.WriteMemory(0xFF40, 0x80);

        emulator.RunCycles(252); // enter HBlank
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFE00));

        emulator.WriteMemory(0xFE00, 0x34); // writes remain available
        emulator.RunCycles(1);
        Assert.Equal((byte)0x34, emulator.PeekMemory(0xFE00));
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

    [Theory]
    [InlineData(GameBoyModel.CgbD)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyVramBankRegisterSelectsTheSecondVramBank(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
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

    [Theory]
    [InlineData(GameBoyModel.CgbD)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyWramBankRegisterSelectsD000AndEchoBank(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
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
    [InlineData(GameBoyModel.CgbD)]
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
    [InlineData(GameBoyModel.CgbD)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyStopWithPreparedKey1TogglesSpeedAndDoesNotHalt(GameBoyModel model)
    {
        var rom = MakeRom();
        new byte[] { 0x00, 0x00, 0x10, 0x00, 0x10, 0x00 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom, model);

        emulator.StepInstruction();
        emulator.StepInstruction();
        emulator.WriteMemory(0xFF05, 0x00);
        emulator.WriteMemory(0xFF07, 0x05); // selected divider bit is high
        emulator.WriteMemory(0xFF4D, 0x01);
        emulator.StepInstruction();
        Assert.False(emulator.Registers.Halted);
        Assert.Equal((byte)0xFE, emulator.PeekMemory(0xFF4D));
        Assert.Equal((byte)0x01, emulator.PeekMemory(0xFF05));

        emulator.WriteMemory(0xFF4D, 0x01);
        emulator.StepInstruction();
        Assert.False(emulator.Registers.Halted);
        Assert.Equal((byte)0x7E, emulator.PeekMemory(0xFF4D));
    }

    [Theory]
    [InlineData(GameBoyModel.CgbD)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyDoubleSpeedHalvesFollowingCpuInstructionCadence(GameBoyModel model)
    {
        var rom = MakeRom();
        new byte[] { 0x10, 0x00, 0x00, 0x00, 0x00 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom, model);

        emulator.WriteMemory(0xFF4D, 0x01);
        Assert.Equal(4, emulator.StepInstruction()); // speed-switch STOP uses normal cadence
        Assert.Equal(4, emulator.CycleCount);
        Assert.Equal(2, emulator.StepInstruction()); // NOP at double speed
        Assert.Equal(6, emulator.CycleCount);

        emulator.RunCycles(4); // two more NOPs at two hardware T-cycles each
        Assert.Equal((ushort)0x105, emulator.Registers.ProgramCounter);
        Assert.Equal(10, emulator.CycleCount);
    }

    [Theory]
    [InlineData(GameBoyModel.Cgb0)]
    [InlineData(GameBoyModel.CgbA)]
    [InlineData(GameBoyModel.CgbB)]
    [InlineData(GameBoyModel.CgbC)]
    public void EarlyCgbDoubleSpeedAllowsMode2OamAccess(GameBoyModel model)
    {
        var rom = MakeRom();
        new byte[] { 0x10, 0x00 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom, model);

        emulator.WriteMemory(0xFF4D, 0x01);
        emulator.StepInstruction();
        emulator.WriteMemory(0xFF40, 0x80); // LCD on: mode 2
        emulator.WriteMemory(0xFE00, 0x5A);

        Assert.Equal((byte)0x5A, emulator.ReadMemory(0xFE00));
    }

    [Fact]
    public void EarlyCgbDoubleSpeedClosesMode2OamWindows()
    {
        var rom = MakeRom();
        new byte[] { 0x10, 0x00 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom, GameBoyModel.CgbC);

        emulator.WriteMemory(0xFF4D, 0x01);
        emulator.StepInstruction();
        emulator.WriteMemory(0xFF40, 0x80); // LCD on: mode 2
        emulator.WriteMemory(0xFE00, 0x5A);
        emulator.RunCycles(70); // CGB OAM writes close here at double speed
        emulator.WriteMemory(0xFE00, 0xA5);
        Assert.Equal((byte)0x5A, emulator.ReadMemory(0xFE00));

        emulator.RunCycles(6); // early-CGB OAM reads close at 76 T-cycles
        Assert.Equal((byte)0xFF, emulator.ReadMemory(0xFE00));
    }

    [Theory]
    [InlineData(GameBoyModel.CgbD)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyDoubleSpeedDelaysHblankVramAccess(GameBoyModel model)
    {
        var rom = MakeRom();
        new byte[] { 0x10, 0x00 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom, model);
        emulator.WriteMemory(0x8000, 0x12);
        emulator.WriteMemory(0xFF4D, 0x01);
        emulator.StepInstruction();
        emulator.WriteMemory(0xFF40, 0x80);
        emulator.RunCycles(252); // enter HBlank

        Assert.Equal((byte)0xFF, emulator.ReadMemory(0x8000));
        emulator.WriteMemory(0x8000, 0x34);
        emulator.RunCycles(2);
        Assert.Equal((byte)0x12, emulator.ReadMemory(0x8000));
        emulator.WriteMemory(0x8000, 0x34);
        Assert.Equal((byte)0x34, emulator.ReadMemory(0x8000));
    }

    [Theory]
    [InlineData(GameBoyModel.CgbD)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void LaterCgbDoubleSpeedKeepsInitialMode2OamReadWindow(GameBoyModel model)
    {
        var rom = MakeRom();
        new byte[] { 0x10, 0x00 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom, model);
        emulator.WriteMemory(0xFE00, 0x5A);
        emulator.WriteMemory(0xFF4D, 0x01);
        emulator.StepInstruction();
        emulator.WriteMemory(0xFF40, 0x80); // LCD on: mode 2

        Assert.Equal((byte)0x5A, emulator.ReadMemory(0xFE00));
        emulator.RunCycles(76);
        Assert.Equal((byte)0xFF, emulator.ReadMemory(0xFE00));
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyDoubleSpeedDelaysMode3StatTransition(GameBoyModel model)
    {
        var rom = MakeRom();
        new byte[] { 0x10, 0x00 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom, model);
        emulator.WriteMemory(0xFF4D, 0x01);
        emulator.StepInstruction();
        emulator.WriteMemory(0xFF40, 0x80);

        emulator.RunCycles(252);
        Assert.Equal((byte)3, (byte)(emulator.PeekMemory(0xFF41) & 0x03));
        emulator.RunCycles(1);
        Assert.Equal((byte)0, (byte)(emulator.PeekMemory(0xFF41) & 0x03));
    }

    [Fact]
    public void DmgDoesNotExposeCgbKey1()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0xFF4D, 0x01);

        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF4D));
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyObjectPriorityRegisterStoresOnlyItsModeBit(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);

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

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyObjectPriorityModeControlsOverlappingSpriteOrder(GameBoyModel model)
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom, model);
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

        var xPriority = NewEmulator(rom, model);
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

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyColorFrameObjectPriorityModeControlsOverlappingSpriteOrder(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
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

        var xPriority = NewEmulator(MakeRom(), model);
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

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbBackgroundUsesTileAttributesForBankAndFlip(GameBoyModel model)
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom, model);
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

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilySpriteUsesOamTileBankAttribute(GameBoyModel model)
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom, model);
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

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyBackgroundPriorityAttributeHidesOverlappingSprite(GameBoyModel model)
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom, model);
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

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyColorFrameAppliesBackgroundPriorityToSprites(GameBoyModel model)
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom, model);
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

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyColorFrameClearsWhenLcdIsDisabled(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
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

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyColorFramePreservesBackgroundThroughTransparentSprite(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
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
    public void CgbFamilyDmaMapsEchoSourcePagesToWorkRam(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        for (var index = 0; index < 0x10; index++)
            emulator.WriteMemory((ushort)(0xD000 + index), (byte)(index + 1));

        emulator.WriteMemory(0xFF51, 0xE0);
        emulator.WriteMemory(0xFF52, 0x00);
        emulator.WriteMemory(0xFF53, 0x80);
        emulator.WriteMemory(0xFF54, 0x00);
        emulator.WriteMemory(0xFF55, 0x00); // one immediate block from E000/F000

        for (var index = 0; index < 0x10; index++)
            Assert.Equal((byte)(index + 1), emulator.PeekMemory((ushort)(0x8000 + index)));
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyDmaWrapsDestinationWithinVramBank(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        for (var index = 0; index < 0x20; index++)
            emulator.WriteMemory((ushort)(0xC000 + index), (byte)(index + 1));

        emulator.WriteMemory(0xFF51, 0xC0);
        emulator.WriteMemory(0xFF52, 0x00);
        emulator.WriteMemory(0xFF53, 0x9F);
        emulator.WriteMemory(0xFF54, 0xF0);
        emulator.WriteMemory(0xFF55, 0x01); // two blocks from 9FF0, wrapping to 8000

        for (var index = 0; index < 0x10; index++)
            Assert.Equal((byte)(index + 1), emulator.PeekMemory((ushort)(0x9FF0 + index)));
        for (var index = 0; index < 0x10; index++)
            Assert.Equal((byte)(index + 0x11), emulator.PeekMemory((ushort)(0x8000 + index)));
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyDmaReadsHighRamSources(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        for (var index = 0; index < 0x10; index++)
            emulator.WriteMemory((ushort)(0xFF80 + index), (byte)(index + 1));

        emulator.WriteMemory(0xFF51, 0xFF);
        emulator.WriteMemory(0xFF52, 0x80);
        emulator.WriteMemory(0xFF53, 0x80);
        emulator.WriteMemory(0xFF54, 0x00);
        emulator.WriteMemory(0xFF55, 0x00); // one immediate block from HRAM

        for (var index = 0; index < 0x10; index++)
            Assert.Equal((byte)(index + 1), emulator.PeekMemory((ushort)(0x8000 + index)));
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyDmaReadsOamSources(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        for (var index = 0; index < 0x10; index++)
            emulator.WriteMemory((ushort)(0xFE00 + index), (byte)(index + 1));

        emulator.WriteMemory(0xFF51, 0xFE);
        emulator.WriteMemory(0xFF52, 0x00);
        emulator.WriteMemory(0xFF53, 0x80);
        emulator.WriteMemory(0xFF54, 0x00);
        emulator.WriteMemory(0xFF55, 0x00); // one immediate block from OAM

        for (var index = 0; index < 0x10; index++)
            Assert.Equal((byte)(index + 1), emulator.PeekMemory((ushort)(0x8000 + index)));
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyDmaReadsIoSources(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0xFF70, 1);
        emulator.WriteMemory(0xFF51, 0xFF);
        emulator.WriteMemory(0xFF52, 0x70);
        emulator.WriteMemory(0xFF53, 0x80);
        emulator.WriteMemory(0xFF54, 0x00);
        emulator.WriteMemory(0xFF55, 0x00); // one immediate block from I/O

        Assert.Equal((byte)0xF9, emulator.PeekMemory(0x8000)); // FF70 read mask
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyDmaReadsMappedBootRomSources(GameBoyModel model)
    {
        var emulator = new Emulator(model, new EmulatorOptions { SkipBootRom = false });
        emulator.LoadRom(MakeRom());
        var bootRom = new byte[0x900];
        bootRom[0] = 0xA5;
        emulator.LoadBootRom(bootRom);

        emulator.WriteMemory(0xFF51, 0x00);
        emulator.WriteMemory(0xFF52, 0x00);
        emulator.WriteMemory(0xFF53, 0x80);
        emulator.WriteMemory(0xFF54, 0x00);
        emulator.WriteMemory(0xFF55, 0x00); // one immediate block from boot ROM

        Assert.Equal((byte)0xA5, emulator.PeekMemory(0x8000));
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
    public void CgbFamilyHblankDmaStartsImmediatelyWhenRequestedInHblank(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        for (var index = 0; index < 0x10; index++)
            emulator.WriteMemory((ushort)(0xC000 + index), (byte)(index + 1));
        emulator.WriteMemory(0xFF40, 0x80);
        emulator.RunCycles(252); // enter line 0 HBlank
        emulator.WriteMemory(0xFF51, 0xC0);
        emulator.WriteMemory(0xFF52, 0x00);
        emulator.WriteMemory(0xFF53, 0x80);
        emulator.WriteMemory(0xFF54, 0x00);
        emulator.WriteMemory(0xFF55, 0x80); // one block, started in active HBlank

        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF55));
        for (var index = 0; index < 0x10; index++)
            Assert.Equal((byte)(index + 1), emulator.PeekMemory((ushort)(0x8000 + index)));
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyHblankDmaDoesNotTransferWhenHaltWakesInHblank(GameBoyModel model)
    {
        var rom = MakeRom();
        rom[0x13F] = 0x76; // HALT
        rom[0x140] = 0x00; // NOP

        var emulator = NewEmulator(rom, model);
        emulator.WriteMemory(0xC000, 0x5A);
        emulator.WriteMemory(0xC010, 0xA5);
        emulator.WriteMemory(0xFF40, 0x80);
        emulator.RunCycles(252); // enter line 0 HBlank

        emulator.WriteMemory(0xFF51, 0xC0);
        emulator.WriteMemory(0xFF52, 0x00);
        emulator.WriteMemory(0xFF53, 0x80);
        emulator.WriteMemory(0xFF54, 0x00);
        emulator.WriteMemory(0xFF55, 0x81); // two blocks

        Assert.Equal((byte)0x00, emulator.PeekMemory(0xFF55));
        Assert.Equal((byte)0x5A, emulator.PeekMemory(0x8000));

        emulator.StepInstruction(); // HALT during HBlank
        Assert.True(emulator.Registers.Halted);

        emulator.WriteMemory(0xFFFF, 0x01);
        emulator.WriteMemory(0xFF0F, 0x01);
        emulator.StepInstruction(); // wake HALT during HBlank

        Assert.False(emulator.Registers.Halted);
        Assert.Equal((byte)0x00, emulator.PeekMemory(0xFF55)); // second block pending
        Assert.Equal((byte)0x00, emulator.PeekMemory(0x8010)); // second block did not transfer
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyHblankDmaTransfersWhenStopWakesDuringHblank(GameBoyModel model)
    {
        var rom = MakeRom();
        new byte[] { 0x10, 0x00, 0x00 }.CopyTo(rom, 0x100); // STOP, then NOP
        var emulator = NewEmulator(rom, model);
        emulator.WriteMemory(0xC000, 0x5A);
        emulator.WriteMemory(0xFF51, 0xC0);
        emulator.WriteMemory(0xFF52, 0x00);
        emulator.WriteMemory(0xFF53, 0x80);
        emulator.WriteMemory(0xFF54, 0x00);
        emulator.WriteMemory(0xFF40, 0x80);
        emulator.WriteMemory(0xFF55, 0x80);

        emulator.StepInstruction(); // enter STOP at cycle 4
        emulator.RunCycles(248); // line 0 HBlank while halted
        Assert.Equal((byte)0x00, emulator.PeekMemory(0x8000));
        Assert.Equal((byte)0x00, emulator.PeekMemory(0xFF55));

        emulator.WriteMemory(0xFFFF, 0x01);
        emulator.WriteMemory(0xFF0F, 0x01); // wake the halted CPU without servicing
        emulator.StepInstruction();

        Assert.Equal((byte)0x5A, emulator.PeekMemory(0x8000));
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF55));
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyHblankDmaTransfersWhenHaltWakesDuringHblank(GameBoyModel model)
    {
        var rom = MakeRom();
        new byte[] { 0x76, 0x00 }.CopyTo(rom, 0x100); // HALT, then NOP
        var emulator = NewEmulator(rom, model);
        emulator.WriteMemory(0xC000, 0x5A);
        emulator.WriteMemory(0xFF51, 0xC0);
        emulator.WriteMemory(0xFF52, 0x00);
        emulator.WriteMemory(0xFF53, 0x80);
        emulator.WriteMemory(0xFF54, 0x00);
        emulator.WriteMemory(0xFF40, 0x80);
        emulator.WriteMemory(0xFF55, 0x80);

        emulator.StepInstruction(); // enter HALT at cycle 4
        emulator.RunCycles(248); // reach line 0 HBlank while halted
        emulator.WriteMemory(0xFFFF, 0x01);
        emulator.WriteMemory(0xFF0F, 0x01); // wake the halted CPU without servicing
        emulator.StepInstruction();

        Assert.Equal((byte)0x5A, emulator.PeekMemory(0x8000));
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF55));
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyHblankDmaDoesNotTransferDuringVblank(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0xC000, 0x5A);
        emulator.WriteMemory(0xFF51, 0xC0);
        emulator.WriteMemory(0xFF52, 0x00);
        emulator.WriteMemory(0xFF53, 0x80);
        emulator.WriteMemory(0xFF54, 0x00);
        emulator.WriteMemory(0xFF40, 0x80);

        emulator.RunCycles(456 * 144); // enter VBlank
        emulator.WriteMemory(0xFF55, 0x80);

        Assert.Equal((byte)0x00, emulator.PeekMemory(0xFF55));
        Assert.Equal((byte)0x00, emulator.PeekMemory(0x8000));

        emulator.RunCycles(456 * 10);

        Assert.Equal((byte)0x00, emulator.PeekMemory(0xFF55));
        Assert.Equal((byte)0x00, emulator.PeekMemory(0x8000));
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyHblankDmaResumesAtTheNextVisibleHblankAfterVblank(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0xC000, 0x5A);
        emulator.WriteMemory(0xFF51, 0xC0);
        emulator.WriteMemory(0xFF52, 0x00);
        emulator.WriteMemory(0xFF53, 0x80);
        emulator.WriteMemory(0xFF54, 0x00);
        emulator.WriteMemory(0xFF40, 0x80);

        emulator.RunCycles(456 * 144); // enter VBlank
        emulator.WriteMemory(0xFF55, 0x80);
        emulator.RunCycles(456 * 10); // return to line 0
        Assert.Equal((byte)0x00, emulator.PeekMemory(0x8000));

        emulator.RunCycles(252); // enter the next visible HBlank

        Assert.Equal((byte)0x5A, emulator.PeekMemory(0x8000));
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF55));
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void CgbFamilyHblankDmaCancelsOnRequestOrTransfersOnLcdDisable(GameBoyModel model)
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

        emulator.RunCycles(204); // leave line 0 HBlank before re-arming
        emulator.WriteMemory(0xFF55, 0x80);
        emulator.WriteMemory(0xFF40, 0x00); // LCD disable transfers a pending request outside HBlank
        emulator.RunCycles(456);
        Assert.Equal((byte)0xFF, emulator.PeekMemory(0xFF55));
        for (var index = 0; index < 0x10; index++)
            Assert.Equal((byte)(index + 1), emulator.PeekMemory((ushort)(0x8000 + index)));
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

    [Theory]
    [InlineData(GameBoyModel.CgbB, (byte)0xF0)]
    [InlineData(GameBoyModel.CgbC, (byte)0xF1)]
    public void ApuChannelOneLengthEnableCanConsumeTheDividerEdgeTick(GameBoyModel model, byte expectedStatus)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF12, 0xF0);
        emulator.WriteMemory(0xFF11, 0x3F); // one length tick
        emulator.WriteMemory(0xFF14, 0x80); // trigger without length
        emulator.WriteMemory(0xFF14, 0x00); // clear length while divider bit is high

        Assert.Equal(expectedStatus, emulator.PeekMemory(0xFF26));
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
    public void ApuChannelOneSweepZeroPeriodUsesEightSweepSteps()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var emulator = NewEmulator(rom);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF10, 0x01); // zero period, upward shift 1
        emulator.WriteMemory(0xFF12, 0xF0);
        emulator.WriteMemory(0xFF13, 0x00);
        emulator.WriteMemory(0xFF14, 0x84); // frequency 1024, trigger

        emulator.RunCycles(6 * 8192);
        Assert.Equal((byte)0xF1, emulator.PeekMemory(0xFF26));
        emulator.RunCycles(24 * 8192);
        Assert.Equal((byte)0xF1, emulator.PeekMemory(0xFF26));
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

    [Theory]
    [InlineData(GameBoyModel.CgbB, (byte)0xF0)]
    [InlineData(GameBoyModel.CgbC, (byte)0xF2)]
    public void ApuChannelTwoLengthEnableCanConsumeTheDividerEdgeTick(GameBoyModel model, byte expectedStatus)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF17, 0xF0);
        emulator.WriteMemory(0xFF16, 0x3F); // one length tick
        emulator.WriteMemory(0xFF19, 0x80); // trigger without length
        emulator.WriteMemory(0xFF19, 0x00); // clear length while divider bit is high

        Assert.Equal(expectedStatus, emulator.PeekMemory(0xFF26));
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

    [Theory]
    [InlineData(GameBoyModel.DmgB)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void ApuWaveRamWritesRemainAvailableWhilePoweredOff(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0xFF30, 0xA5);
        emulator.WriteMemory(0xFF26, 0x80);

        Assert.Equal((byte)0xA5, emulator.PeekMemory(0xFF30));
    }

    [Theory]
    [InlineData(GameBoyModel.DmgB, 0xF0)]
    [InlineData(GameBoyModel.Mgb, 0xF0)]
    [InlineData(GameBoyModel.CgbE, 0xF1)]
    [InlineData(GameBoyModel.AgbA, 0xF1)]
    [InlineData(GameBoyModel.GbpA, 0xF1)]
    public void ApuPoweredOffLengthWritesFollowModelRules(GameBoyModel model, byte expectedStatus)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0xFF11, 0x3F); // one-step length while powered off
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF12, 0xF0);
        emulator.WriteMemory(0xFF14, 0xC0); // trigger with length enabled
        emulator.RunCycles(16_384); // frame-sequencer step 2 clocks length

        Assert.Equal(expectedStatus, emulator.PeekMemory(0xFF26));
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

    [Theory]
    [InlineData(GameBoyModel.CgbB, (byte)0xF0)]
    [InlineData(GameBoyModel.CgbC, (byte)0xF8)]
    public void ApuChannelFourLengthEnableCanConsumeTheDividerEdgeTick(GameBoyModel model, byte expectedStatus)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF21, 0xF0);
        emulator.WriteMemory(0xFF20, 0x3F); // one length tick
        emulator.WriteMemory(0xFF23, 0x80); // trigger without length
        emulator.WriteMemory(0xFF23, 0x00); // clear length while divider bit is high

        Assert.Equal(expectedStatus, emulator.PeekMemory(0xFF26));
    }

    [Fact]
    public void ApuNoiseFrequencyWriteRestartsTheNoiseCadence()
    {
        var rom = MakeRom();
        new byte[] { 0xC3, 0x00, 0x01 }.CopyTo(rom, 0x100);
        var liveWrite = NewEmulator(rom);
        var unchanged = NewEmulator(rom);
        ConfigureNoise(liveWrite, 0x70);
        ConfigureNoise(unchanged, 0x70);

        liveWrite.RunCycles(95);
        liveWrite.WriteMemory(0xFF22, 0x00);
        unchanged.RunCycles(95);

        var discarded = new short[1];
        Assert.Equal(1, liveWrite.CopyAudioSamples(discarded));
        Assert.Equal(1, unchanged.CopyAudioSamples(discarded));
        liveWrite.RunCycles(95 * 128);
        unchanged.RunCycles(95 * 128);

        var actual = new short[128];
        var baseline = new short[128];
        Assert.Equal(128, liveWrite.CopyAudioSamples(actual));
        Assert.Equal(128, unchanged.CopyAudioSamples(baseline));
        Assert.NotEqual(baseline, actual);
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
    public void ApuMgbMatchesDmgChannelAndPowerBehavior()
    {
        var rom = MakeRom();
        var dmg = NewEmulator(rom, GameBoyModel.DmgB);
        var mgb = NewEmulator(rom, GameBoyModel.Mgb);
        ConfigurePulseChannel(dmg);
        ConfigurePulseChannel(mgb);

        dmg.RunCycles(95);
        mgb.RunCycles(95);
        var dmgSamples = new short[1];
        var mgbSamples = new short[1];
        Assert.Equal(1, dmg.CopyAudioSamples(dmgSamples));
        Assert.Equal(1, mgb.CopyAudioSamples(mgbSamples));
        Assert.Equal(dmgSamples, mgbSamples);
        Assert.Equal((byte)0xF1, dmg.PeekMemory(0xFF26));
        Assert.Equal(dmg.PeekMemory(0xFF26), mgb.PeekMemory(0xFF26));

        dmg.WriteMemory(0xFF26, 0);
        mgb.WriteMemory(0xFF26, 0);
        Assert.Equal((byte)0x70, dmg.PeekMemory(0xFF26));
        Assert.Equal(dmg.PeekMemory(0xFF26), mgb.PeekMemory(0xFF26));
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

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void ApuActiveWaveRamUsesModelSpecificAccessRules(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF30, 0xF0);
        emulator.WriteMemory(0xFF31, 0x0F);
        emulator.WriteMemory(0xFF1A, 0x80);
        emulator.WriteMemory(0xFF1E, 0x80);

        var expectedActiveRead = model == GameBoyModel.CgbE ? (byte)0xF0 : (byte)0xFF;
        var expectedActiveWriteRead = model == GameBoyModel.CgbE ? (byte)0xAA : (byte)0xFF;
        Assert.Equal(expectedActiveRead, emulator.PeekMemory(0xFF31));
        emulator.WriteMemory(0xFF31, 0xAA);
        Assert.Equal(expectedActiveWriteRead, emulator.PeekMemory(0xFF30));

        emulator.WriteMemory(0xFF1A, 0x00);
        Assert.Equal((byte)0x0F, emulator.PeekMemory(0xFF31));
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void ApuPcmRegistersExposeCgbFamilyChannelAmplitudes(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
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
    public void EarlyCgbPcmMaskClearsPulseBitsAtWaveformEdge()
    {
        var early = NewEmulator(MakeRom(), GameBoyModel.CgbC);
        var late = NewEmulator(MakeRom(), GameBoyModel.CgbE);
        foreach (var emulator in new[] { early, late })
        {
            emulator.WriteMemory(0xFF26, 0x80);
            emulator.WriteMemory(0xFF11, 0x40); // duty 1 starts high, then falls
            emulator.WriteMemory(0xFF12, 0xF0);
            emulator.WriteMemory(0xFF13, 0x00);
            emulator.WriteMemory(0xFF14, 0x81); // frequency 0x100, trigger
        }

        early.RunCycles(95 * 8);
        late.RunCycles(95 * 8);

        Assert.Equal((byte)0x00, early.PeekMemory(0xFF76));
        Assert.Equal((byte)0x0F, late.PeekMemory(0xFF76));
    }

    [Fact]
    public void InputRecordingRoundTripsOrderedEventsAndRejectsMalformedData()
    {
        var recording = new InputRecording();
        recording.Add(new InputEvent(12, GameBoyButton.A, true));
        recording.Add(new InputEvent(12, GameBoyButton.A, false));
        recording.Add(new InputEvent(40, GameBoyButton.Start, true));
        Assert.Throws<ArgumentNullException>(() => recording.Write(null!));
        Assert.Throws<ArgumentNullException>(() => InputRecording.Read(null!));
        using var stream = new MemoryStream();
        recording.Write(stream);
        Assert.True(stream.CanWrite);
        Assert.Equal(stream.Length, stream.Position);
        var exposedEvents = Assert.IsAssignableFrom<IList<InputEvent>>(recording.Events);
        Assert.Throws<NotSupportedException>(() => exposedEvents.Add(new InputEvent(80, GameBoyButton.B, true)));
        Assert.Equal(3, recording.Events.Count);
        stream.Position = 0;
        var restored = InputRecording.Read(stream);
        Assert.Equal(recording.Events, restored.Events);
        Assert.True(stream.CanRead);
        Assert.Equal(stream.Length, stream.Position);
        Assert.Throws<ArgumentException>(() => recording.Add(new InputEvent(1, GameBoyButton.B, true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => recording.Add(new InputEvent(40, GameBoyButton.B, true, Player: 1)));

        using var malformed = new MemoryStream(new byte[] { (byte)'C', (byte)'B', (byte)'I', (byte)'N', 1, 0, 1, 0, 0, 0 });
        Assert.Throws<EndOfStreamException>(() => InputRecording.Read(malformed));
    }

    [Fact]
    public void GameBoyModelClassifiesCgbAgbAndGbpFamilies()
    {
        foreach (var model in new[] { GameBoyModel.Cgb0, GameBoyModel.CgbA, GameBoyModel.CgbB, GameBoyModel.CgbC, GameBoyModel.CgbD, GameBoyModel.CgbE })
        {
            Assert.True(model.IsCgbRevision());
            Assert.False(model.IsAgb());
            Assert.False(model.IsGbp());
            Assert.True(model.IsColor());
        }

        Assert.True(GameBoyModel.AgbA.IsAgb());
        Assert.False(GameBoyModel.AgbA.IsCgbRevision());
        Assert.False(GameBoyModel.AgbA.IsGbp());
        Assert.True(GameBoyModel.AgbA.IsColor());

        Assert.True(GameBoyModel.GbpA.IsGbp());
        Assert.False(GameBoyModel.GbpA.IsCgbRevision());
        Assert.False(GameBoyModel.GbpA.IsAgb());
        Assert.True(GameBoyModel.GbpA.IsColor());

        foreach (var model in new[] { GameBoyModel.DmgB, GameBoyModel.Mgb, GameBoyModel.Sgb, GameBoyModel.Sgb2 })
        {
            Assert.False(model.IsCgbRevision());
            Assert.False(model.IsAgb());
            Assert.False(model.IsGbp());
            Assert.False(model.IsColor());
        }

        Assert.True(GameBoyModel.DmgB.IsDmg());
        Assert.False(GameBoyModel.Mgb.IsDmg());
        Assert.True(GameBoyModel.Mgb.IsMgb());
        Assert.False(GameBoyModel.DmgB.IsMgb());
        Assert.True(GameBoyModel.Sgb.IsSgb());
        Assert.False(GameBoyModel.Sgb.IsSgb2());
        Assert.True(GameBoyModel.Sgb2.IsSgb2());
        Assert.False(GameBoyModel.Sgb2.IsSgb());
        Assert.True(GameBoyModel.Sgb.IsSuperGameBoy());
        Assert.True(GameBoyModel.Sgb2.IsSuperGameBoy());
    }

    [Fact]
    public void InputRecordingRejectsInvalidEventsAndTrailingData()
    {
        using var invalidMagic = new MemoryStream(new byte[] { (byte)'N', (byte)'O', (byte)'P', (byte)'E' });
        Assert.Throws<InvalidDataException>(() => InputRecording.Read(invalidMagic));

        using var unsupportedVersion = new MemoryStream();
        using (var writer = new BinaryWriter(unsupportedVersion, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("CBIN"u8.ToArray());
            writer.Write((ushort)2);
        }
        unsupportedVersion.Position = 0;
        Assert.Throws<InvalidDataException>(() => InputRecording.Read(unsupportedVersion));

        using var invalidButton = new MemoryStream();
        using (var writer = new BinaryWriter(invalidButton, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("CBIN"u8.ToArray());
            writer.Write((ushort)1);
            writer.Write(1);
            writer.Write(0L);
            writer.Write((byte)0xFF); // invalid button
            writer.Write(true);
            writer.Write((byte)0);
        }
        invalidButton.Position = 0;
        Assert.Throws<InvalidDataException>(() => InputRecording.Read(invalidButton));

        using var invalidPlayer = new MemoryStream();
        using (var writer = new BinaryWriter(invalidPlayer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("CBIN"u8.ToArray());
            writer.Write((ushort)1);
            writer.Write(1);
            writer.Write(0L);
            writer.Write((byte)GameBoyButton.A);
            writer.Write(true);
            writer.Write((byte)4); // unsupported player
        }
        invalidPlayer.Position = 0;
        Assert.Throws<InvalidDataException>(() => InputRecording.Read(invalidPlayer));

        using var outOfOrder = new MemoryStream();
        using (var writer = new BinaryWriter(outOfOrder, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("CBIN"u8.ToArray());
            writer.Write((ushort)1);
            writer.Write(2);
            writer.Write(16L);
            writer.Write((byte)GameBoyButton.A);
            writer.Write(true);
            writer.Write((byte)0);
            writer.Write(8L); // earlier than the preceding event
            writer.Write((byte)GameBoyButton.A);
            writer.Write(false);
            writer.Write((byte)0);
        }
        outOfOrder.Position = 0;
        Assert.Throws<InvalidDataException>(() => InputRecording.Read(outOfOrder));

        using var trailing = new MemoryStream();
        var recording = new InputRecording();
        recording.Add(new InputEvent(0, GameBoyButton.Start, true));
        recording.Write(trailing);
        trailing.WriteByte(0x00);
        trailing.Position = 0;
        Assert.Throws<InvalidDataException>(() => InputRecording.Read(trailing));
    }

    [Fact]
    public void InputRecordingReadsFromNonSeekableStream()
    {
        var recording = new InputRecording();
        recording.Add(new InputEvent(8, GameBoyButton.A, true));
        recording.Add(new InputEvent(16, GameBoyButton.A, false));
        using var encoded = new MemoryStream();
        recording.Write(encoded);

        using var source = new NonSeekableReadStream(encoded.ToArray());
        var restored = InputRecording.Read(source);

        Assert.Equal(recording.Events, restored.Events);
        Assert.False(source.CanSeek);
    }

    [Fact]
    public void InputRecordingWritesToNonSeekableStream()
    {
        var recording = new InputRecording();
        recording.Add(new InputEvent(8, GameBoyButton.Start, true));
        using var destination = new NonSeekableWriteStream();
        recording.Write(destination);

        using var source = new NonSeekableReadStream(destination.ToArray());
        var restored = InputRecording.Read(source);

        Assert.Equal(recording.Events, restored.Events);
        Assert.False(destination.CanSeek);
    }

    [Fact]
    public void InputRecordingRejectsInvalidEventCounts()
    {
        foreach (var count in new[] { -1, 1_000_001 })
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write("CBIN"u8.ToArray());
                writer.Write((ushort)1);
                writer.Write(count);
            }
            stream.Position = 0;
            Assert.Throws<InvalidDataException>(() => InputRecording.Read(stream));
        }
    }

    [Fact]
    public void BessReaderReadsRequiredBlocksAndSkipsUnknownBlocks()
    {
        using var source = new MemoryStream();
        source.Write("native"u8);
        var blockOffset = source.Position;
        WriteBessBlock(source, "NAME", "SameBoy"u8.ToArray());
        WriteBessBlock(source, "FUTR", new byte[] { 1, 2, 3 });
        WriteBessBlock(source, "FUTR", new byte[] { 4, 5 });
        WriteBessBlock(source, "CORE", new byte[] { 1, 0, 0, 0 });
        WriteBessBlock(source, "END ", Array.Empty<byte>());
        WriteBessFooter(source, checked((uint)blockOffset));
        source.Position = 0;

        var blocks = BessReader.Read(source);

        Assert.Equal(new[] { "NAME", "FUTR", "FUTR", "CORE", "END " }, blocks.Select(block => block.Identifier));
        Assert.Equal(new byte[] { 1, 2, 3 }, blocks[1].Payload.ToArray());
        Assert.Equal(new byte[] { 4, 5 }, blocks[2].Payload.ToArray());
        Assert.True(source.CanRead);
    }

    [Fact]
    public void BessReaderRejectsMalformedStructure()
    {
        using var missingFooter = new MemoryStream("CORE"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => BessReader.Read(missingFooter));

        using var duplicateCore = CreateBess((stream, offset) =>
        {
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });
        Assert.Throws<InvalidDataException>(() => BessReader.Read(duplicateCore));

        using var blockBeforeCore = CreateBess((stream, offset) =>
        {
            WriteBessBlock(stream, "MBC ", new byte[3]);
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });
        Assert.Throws<InvalidDataException>(() => BessReader.Read(blockBeforeCore));

        using var nonEmptyEnd = CreateBess((stream, offset) =>
        {
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "END ", new byte[] { 1 });
        });
        Assert.Throws<InvalidDataException>(() => BessReader.Read(nonEmptyEnd));
    }

    [Fact]
    public void BessWriterAppendsBlocksAndRoundTripsThroughReader()
    {
        using var stream = new MemoryStream();
        stream.Write("native"u8);
        BessWriter.Write(stream, new[]
        {
            new BessBlock("NAME", "Craterboy"u8.ToArray()),
            new BessBlock("FUTR", new byte[] { 1, 2, 3 }),
            new BessBlock("FUTR", new byte[] { 4, 5 }),
            new BessBlock("CORE", new byte[] { 1, 2, 3 }),
        });
        var endPosition = stream.Position;
        stream.Position = 0;

        var blocks = BessReader.Read(stream);

        Assert.Equal(new[] { "NAME", "FUTR", "FUTR", "CORE", "END " }, blocks.Select(block => block.Identifier));
        Assert.Equal(new byte[] { 1, 2, 3 }, blocks[1].Payload.ToArray());
        Assert.Equal(new byte[] { 4, 5 }, blocks[2].Payload.ToArray());
        Assert.Equal(endPosition, stream.Length);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void BessWriterRejectsInvalidBlockSequencesBeforeWriting()
    {
        foreach (var blocks in new[]
        {
            new[] { new BessBlock("NAME", ReadOnlyMemory<byte>.Empty) },
            new[] { new BessBlock("MBC ", ReadOnlyMemory<byte>.Empty), new BessBlock("CORE", ReadOnlyMemory<byte>.Empty) },
            new[] { new BessBlock("CORE", ReadOnlyMemory<byte>.Empty), new BessBlock("END ", ReadOnlyMemory<byte>.Empty) },
            new[] { new BessBlock("éééé", ReadOnlyMemory<byte>.Empty), new BessBlock("CORE", ReadOnlyMemory<byte>.Empty) },
            new[] { new BessBlock("NAME", ReadOnlyMemory<byte>.Empty), new BessBlock("NAME", ReadOnlyMemory<byte>.Empty), new BessBlock("CORE", ReadOnlyMemory<byte>.Empty) },
        })
        {
            using var destination = new MemoryStream();
            Assert.Throws<ArgumentException>(() => BessWriter.Write(destination, blocks));
            Assert.Equal(0, destination.Length);
        }
    }

    [Fact]
    public void BessReaderRejectsDuplicateKnownBlocks()
    {
        using var source = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "MBC ", new byte[3]);
            WriteBessBlock(stream, "MBC ", new byte[3]);
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });

        Assert.Throws<InvalidDataException>(() => BessReader.Read(source));
    }

    [Fact]
    public void BessWriterSerializesCoreMetadataForRoundTrip()
    {
        var core = new BessCore(
            1,
            3,
            "GD  ",
            0x1234,
            0xB0F0,
            0x5678,
            0x9ABC,
            0xDEF0,
            0xFFFE,
            true,
            0x1F,
            1,
            Enumerable.Range(0, 0x80).Select(value => (byte)value).ToArray(),
            default,
            default,
            default,
            default,
            default,
            default,
            default);

        using var stream = new MemoryStream();
        BessWriter.Write(stream, new[] { BessWriter.CreateCoreBlock(core) });
        stream.Position = 0;

        var parsed = BessReader.ReadCore(stream);

        Assert.Equal(core.MajorVersion, parsed.MajorVersion);
        Assert.Equal(core.MinorVersion, parsed.MinorVersion);
        Assert.Equal(core.ModelIdentifier, parsed.ModelIdentifier);
        Assert.Equal(core.Pc, parsed.Pc);
        Assert.Equal(core.Af, parsed.Af);
        Assert.Equal(core.Hl, parsed.Hl);
        Assert.Equal(core.Sp, parsed.Sp);
        Assert.Equal(core.Ime, parsed.Ime);
        Assert.Equal(core.Ie, parsed.Ie);
        Assert.Equal(core.ExecutionMode, parsed.ExecutionMode);
        Assert.Equal(core.IoRegisters.ToArray(), parsed.IoRegisters.ToArray());
    }

    [Fact]
    public void BessWriterLaysOutCoreBuffersForReaderExtraction()
    {
        var core = new BessCore(
            1, 0, "GD  ", 0, 0, 0, 0, 0, 0, false, 0, 0, new byte[0x80],
            default, default, default, default, default, default, default);
        var buffers = new BessCoreBuffers(
            new byte[] { 1, 2 },
            new byte[] { 3 },
            Array.Empty<byte>(),
            new byte[] { 4, 5 },
            new byte[] { 6 },
            new byte[] { 7, 8 },
            new byte[] { 9 });

        using var stream = new MemoryStream();
        BessWriter.WriteCoreWithBuffers(stream, core, buffers);
        stream.Position = 0;

        var parsed = BessReader.ReadCore(stream);

        Assert.Equal((uint)buffers.Ram.Length, parsed.Ram.Size);
        Assert.Equal(0u, parsed.Ram.Offset);
        Assert.Equal((uint)buffers.Vram.Length, parsed.Vram.Size);
        Assert.Equal(2u, parsed.Vram.Offset);
        Assert.Equal(default, parsed.MbcRam);
        stream.Position = 0;
        Assert.Equal(buffers.Oam.ToArray(), BessReader.ReadBuffer(stream, parsed.Oam));
        stream.Position = 0;
        Assert.Equal(buffers.Hram.ToArray(), BessReader.ReadBuffer(stream, parsed.Hram));
        stream.Position = 0;
        Assert.Equal(buffers.BackgroundPalettes.ToArray(), BessReader.ReadBuffer(stream, parsed.BackgroundPalettes));
        stream.Position = 0;
        Assert.Equal(buffers.ObjectPalettes.ToArray(), BessReader.ReadBuffer(stream, parsed.ObjectPalettes));
    }

    [Fact]
    public void BessWriterCombinesCoreBuffersWithTypedOptionalBlocks()
    {
        var core = new BessCore(1, 0, "GD  ", 0, 0, 0, 0, 0, 0, false, 0, 0, new byte[0x80], default, default, default, default, default, default, default);
        var buffers = new BessCoreBuffers(new byte[] { 1, 2 }, default, default, default, default, default, default);
        var infoState = new BessInfo("CRATERBOY       "u8.ToArray(), 0xA55A);
        var info = BessWriter.CreateInfoBlock(infoState);
        var writes = new[] { new BessMbcWrite(0x2000, 3) };

        using var stream = new MemoryStream();
        BessWriter.WriteCoreWithBuffers(
            stream,
            core,
            buffers,
            new[] { BessWriter.CreateNameBlock("Craterboy"), info },
            new[] { BessWriter.CreateMbcBlock(writes) });
        stream.Position = 0;

        Assert.Equal("Craterboy", BessReader.ReadName(stream));
        stream.Position = 0;
        var parsedInfo = BessReader.ReadInfo(stream);
        Assert.NotNull(parsedInfo);
        Assert.Equal(infoState.Title.ToArray(), parsedInfo!.Value.Title.ToArray());
        Assert.Equal(infoState.GlobalChecksum, parsedInfo.Value.GlobalChecksum);
        stream.Position = 0;
        Assert.Equal(writes, BessReader.ReadMbc(stream));
    }

    [Fact]
    public void BessWriterSerializesInfoAndNameMetadataForRoundTrip()
    {
        var info = new BessInfo("CRATERBOY       "u8.ToArray(), 0xA55A);
        var core = new BessCore(1, 0, "GD  ", 0, 0, 0, 0, 0, 0, false, 0, 0, new byte[0x80], default, default, default, default, default, default, default);

        using var stream = new MemoryStream();
        BessWriter.Write(stream, new[]
        {
            BessWriter.CreateNameBlock("Craterboy v0.1"),
            BessWriter.CreateInfoBlock(info),
            BessWriter.CreateCoreBlock(core),
        });

        stream.Position = 0;
        Assert.Equal("Craterboy v0.1", BessReader.ReadName(stream));
        stream.Position = 0;
        var parsedInfo = BessReader.ReadInfo(stream);
        Assert.NotNull(parsedInfo);
        Assert.Equal(info.Title.ToArray(), parsedInfo!.Value.Title.ToArray());
        Assert.Equal(info.GlobalChecksum, parsedInfo.Value.GlobalChecksum);
    }

    [Fact]
    public void BessWriterRejectsInvalidInfoAndNameMetadata()
    {
        Assert.Throws<ArgumentException>(() => BessWriter.CreateInfoBlock(new BessInfo(new byte[0x0F], 0)));
        Assert.Throws<ArgumentException>(() => BessWriter.CreateNameBlock("Craterboy é"));
    }

    [Fact]
    public void BessWriterSerializesMbcWritesForRoundTrip()
    {
        var writes = new[]
        {
            new BessMbcWrite(0x2000, 3),
            new BessMbcWrite(0xA123, 0x5A),
        };

        var block = BessWriter.CreateMbcBlock(writes);
        using var stream = CreateBess((destination, _) =>
        {
            WriteBessBlock(destination, "CORE", Array.Empty<byte>());
            WriteBessBlock(destination, block.Identifier, block.Payload.ToArray());
            WriteBessBlock(destination, "END ", Array.Empty<byte>());
        });

        Assert.Equal(writes, BessReader.ReadMbc(stream));
    }

    [Fact]
    public void BessWriterRejectsInvalidMbcWrites()
    {
        Assert.Throws<ArgumentException>(() => BessWriter.CreateMbcBlock(new[] { new BessMbcWrite(0x8000, 0) }));
        Assert.Throws<ArgumentException>(() => BessWriter.CreateMbcBlock(Enumerable.Repeat(new BessMbcWrite(0, 0), 0x1000 / 3 + 1).ToArray()));
    }

    [Fact]
    public void BessWriterSerializesRtcStateForRoundTrip()
    {
        var rtc = new BessRtc(59, 58, 23, 255, 0x81, 1, 2, 3, 4, 0x40, 1_700_000_000);
        var block = BessWriter.CreateRtcBlock(rtc);
        using var stream = CreateBess((destination, _) =>
        {
            WriteBessBlock(destination, "CORE", Array.Empty<byte>());
            WriteBessBlock(destination, block.Identifier, block.Payload.ToArray());
            WriteBessBlock(destination, "END ", Array.Empty<byte>());
        });

        Assert.Equal(rtc, BessReader.ReadRtc(stream));
    }

    [Fact]
    public void BessWriterSerializesExtraOamForRoundTrip()
    {
        var extraOam = Enumerable.Range(0, 0x60).Select(value => (byte)value).ToArray();
        var block = BessWriter.CreateExtraOamBlock(extraOam);
        extraOam[0] = 0xFF;
        using var stream = CreateBess((destination, _) =>
        {
            WriteBessBlock(destination, "CORE", Array.Empty<byte>());
            WriteBessBlock(destination, block.Identifier, block.Payload.ToArray());
            WriteBessBlock(destination, "END ", Array.Empty<byte>());
        });

        var parsed = BessReader.ReadExtraOam(stream);
        Assert.NotNull(parsed);
        Assert.Equal((byte)0, parsed![0]);
        Assert.Equal(Enumerable.Range(1, 0x5F).Select(value => (byte)value), parsed.Skip(1));
    }

    [Fact]
    public void BessWriterRejectsInvalidExtraOamLength()
    {
        Assert.Throws<ArgumentException>(() => BessWriter.CreateExtraOamBlock(new byte[0x5F]));
        Assert.Throws<ArgumentException>(() => BessWriter.CreateExtraOamBlock(new byte[0x61]));
    }

    [Fact]
    public void BessWriterSerializesMbc7StateForRoundTrip()
    {
        var state = new BessMbc7(0x3F, 7, 0x1234, 0x5678, 0x9ABC, 0xDEF0);
        var block = BessWriter.CreateMbc7Block(state);
        using var stream = CreateBess((destination, _) =>
        {
            WriteBessBlock(destination, "CORE", Array.Empty<byte>());
            WriteBessBlock(destination, block.Identifier, block.Payload.ToArray());
            WriteBessBlock(destination, "END ", Array.Empty<byte>());
        });

        Assert.Equal(state, BessReader.ReadMbc7(stream));
    }

    [Fact]
    public void BessWriterRejectsMbc7ReservedFlags()
    {
        Assert.Throws<ArgumentException>(() => BessWriter.CreateMbc7Block(new BessMbc7(0x40, 0, 0, 0, 0, 0)));
    }

    [Fact]
    public void BessWriterSerializesHuc3StateForRoundTrip()
    {
        var state = new BessHuc3(1_700_000_000, 1234, 56, 1250, 57, true);
        var block = BessWriter.CreateHuc3Block(state);
        using var stream = CreateBess((destination, _) =>
        {
            WriteBessBlock(destination, "CORE", Array.Empty<byte>());
            WriteBessBlock(destination, block.Identifier, block.Payload.ToArray());
            WriteBessBlock(destination, "END ", Array.Empty<byte>());
        });

        Assert.Equal(state, BessReader.ReadHuc3(stream));
    }

    [Fact]
    public void BessWriterSerializesTpp1StateForRoundTrip()
    {
        var state = new BessTpp1(1_700_000_000, new byte[] { 1, 2, 3, 4 }, new byte[] { 5, 6, 7, 8 }, 0xA5);
        var block = BessWriter.CreateTpp1Block(state);
        using var stream = CreateBess((destination, _) =>
        {
            WriteBessBlock(destination, "CORE", Array.Empty<byte>());
            WriteBessBlock(destination, block.Identifier, block.Payload.ToArray());
            WriteBessBlock(destination, "END ", Array.Empty<byte>());
        });

        var parsed = BessReader.ReadTpp1(stream);
        Assert.NotNull(parsed);
        Assert.Equal(state.LastUnixSecond, parsed!.Value.LastUnixSecond);
        Assert.Equal(state.RealRtcData.ToArray(), parsed.Value.RealRtcData.ToArray());
        Assert.Equal(state.LatchedRtcData.ToArray(), parsed.Value.LatchedRtcData.ToArray());
        Assert.Equal(state.Mr4, parsed.Value.Mr4);
    }

    [Fact]
    public void BessWriterRejectsInvalidTpp1RtcDataLengths()
    {
        Assert.Throws<ArgumentException>(() => BessWriter.CreateTpp1Block(new BessTpp1(0, new byte[3], new byte[4], 0)));
        Assert.Throws<ArgumentException>(() => BessWriter.CreateTpp1Block(new BessTpp1(0, new byte[4], new byte[5], 0)));
    }

    [Fact]
    public void BessWriterSerializesSgbStateForRoundTrip()
    {
        var state = new BessSgb(
            new BessBufferDescriptor(0, 0),
            new BessBufferDescriptor(0, 0),
            new BessBufferDescriptor(0, 0),
            new BessBufferDescriptor(0, 0),
            new BessBufferDescriptor(0, 0),
            new BessBufferDescriptor(0, 0),
            new BessBufferDescriptor(0, 0),
            0x21);
        var block = BessWriter.CreateSgbBlock(state);
        using var stream = CreateBess((destination, _) =>
        {
            WriteBessBlock(destination, "CORE", Array.Empty<byte>());
            WriteBessBlock(destination, block.Identifier, block.Payload.ToArray());
            WriteBessBlock(destination, "END ", Array.Empty<byte>());
        });

        Assert.Equal(state, BessReader.ReadSgb(stream));
    }

    [Fact]
    public void BessWriterRejectsInvalidSgbMultiplayerState()
    {
        var invalid = new BessSgb(default, default, default, default, default, default, default, 0x50);
        Assert.Throws<ArgumentException>(() => BessWriter.CreateSgbBlock(invalid));
    }

    [Fact]
    public void BessWriterRejectsZeroSizedCoreAndSgbBuffersWithOffsets()
    {
        var invalidCore = new BessCore(1, 0, "GD  ", 0, 0, 0, 0, 0, 0, false, 0, 0, new byte[0x80], new BessBufferDescriptor(0, 1), default, default, default, default, default, default);
        Assert.Throws<ArgumentException>(() => BessWriter.CreateCoreBlock(invalidCore));

        var invalidSgb = new BessSgb(new BessBufferDescriptor(0, 1), default, default, default, default, default, default, 0x10);
        Assert.Throws<ArgumentException>(() => BessWriter.CreateSgbBlock(invalidSgb));
    }

    [Fact]
    public void BessWriterRejectsDescriptorRangesThatOverflowFileOffsets()
    {
        var invalidCore = new BessCore(1, 0, "GD  ", 0, 0, 0, 0, 0, 0, false, 0, 0, new byte[0x80], new BessBufferDescriptor(1, uint.MaxValue), default, default, default, default, default, default);
        Assert.Throws<ArgumentException>(() => BessWriter.CreateCoreBlock(invalidCore));

        var invalidSgb = new BessSgb(new BessBufferDescriptor(uint.MaxValue, 1), default, default, default, default, default, default, 0x10);
        Assert.Throws<ArgumentException>(() => BessWriter.CreateSgbBlock(invalidSgb));
    }

    [Theory]
    [InlineData("GD  ")]
    [InlineData("GDA ")]
    [InlineData("GM  ")]
    [InlineData("SN  ")]
    [InlineData("SP  ")]
    [InlineData("S2  ")]
    [InlineData("CC  ")]
    [InlineData("CCE ")]
    [InlineData("CA  ")]
    [InlineData("CAB ")]
    public void BessRoundTripsKnownModelIdentifiers(string model)
    {
        var core = new BessCore(1, 0, model, 0, 0, 0, 0, 0, 0, false, 0, 0, new byte[0x80], default, default, default, default, default, default, default);
        using var stream = new MemoryStream();
        BessWriter.Write(stream, new[] { BessWriter.CreateCoreBlock(core) });
        stream.Position = 0;

        Assert.Equal(model, BessReader.ReadCore(stream).ModelIdentifier);
    }

    [Fact]
    public void BessWriterRejectsInvalidCoreMetadata()
    {
        var invalidIo = new BessCore(1, 0, "GD  ", 0, 0, 0, 0, 0, 0, false, 0, 0, new byte[0x7F], default, default, default, default, default, default, default);
        Assert.Throws<ArgumentException>(() => BessWriter.CreateCoreBlock(invalidIo));

        var invalidModel = new BessCore(1, 0, "GDXX", 0, 0, 0, 0, 0, 0, false, 0, 0, new byte[0x80], default, default, default, default, default, default, default);
        Assert.Throws<ArgumentException>(() => BessWriter.CreateCoreBlock(invalidModel));

        var invalidFamily = invalidModel with { ModelIdentifier = "GZ  " };
        Assert.Throws<ArgumentException>(() => BessWriter.CreateCoreBlock(invalidFamily));
    }

    [Fact]
    public void BessReaderParsesCoreMetadataAndBufferDescriptors()
    {
        var core = new byte[0xD0];
        WriteUInt16(core, 0, 1);
        WriteUInt16(core, 2, 7);
        "GD  "u8.CopyTo(core.AsSpan(4));
        WriteUInt16(core, 8, 0x1234);
        WriteUInt16(core, 0x0A, 0xB0F0);
        WriteUInt16(core, 0x0C, 0x5678);
        WriteUInt16(core, 0x0E, 0x9ABC);
        WriteUInt16(core, 0x10, 0xDEF0);
        WriteUInt16(core, 0x12, 0xFFFE);
        core[0x14] = 1;
        core[0x15] = 0x1F;
        core[0x16] = 1;
        core[0x18] = 0x80;
        WriteUInt32(core, 0x98, 0x20);
        WriteUInt32(core, 0x9C, 0x10);
        WriteUInt32(core, 0xA0, 0x40);
        WriteUInt32(core, 0xA4, 0x30);

        using var source = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "CORE", core);
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });

        var parsed = BessReader.ReadCore(source);

        Assert.Equal((ushort)1, parsed.MajorVersion);
        Assert.Equal((ushort)7, parsed.MinorVersion);
        Assert.Equal("GD  ", parsed.ModelIdentifier);
        Assert.Equal((ushort)0x1234, parsed.Pc);
        Assert.Equal((ushort)0xB0F0, parsed.Af);
        Assert.True(parsed.Ime);
        Assert.Equal((byte)0x1F, parsed.Ie);
        Assert.Equal((byte)1, parsed.ExecutionMode);
        Assert.Equal((byte)0x80, parsed.IoRegisters.Span[0]);
        Assert.Equal(new BessBufferDescriptor(0x20, 0x10), parsed.Ram);
        Assert.Equal(new BessBufferDescriptor(0x40, 0x30), parsed.Vram);
    }

    [Fact]
    public void BessReaderRejectsInvalidCoreMetadata()
    {
        foreach (var mutate in new Action<byte[]>[]
        {
            core => WriteUInt16(core, 0, 2),
            core => core[4] = (byte)'X',
            core => core[7] = (byte)'X',
            core => core[0x16] = 3,
            core => core[0x17] = 1,
            core => WriteUInt32(core, 0x9C, uint.MaxValue),
            core =>
            {
                WriteUInt32(core, 0x98, 1);
                WriteUInt32(core, 0x9C, uint.MaxValue);
            },
        })
        {
            var core = new byte[0xD0];
            WriteUInt16(core, 0, 1);
            "GD  "u8.CopyTo(core.AsSpan(4));
            mutate(core);
            using var source = CreateBess((stream, _) =>
            {
                WriteBessBlock(stream, "CORE", core);
                WriteBessBlock(stream, "END ", Array.Empty<byte>());
            });
            Assert.Throws<InvalidDataException>(() => BessReader.ReadCore(source));
        }

        using var shortCore = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "CORE", new byte[0xCF]);
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });
        Assert.Throws<InvalidDataException>(() => BessReader.ReadCore(shortCore));
    }

    [Fact]
    public void BessReaderExtractsValidatedExternalBuffers()
    {
        using var source = new MemoryStream();
        source.Write("RAM!"u8);
        var blockOffset = source.Position;
        WriteBessBlock(source, "CORE", Array.Empty<byte>());
        WriteBessBlock(source, "END ", Array.Empty<byte>());
        WriteBessFooter(source, checked((uint)blockOffset));
        source.Position = 0;

        var buffer = BessReader.ReadBuffer(source, new BessBufferDescriptor(4, 0));

        Assert.Equal("RAM!"u8.ToArray(), buffer);
        Assert.True(source.CanRead);
        using var emptySource = new MemoryStream(source.ToArray());
        Assert.Empty(BessReader.ReadBuffer(emptySource, default));
    }

    [Fact]
    public void BessReaderReadsCoreAndOwnedBuffersAsOneSnapshot()
    {
        var core = new BessCore(1, 0, "GD  ", 0, 0, 0, 0, 0, 0, false, 0, 0, new byte[0x80], default, default, default, default, default, default, default);
        var buffers = new BessCoreBuffers(
            new byte[] { 1, 2 },
            new byte[] { 3 },
            Array.Empty<byte>(),
            new byte[] { 4 },
            new byte[] { 5, 6 },
            new byte[] { 7 },
            new byte[] { 8, 9 });
        using var source = new MemoryStream();
        BessWriter.WriteCoreWithBuffers(source, core, buffers);
        source.Position = 0;

        var snapshot = BessReader.ReadCoreWithBuffers(source);

        Assert.Equal(core.ModelIdentifier, snapshot.Core.ModelIdentifier);
        Assert.Equal(buffers.Ram.ToArray(), snapshot.Buffers.Ram.ToArray());
        Assert.Equal(buffers.Vram.ToArray(), snapshot.Buffers.Vram.ToArray());
        Assert.Equal(buffers.MbcRam.ToArray(), snapshot.Buffers.MbcRam.ToArray());
        Assert.Equal(buffers.Oam.ToArray(), snapshot.Buffers.Oam.ToArray());
        Assert.Equal(buffers.Hram.ToArray(), snapshot.Buffers.Hram.ToArray());
        Assert.Equal(buffers.BackgroundPalettes.ToArray(), snapshot.Buffers.BackgroundPalettes.ToArray());
        Assert.Equal(buffers.ObjectPalettes.ToArray(), snapshot.Buffers.ObjectPalettes.ToArray());
        Assert.True(source.CanRead);
    }

    [Fact]
    public void BessReaderReadsTypedOptionalBlocksAsOneSnapshot()
    {
        var core = new BessCore(1, 0, "GD  ", 0, 0, 0, 0, 0, 0, false, 0, 0, new byte[0x80], default, default, default, default, default, default, default);
        var info = new BessInfo("CRATERBOY       "u8.ToArray(), 0xA55A);
        var writes = new[] { new BessMbcWrite(0x2000, 3), new BessMbcWrite(0xA000, 0x5A) };
        using var source = new MemoryStream();
        BessWriter.WriteCoreWithBuffers(
            source,
            core,
            new BessCoreBuffers(new byte[] { 1, 2 }, default, default, default, default, default, default),
            new[] { BessWriter.CreateNameBlock("Craterboy"), BessWriter.CreateInfoBlock(info) },
            new[] { BessWriter.CreateMbcBlock(writes) });
        source.Position = 0;

        var snapshot = BessReader.ReadSnapshot(source);

        Assert.Equal("Craterboy", snapshot.Name);
        Assert.NotNull(snapshot.Info);
        Assert.Equal(info.Title.ToArray(), snapshot.Info!.Value.Title.ToArray());
        Assert.Equal(info.GlobalChecksum, snapshot.Info.Value.GlobalChecksum);
        Assert.Equal(writes, snapshot.Mbc);
        Assert.Equal(new byte[] { 1, 2 }, snapshot.Core.Buffers.Ram.ToArray());
        Assert.Null(snapshot.Rtc);
        Assert.Null(snapshot.Sgb);
        Assert.True(source.CanRead);
    }

    [Fact]
    public void BessReaderSnapshotValidatesSgbBufferBounds()
    {
        using var source = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "SGB ", CreateInvalidSgbBuffer());
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });

        Assert.Throws<InvalidDataException>(() => BessReader.ReadSnapshot(source));
    }

    [Fact]
    public void BessReaderSnapshotOwnsSgbBuffers()
    {
        var core = new BessCore(1, 0, "GD  ", 0, 0, 0, 0, 0, 0, false, 0, 0, new byte[0x80], default, default, default, default, default, default, default);
        var sgbBuffers = new[]
        {
            new byte[] { 1 }, new byte[] { 2, 3 }, new byte[] { 4 },
            new byte[] { 5 }, new byte[] { 6 }, new byte[] { 7, 8 }, new byte[] { 9 },
        };
        using var source = new MemoryStream();
        foreach (var buffer in sgbBuffers)
            source.Write(buffer);
        var descriptors = new BessBufferDescriptor[7];
        var offset = 0u;
        for (var index = 0; index < descriptors.Length; index++)
        {
            descriptors[index] = new BessBufferDescriptor((uint)sgbBuffers[index].Length, offset);
            offset += (uint)sgbBuffers[index].Length;
        }
        var sgb = BessWriter.CreateSgbBlock(new BessSgb(
            descriptors[0], descriptors[1], descriptors[2], descriptors[3],
            descriptors[4], descriptors[5], descriptors[6], 0x10));
        var blockOffset = checked((uint)source.Position);
        WriteBessBlock(source, "CORE", BessWriter.CreateCoreBlock(core).Payload.ToArray());
        WriteBessBlock(source, sgb.Identifier, sgb.Payload.ToArray());
        WriteBessBlock(source, "END ", Array.Empty<byte>());
        WriteBessFooter(source, blockOffset);
        source.Position = 0;

        var snapshot = BessReader.ReadSnapshot(source);

        Assert.NotNull(snapshot.SgbBuffers);
        Assert.Equal(sgbBuffers[0], snapshot.SgbBuffers!.Value.BorderTiles);
        Assert.Equal(sgbBuffers[1], snapshot.SgbBuffers.Value.BorderTilemap);
        Assert.Equal(sgbBuffers[5], snapshot.SgbBuffers.Value.AttributeMap);
        Assert.Equal(sgbBuffers[6], snapshot.SgbBuffers.Value.AttributeFiles);
    }

    [Fact]
    public void BessWriterLaysOutCoreAndSgbBuffersForSnapshotRoundTrip()
    {
        var core = new BessCore(1, 0, "GD  ", 0, 0, 0, 0, 0, 0, false, 0, 0, new byte[0x80], default, default, default, default, default, default, default);
        var sgb = new BessSgb(default, default, default, default, default, default, default, 0x10);
        var coreBuffers = new BessCoreBuffers(new byte[] { 1 }, default, default, default, default, default, default);
        var sgbBuffers = new BessSgbBuffers(
            new byte[] { 2 }, new byte[] { 3 }, new byte[] { 4 }, new byte[] { 5 },
            new byte[] { 6 }, new byte[] { 7 }, new byte[] { 8 });
        using var stream = new MemoryStream();

        BessWriter.WriteCoreAndSgbWithBuffers(stream, core, coreBuffers, sgb, sgbBuffers, Array.Empty<BessBlock>(), Array.Empty<BessBlock>());
        stream.Position = 0;
        var snapshot = BessReader.ReadSnapshot(stream);

        Assert.Equal(new byte[] { 1 }, snapshot.Core.Buffers.Ram.ToArray());
        Assert.NotNull(snapshot.SgbBuffers);
        Assert.Equal(sgbBuffers.BorderTiles, snapshot.SgbBuffers!.Value.BorderTiles);
        Assert.Equal(sgbBuffers.AttributeFiles, snapshot.SgbBuffers.Value.AttributeFiles);
    }

    [Fact]
    public void BessWriterRoundTripsAggregateSnapshot()
    {
        var core = new BessCore(1, 0, "GD  ", 0, 0, 0, 0, 0, 0, false, 0, 0, new byte[0x80], default, default, default, default, default, default, default);
        var sgb = new BessSgb(default, default, default, default, default, default, default, 0x10);
        var snapshot = new BessStateSnapshot(
            new BessCoreState(core, new BessCoreBuffers(new byte[] { 1 }, default, default, default, default, default, default)),
            new BessInfo("CRATERBOY       "u8.ToArray(), 0xA55A),
            "Craterboy",
            new[] { new BessMbcWrite(0x2000, 3) },
            null,
            null,
            null,
            null,
            null,
            sgb,
            new BessSgbBuffers(new byte[] { 2 }, new byte[] { 3 }, new byte[] { 4 }, new byte[] { 5 }, new byte[] { 6 }, new byte[] { 7 }, new byte[] { 8 }));
        using var stream = new MemoryStream();

        BessWriter.WriteSnapshot(stream, snapshot);
        stream.Position = 0;
        var restored = BessReader.ReadSnapshot(stream);

        Assert.Equal(snapshot.Name, restored.Name);
        Assert.Equal(snapshot.Mbc, restored.Mbc);
        Assert.Equal(snapshot.Core.Buffers.Ram.ToArray(), restored.Core.Buffers.Ram.ToArray());
        Assert.Equal(snapshot.SgbBuffers!.Value.BorderPalettes, restored.SgbBuffers!.Value.BorderPalettes);
    }

    [Fact]
    public void BessWriterRoundTripsEveryTypedOptionalSnapshotSection()
    {
        var core = new BessCore(1, 0, "GD  ", 0, 0, 0, 0, 0, 0, false, 0, 0, new byte[0x80], default, default, default, default, default, default, default);
        var snapshot = new BessStateSnapshot(
            new BessCoreState(core, new BessCoreBuffers(default, default, default, default, default, default, default)),
            new BessInfo("CRATERBOY       "u8.ToArray(), 0xA55A),
            "Craterboy",
            new[] { new BessMbcWrite(0x2000, 3) },
            new BessRtc(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 1_700_000_000),
            new byte[0x60],
            new BessMbc7(0x3F, 7, 0x1234, 0x5678, 0x9ABC, 0xDEF0),
            new BessHuc3(1_700_000_000, 1234, 56, 1250, 57, true),
            new BessTpp1(1_700_000_000, new byte[] { 1, 2, 3, 4 }, new byte[] { 5, 6, 7, 8 }, 0xA5),
            new BessSgb(default, default, default, default, default, default, default, 0x10),
            new BessSgbBuffers(new byte[] { 11 }, new byte[] { 12 }, new byte[] { 13 }, new byte[] { 14 }, new byte[] { 15 }, new byte[] { 16 }, new byte[] { 17 }));
        using var stream = new MemoryStream();

        BessWriter.WriteSnapshot(stream, snapshot);
        stream.Position = 0;
        var restored = BessReader.ReadSnapshot(stream);

        Assert.NotNull(restored.Info);
        Assert.Equal(snapshot.Info!.Value.GlobalChecksum, restored.Info!.Value.GlobalChecksum);
        Assert.Equal(snapshot.Name, restored.Name);
        Assert.Equal(snapshot.Mbc, restored.Mbc);
        Assert.Equal(snapshot.Rtc, restored.Rtc);
        Assert.Equal(snapshot.ExtraOam, restored.ExtraOam);
        Assert.Equal(snapshot.Mbc7, restored.Mbc7);
        Assert.Equal(snapshot.Huc3, restored.Huc3);
        Assert.Equal(snapshot.Tpp1!.Value.LastUnixSecond, restored.Tpp1!.Value.LastUnixSecond);
        Assert.Equal(snapshot.Tpp1.Value.RealRtcData.ToArray(), restored.Tpp1.Value.RealRtcData.ToArray());
        Assert.Equal(snapshot.SgbBuffers!.Value.AttributeFiles, restored.SgbBuffers!.Value.AttributeFiles);
    }

    [Fact]
    public void EmulatorSavesCoreMemoryAndRegistersAsBess()
    {
        var emulator = new Emulator(GameBoyModel.DmgB);
        emulator.LoadRom(MakeRom());
        emulator.WriteMemory(0xC000, 0x42);
        emulator.WriteMemory(0xFF80, 0x24);
        emulator.WriteMemory(0xFF0F, 0x03);

        using var stream = new MemoryStream();
        emulator.SaveBess(stream);
        stream.Position = 0;
        var snapshot = BessReader.ReadSnapshot(stream);

        Assert.Equal("GD  ", snapshot.Core.Core.ModelIdentifier);
        Assert.Equal(emulator.Registers.ProgramCounter, snapshot.Core.Core.Pc);
        Assert.Equal(new string(' ', 16), System.Text.Encoding.ASCII.GetString(snapshot.Info!.Value.Title.Span));
        Assert.Equal("Craterboy", snapshot.Name);
        Assert.Equal(0x42, snapshot.Core.Buffers.Ram.Span[0]);
        Assert.Equal(0x24, snapshot.Core.Buffers.Hram.Span[0]);
        Assert.Equal(0x03, snapshot.Core.Core.IoRegisters.Span[0x0F]);
        Assert.Null(snapshot.Sgb);
    }

    [Fact]
    public void EmulatorLoadsCoreMemoryAndRegistersFromBess()
    {
        var emulator = new Emulator(GameBoyModel.DmgB);
        emulator.LoadRom(MakeRom());
        emulator.WriteMemory(0xC000, 0x42);
        emulator.WriteMemory(0xFF80, 0x24);
        emulator.WriteMemory(0xFF0F, 0x03);
        using var stream = new MemoryStream();
        emulator.SaveBess(stream);
        var expected = emulator.Registers;

        emulator.WriteMemory(0xC000, 0x99);
        emulator.WriteMemory(0xFF80, 0x88);
        emulator.WriteMemory(0xFF0F, 0x77);
        emulator.LoadBess(new MemoryStream(stream.ToArray()));

        Assert.Equal(expected, emulator.Registers);
        Assert.Equal(0x42, emulator.ReadMemory(0xC000));
        Assert.Equal(0x24, emulator.ReadMemory(0xFF80));
        Assert.Equal(0x03, emulator.ReadMemory(0xFF0F) & 0x1F);
    }

    [Fact]
    public void EmulatorRejectsMismatchedBessWithoutMutatingState()
    {
        var emulator = new Emulator(GameBoyModel.DmgB);
        emulator.LoadRom(MakeRom());
        emulator.WriteMemory(0xC000, 0x42);
        var before = emulator.ComputeStateHash();
        using var stream = new MemoryStream();
        var snapshot = new BessStateSnapshot(
            new BessCoreState(
                new BessCore(1, 0, "GM  ", 0x100, 0, 0, 0, 0, 0, false, 0, 0, new byte[0x80], default, default, default, default, default, default, default),
                new BessCoreBuffers(new byte[0x8000], new byte[0x4000], Array.Empty<byte>(), new byte[0xA0], new byte[0x7F], default, default)),
            null, null, null, null, null, null, null, null, null, null);
        BessWriter.WriteSnapshot(stream, snapshot);

        Assert.Throws<InvalidDataException>(() => emulator.LoadBess(new MemoryStream(stream.ToArray())));
        Assert.Equal(before, emulator.ComputeStateHash());
    }

    [Fact]
    public void EmulatorLoadsBessMapperWritesAfterCoreState()
    {
        var rom = MakeRom(type: 0x03, romSizeCode: 1, ramSizeCode: 2);
        rom[0x4000] = 1;
        rom[0x8000] = 2;
        rom[0xC000] = 3;
        var emulator = NewEmulator(rom);
        using var saved = new MemoryStream();
        emulator.SaveBess(saved);
        saved.Position = 0;
        var core = BessReader.ReadSnapshot(saved).Core;
        var snapshot = new BessStateSnapshot(
            core,
            null,
            null,
            new[] { new BessMbcWrite(0x2000, 3) },
            null,
            null,
            null,
            null,
            null,
            null,
            null);
        using var withMapperState = new MemoryStream();
        BessWriter.WriteSnapshot(withMapperState, snapshot);

        emulator.WriteMemory(0x2000, 2);
        Assert.Equal(2, emulator.ReadMemory(0x4000));
        emulator.LoadBess(new MemoryStream(withMapperState.ToArray()));

        Assert.Equal(3, emulator.ReadMemory(0x4000));
    }

    [Fact]
    public void BessBufferWritersPreflightOrderingBeforeWritingExternalData()
    {
        var core = new BessCore(1, 0, "GD  ", 0, 0, 0, 0, 0, 0, false, 0, 0, new byte[0x80], default, default, default, default, default, default, default);
        using var coreStream = new MemoryStream();
        Assert.Throws<ArgumentException>(() => BessWriter.WriteCoreWithBuffers(
            coreStream,
            core,
            new BessCoreBuffers(new byte[] { 1 }, default, default, default, default, default, default),
            Array.Empty<BessBlock>(),
            new[] { BessWriter.CreateNameBlock("invalid-after-core") }));
        Assert.Equal(0, coreStream.Length);

        using var sgbStream = new MemoryStream();
        Assert.Throws<ArgumentException>(() => BessWriter.WriteCoreAndSgbWithBuffers(
            sgbStream,
            core,
            default,
            new BessSgb(default, default, default, default, default, default, default, 0x10),
            default,
            Array.Empty<BessBlock>(),
            new[] { BessWriter.CreateNameBlock("invalid-after-sgb") }));
        Assert.Equal(0, sgbStream.Length);
    }

    [Fact]
    public void BessReaderParsesOptionalInfoMetadata()
    {
        var info = new byte[0x12];
        "CRATERBOY       "u8.CopyTo(info);
        WriteUInt16(info, 0x10, 0xA55A);
        using var source = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "INFO", info);
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });

        var parsed = BessReader.ReadInfo(source);

        Assert.NotNull(parsed);
        Assert.Equal("CRATERBOY       ", System.Text.Encoding.ASCII.GetString(parsed.Value.Title.Span));
        Assert.Equal((ushort)0xA55A, parsed.Value.GlobalChecksum);
        Assert.True(source.CanRead);

        using var noInfo = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });
        Assert.Null(BessReader.ReadInfo(noInfo));
    }

    [Fact]
    public void BessReaderRejectsInvalidInfoLength()
    {
        using var source = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "INFO", new byte[0x11]);
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });

        Assert.Throws<InvalidDataException>(() => BessReader.ReadInfo(source));
    }

    [Fact]
    public void BessReaderParsesOptionalNameMetadata()
    {
        using var source = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "NAME", "SameBoy v1.0.3"u8.ToArray());
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });

        Assert.Equal("SameBoy v1.0.3", BessReader.ReadName(source));
        Assert.True(source.CanRead);

        using var noName = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });
        Assert.Null(BessReader.ReadName(noName));
    }

    [Fact]
    public void BessReaderRejectsNonAsciiNameMetadata()
    {
        using var source = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "NAME", new byte[] { (byte)'S', 0xFF });
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });

        Assert.Throws<InvalidDataException>(() => BessReader.ReadName(source));
    }

    [Fact]
    public void BessReaderParsesOptionalMbcWritesInOrder()
    {
        using var source = CreateBess((stream, _) =>
        {
            var mbc = new byte[6];
            WriteUInt16(mbc, 0, 0x0000);
            mbc[2] = 0x0A;
            WriteUInt16(mbc, 3, 0x4000);
            mbc[5] = 0x03;
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "MBC ", mbc);
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });

        var writes = BessReader.ReadMbc(source);

        Assert.NotNull(writes);
        Assert.Equal(new[] { new BessMbcWrite(0x0000, 0x0A), new BessMbcWrite(0x4000, 0x03) }, writes);
        Assert.True(source.CanRead);

        using var noMbc = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });
        Assert.Null(BessReader.ReadMbc(noMbc));
    }

    [Fact]
    public void BessReaderRejectsMalformedMbcWrites()
    {
        foreach (var payload in new[] { new byte[] { 0 }, new byte[] { 0x00, 0x80, 0x01 } })
        {
            using var source = CreateBess((stream, _) =>
            {
                WriteBessBlock(stream, "CORE", Array.Empty<byte>());
                WriteBessBlock(stream, "MBC ", payload);
                WriteBessBlock(stream, "END ", Array.Empty<byte>());
            });

            Assert.Throws<InvalidDataException>(() => BessReader.ReadMbc(source));
        }
    }

    [Fact]
    public void BessReaderParsesOptionalRtcMetadata()
    {
        var rtc = new byte[0x30];
        rtc[0] = 59;
        rtc[4] = 58;
        rtc[8] = 23;
        rtc[0x0C] = 255;
        rtc[0x10] = 0x81;
        rtc[0x14] = 1;
        rtc[0x18] = 2;
        rtc[0x1C] = 3;
        rtc[0x20] = 4;
        rtc[0x24] = 0x40;
        WriteUInt64(rtc, 0x28, 1_700_000_000);
        using var source = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "RTC ", rtc);
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });

        var parsed = BessReader.ReadRtc(source);

        Assert.Equal(new BessRtc(59, 58, 23, 255, 0x81, 1, 2, 3, 4, 0x40, 1_700_000_000), parsed);
        Assert.True(source.CanRead);

        using var noRtc = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });
        Assert.Null(BessReader.ReadRtc(noRtc));
    }

    [Fact]
    public void BessReaderRejectsInvalidRtcLength()
    {
        using var source = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "RTC ", new byte[0x2F]);
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });

        Assert.Throws<InvalidDataException>(() => BessReader.ReadRtc(source));
    }

    [Fact]
    public void BessReaderParsesOptionalExtraOam()
    {
        var extraOam = Enumerable.Range(0, 0x60).Select(value => (byte)value).ToArray();
        using var source = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "XOAM", extraOam);
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });

        var parsed = BessReader.ReadExtraOam(source);

        Assert.Equal(extraOam, parsed);
        Assert.True(source.CanRead);

        using var noExtraOam = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });
        Assert.Null(BessReader.ReadExtraOam(noExtraOam));
    }

    [Fact]
    public void BessReaderRejectsInvalidExtraOamLength()
    {
        using var source = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "XOAM", new byte[0x5F]);
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });

        Assert.Throws<InvalidDataException>(() => BessReader.ReadExtraOam(source));
    }

    [Fact]
    public void BessReaderParsesOptionalMbc7State()
    {
        var mbc7 = new byte[0x0A];
        mbc7[0] = 0x3F;
        mbc7[1] = 7;
        WriteUInt16(mbc7, 2, 0x1234);
        WriteUInt16(mbc7, 4, 0x5678);
        WriteUInt16(mbc7, 6, 0x9ABC);
        WriteUInt16(mbc7, 8, 0xDEF0);
        using var source = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "MBC7", mbc7);
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });

        var parsed = BessReader.ReadMbc7(source);

        Assert.Equal(new BessMbc7(0x3F, 7, 0x1234, 0x5678, 0x9ABC, 0xDEF0), parsed);
        Assert.True(source.CanRead);

        using var noMbc7 = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });
        Assert.Null(BessReader.ReadMbc7(noMbc7));
    }

    [Fact]
    public void BessReaderRejectsMalformedMbc7State()
    {
        foreach (var payload in new[] { new byte[9], new byte[] { 0x40, 0, 0, 0, 0, 0, 0, 0, 0, 0 } })
        {
            using var source = CreateBess((stream, _) =>
            {
                WriteBessBlock(stream, "CORE", Array.Empty<byte>());
                WriteBessBlock(stream, "MBC7", payload);
                WriteBessBlock(stream, "END ", Array.Empty<byte>());
            });

            Assert.Throws<InvalidDataException>(() => BessReader.ReadMbc7(source));
        }
    }

    [Fact]
    public void BessReaderParsesOptionalHuc3State()
    {
        var huc3 = new byte[0x11];
        WriteUInt64(huc3, 0, 1_700_000_000);
        WriteUInt16(huc3, 8, 1234);
        WriteUInt16(huc3, 0x0A, 56);
        WriteUInt16(huc3, 0x0C, 1250);
        WriteUInt16(huc3, 0x0E, 57);
        huc3[0x10] = 1;
        using var source = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "HUC3", huc3);
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });

        var parsed = BessReader.ReadHuc3(source);

        Assert.Equal(new BessHuc3(1_700_000_000, 1234, 56, 1250, 57, true), parsed);
        Assert.True(source.CanRead);

        using var noHuc3 = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });
        Assert.Null(BessReader.ReadHuc3(noHuc3));
    }

    [Fact]
    public void BessReaderRejectsMalformedHuc3State()
    {
        foreach (var payload in new[]
        {
            new byte[0x10],
            new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2 },
        })
        {
            using var source = CreateBess((stream, _) =>
            {
                WriteBessBlock(stream, "CORE", Array.Empty<byte>());
                WriteBessBlock(stream, "HUC3", payload);
                WriteBessBlock(stream, "END ", Array.Empty<byte>());
            });

            Assert.Throws<InvalidDataException>(() => BessReader.ReadHuc3(source));
        }
    }

    [Fact]
    public void BessReaderParsesOptionalTpp1State()
    {
        var tpp1 = new byte[0x11];
        WriteUInt64(tpp1, 0, 1_700_000_000);
        new byte[] { 1, 2, 3, 4 }.CopyTo(tpp1, 8);
        new byte[] { 5, 6, 7, 8 }.CopyTo(tpp1, 0x0C);
        tpp1[0x10] = 0xA5;
        using var source = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "TPP1", tpp1);
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });

        var parsed = BessReader.ReadTpp1(source);

        Assert.NotNull(parsed);
        Assert.Equal((ulong)1_700_000_000, parsed.Value.LastUnixSecond);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, parsed.Value.RealRtcData.ToArray());
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, parsed.Value.LatchedRtcData.ToArray());
        Assert.Equal((byte)0xA5, parsed.Value.Mr4);
        Assert.True(source.CanRead);

        using var noTpp1 = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });
        Assert.Null(BessReader.ReadTpp1(noTpp1));
    }

    [Fact]
    public void BessReaderRejectsInvalidTpp1Length()
    {
        using var source = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "TPP1", new byte[0x10]);
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });

        Assert.Throws<InvalidDataException>(() => BessReader.ReadTpp1(source));
    }

    [Fact]
    public void BessReaderParsesOptionalSgbState()
    {
        var sgb = new byte[0x39];
        WriteUInt32(sgb, 0, 4);
        WriteUInt32(sgb, 4, 0);
        WriteUInt32(sgb, 8, 4);
        WriteUInt32(sgb, 0x0C, 4);
        sgb[0x38] = 0x20;
        using var source = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "SGB ", sgb);
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });

        var parsed = BessReader.ReadSgb(source);

        Assert.NotNull(parsed);
        Assert.Equal(new BessBufferDescriptor(4, 0), parsed.Value.BorderTiles);
        Assert.Equal(new BessBufferDescriptor(4, 4), parsed.Value.BorderTilemap);
        Assert.Equal((byte)0x20, parsed.Value.MultiplayerState);
        Assert.True(source.CanRead);

        using var noSgb = CreateBess((stream, _) =>
        {
            WriteBessBlock(stream, "CORE", Array.Empty<byte>());
            WriteBessBlock(stream, "END ", Array.Empty<byte>());
        });
        Assert.Null(BessReader.ReadSgb(noSgb));
    }

    [Fact]
    public void BessReaderRejectsMalformedSgbState()
    {
        foreach (var payload in new[] { new byte[0x38], CreateInvalidSgbState(0x30), CreateInvalidSgbState(0x22), CreateInvalidSgbBuffer() })
        {
            using var source = CreateBess((stream, _) =>
            {
                WriteBessBlock(stream, "CORE", Array.Empty<byte>());
                WriteBessBlock(stream, "SGB ", payload);
                WriteBessBlock(stream, "END ", Array.Empty<byte>());
            });

            Assert.Throws<InvalidDataException>(() => BessReader.ReadSgb(source));
        }
    }

    private static byte[] CreateInvalidSgbState(byte multiplayerState)
    {
        var payload = new byte[0x39];
        payload[0x38] = multiplayerState;
        return payload;
    }

    private static byte[] CreateInvalidSgbBuffer()
    {
        var payload = CreateInvalidSgbState(0x10);
        WriteUInt32(payload, 0, 1);
        WriteUInt32(payload, 4, uint.MaxValue);
        return payload;
    }

    private static void WriteUInt16(byte[] destination, int offset, ushort value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(offset), value);

    private static void WriteUInt32(byte[] destination, int offset, uint value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination.AsSpan(offset), value);

    private static void WriteUInt64(byte[] destination, int offset, ulong value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(destination.AsSpan(offset), value);

    private static MemoryStream CreateBess(Action<MemoryStream, long> writeBlocks)
    {
        var stream = new MemoryStream();
        var offset = stream.Position;
        writeBlocks(stream, offset);
        WriteBessFooter(stream, checked((uint)offset));
        stream.Position = 0;
        return stream;
    }

    private static void WriteBessBlock(Stream stream, string identifier, byte[] payload)
    {
        stream.Write(System.Text.Encoding.ASCII.GetBytes(identifier));
        stream.Write(BitConverter.GetBytes(payload.Length));
        stream.Write(payload);
    }

    private static void WriteBessFooter(Stream stream, uint blockOffset)
    {
        stream.Write(BitConverter.GetBytes(blockOffset));
        stream.Write("BESS"u8);
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

    [Theory]
    [InlineData(GameBoyModel.DmgB)]
    [InlineData(GameBoyModel.Mgb)]
    [InlineData(GameBoyModel.CgbE)]
    public void InputRecordingReplayReproducesDeterministicCheckpoint(GameBoyModel model)
    {
        var rom = MakeRom();
        var first = NewEmulator(rom, model);
        var second = NewEmulator(rom, model);
        first.WriteMemory(0xFF00, 0x10);
        second.WriteMemory(0xFF00, 0x10);
        var recording = new InputRecording();
        recording.Add(new InputEvent(20, GameBoyButton.A, true));
        recording.Add(new InputEvent(40, GameBoyButton.A, false));
        recording.Add(new InputEvent(64, GameBoyButton.Start, true));

        first.ReplayInputRecording(recording);
        second.ReplayInputRecording(recording);

        Assert.Equal(64, first.CycleCount);
        Assert.Equal(first.CycleCount, second.CycleCount);
        Assert.Equal(first.ComputeStateHash(), second.ComputeStateHash());
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

    [Theory]
    [InlineData(GameBoyModel.CgbB, (byte)0xF0)]
    [InlineData(GameBoyModel.CgbC, (byte)0xF4)]
    public void ApuChannelThreeLengthEnableCanConsumeTheDividerEdgeTick(GameBoyModel model, byte expectedStatus)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF1A, 0x80);
        emulator.WriteMemory(0xFF1C, 0x20);
        emulator.WriteMemory(0xFF1B, 0xFF); // one length tick
        emulator.WriteMemory(0xFF1E, 0x80); // trigger without length
        emulator.WriteMemory(0xFF1E, 0x00); // clear length while divider bit is high

        Assert.Equal(expectedStatus, emulator.PeekMemory(0xFF26));
    }

    [Fact]
    public void DmgWaveRetriggerCopiesTheCurrentSampleGroupToWaveByteZero()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF30, 0x10);
        emulator.WriteMemory(0xFF31, 0x20);
        emulator.WriteMemory(0xFF1A, 0x80);
        emulator.WriteMemory(0xFF1D, 0x00);
        emulator.WriteMemory(0xFF1E, 0x81);

        emulator.RunCycles(95 * 2);
        emulator.WriteMemory(0xFF1E, 0x81);
        emulator.WriteMemory(0xFF1A, 0x00);

        Assert.Equal((byte)0x20, emulator.PeekMemory(0xFF30));
    }

    [Theory]
    [InlineData(GameBoyModel.DmgB)]
    [InlineData(GameBoyModel.Mgb)]
    public void WaveRetriggerCopiesACompleteLaterSampleGroup(GameBoyModel model)
    {
        var emulator = NewEmulator(MakeRom(), model);
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF30, 0x10);
        emulator.WriteMemory(0xFF31, 0x20);
        emulator.WriteMemory(0xFF34, 0x50);
        emulator.WriteMemory(0xFF35, 0x60);
        emulator.WriteMemory(0xFF1A, 0x80);
        emulator.WriteMemory(0xFF1D, 0x00);
        emulator.WriteMemory(0xFF1E, 0x81);

        emulator.RunCycles(95 * 8);
        emulator.WriteMemory(0xFF1E, 0x81);
        emulator.WriteMemory(0xFF1A, 0x00);

        Assert.Equal((byte)0x50, emulator.PeekMemory(0xFF30));
        Assert.Equal((byte)0x60, emulator.PeekMemory(0xFF31));
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

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void StateHashIncludesCgbFamilyPaletteRam(GameBoyModel model)
    {
        var first = NewEmulator(MakeRom(), model);
        var second = NewEmulator(MakeRom(), model);

        second.WriteMemory(0xFF68, 0x00);
        second.WriteMemory(0xFF69, 0x7F);

        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void StateHashIncludesCgbObjectPaletteRam(GameBoyModel model)
    {
        var first = NewEmulator(MakeRom(), model);
        var second = NewEmulator(MakeRom(), model);

        second.WriteMemory(0xFF6A, 0x00);
        second.WriteMemory(0xFF6B, 0x7F);

        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Theory]
    [InlineData(GameBoyModel.DmgB)]
    [InlineData(GameBoyModel.Mgb)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void StateHashIncludesPpuWindowProgress(GameBoyModel model)
    {
        var first = NewEmulator(MakeRom(), model);
        var second = NewEmulator(MakeRom(), model);
        first.WriteMemory(0xFF40, 0xB1); // LCD, BG, and window enabled
        first.RunCycles(456);
        first.WriteMemory(0xFF40, 0x91); // match second's final LCD control
        second.WriteMemory(0xFF40, 0x91);
        second.RunCycles(456);

        Assert.Equal(first.CycleCount, second.CycleCount);
        Assert.Equal(first.PeekMemory(0xFF40), second.PeekMemory(0xFF40));
        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Theory]
    [InlineData(GameBoyModel.DmgB)]
    [InlineData(GameBoyModel.Mgb)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void StateHashIncludesTimerDividerPrecision(GameBoyModel model)
    {
        var first = NewEmulator(MakeRom(), model);
        var second = NewEmulator(MakeRom(), model);
        first.RunCycles(1000);
        second.RunCycles(64);
        second.WriteMemory(0xFF04, 0); // reset divider without changing cycle count
        second.RunCycles(936);

        Assert.Equal(first.CycleCount, second.CycleCount);
        Assert.Equal(first.PeekMemory(0xFF04), second.PeekMemory(0xFF04));
        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Theory]
    [InlineData(GameBoyModel.DmgB)]
    [InlineData(GameBoyModel.Mgb)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void StateHashIncludesApuPhaseAndSampleState(GameBoyModel model)
    {
        var first = NewEmulator(MakeRom(), model);
        var second = NewEmulator(MakeRom(), model);
        ConfigurePulseChannel(first);
        ConfigurePulseChannel(second);
        first.RunCycles(200);
        second.RunCycles(100);
        second.WriteMemory(0xFF14, 0x80); // retrigger at a different phase
        second.RunCycles(100);

        Assert.Equal(first.CycleCount, second.CycleCount);
        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Theory]
    [InlineData(GameBoyModel.DmgB)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void StateHashIncludesApuMixerState(GameBoyModel model)
    {
        var rom = MakeRom();
        var first = NewEmulator(rom, model);
        var second = NewEmulator(rom, model);
        first.WriteMemory(0xFF26, 0x80);
        second.WriteMemory(0xFF26, 0x80);
        second.WriteMemory(0xFF24, 0x77); // NR50 mixer volumes
        second.WriteMemory(0xFF25, 0xF0); // NR51 channel routing

        Assert.Equal((byte)0x77, second.PeekMemory(0xFF24));
        Assert.Equal((byte)0xF0, second.PeekMemory(0xFF25));
        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Theory]
    [InlineData(GameBoyModel.DmgB)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void StateHashIncludesSerialTransferProgress(GameBoyModel model)
    {
        var first = NewEmulator(MakeRom(), model);
        var second = NewEmulator(MakeRom(), model);
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

    [Theory]
    [InlineData(GameBoyModel.DmgB)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void StateHashIncludesOamDmaProgress(GameBoyModel model)
    {
        var first = NewEmulator(MakeRom(), model);
        var second = NewEmulator(MakeRom(), model);
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

    [Theory]
    [InlineData(GameBoyModel.DmgB, true)]
    [InlineData(GameBoyModel.Mgb, true)]
    [InlineData(GameBoyModel.CgbE, false)]
    [InlineData(GameBoyModel.AgbA, false)]
    [InlineData(GameBoyModel.GbpA, false)]
    public void StateHashTracksModelSpecificJoypadSelectionProgress(GameBoyModel model, bool expectPendingState)
    {
        var first = NewEmulator(MakeRom(), model);
        var second = NewEmulator(MakeRom(), model);
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
        if (expectPendingState)
            Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
        else
            Assert.Equal(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Theory]
    [InlineData(GameBoyModel.DmgB)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void StateHashIncludesCartridgeMapperState(GameBoyModel model)
    {
        var rom = MakeRom(type: 0x01, romSizeCode: 1, ramSizeCode: 2);
        var first = NewEmulator(rom, model);
        var second = NewEmulator(rom, model);

        second.WriteMemory(0x2000, 0x02); // select a different MBC1 ROM bank

        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Theory]
    [InlineData(GameBoyModel.DmgB)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void StateHashIncludesRomIdentity(GameBoyModel model)
    {
        var firstRom = MakeRom();
        var secondRom = MakeRom();
        secondRom[0x200] = 0xA5;
        FixChecksum(secondRom);

        var first = NewEmulator(firstRom, model);
        var second = NewEmulator(secondRom, model);

        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Theory]
    [InlineData(GameBoyModel.DmgB)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void StateHashIncludesBootRomIdentity(GameBoyModel model)
    {
        var first = new Emulator(model, new EmulatorOptions { SkipBootRom = false });
        var second = new Emulator(model, new EmulatorOptions { SkipBootRom = false });
        var rom = MakeRom();
        first.LoadRom(rom);
        second.LoadRom(rom);
        first.LoadBootRom(new byte[0x100]);
        var bootRom = new byte[0x100];
        bootRom[0xFF] = 0xA5;
        second.LoadBootRom(bootRom);

        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Theory]
    [InlineData(GameBoyModel.DmgB)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void StateHashIncludesBootRomMappingState(GameBoyModel model)
    {
        var rom = MakeRom();
        var mapped = new Emulator(model, new EmulatorOptions { SkipBootRom = false });
        var skipped = new Emulator(model, new EmulatorOptions { SkipBootRom = true });
        var bootRom = new byte[0x100];
        bootRom[0] = 0xA5;
        mapped.LoadRom(rom);
        skipped.LoadRom(rom);
        mapped.LoadBootRom(bootRom);
        skipped.LoadBootRom(bootRom);

        Assert.Equal((byte)0xA5, mapped.PeekMemory(0));
        Assert.Equal(rom[0], skipped.PeekMemory(0));
        Assert.NotEqual(mapped.ComputeStateHash(), skipped.ComputeStateHash());
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE, GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.CgbE, GameBoyModel.GbpA)]
    [InlineData(GameBoyModel.AgbA, GameBoyModel.GbpA)]
    public void StateHashIncludesModelIdentity(GameBoyModel firstModel, GameBoyModel secondModel)
    {
        var rom = MakeRom();
        var first = NewEmulator(rom, firstModel);
        var second = NewEmulator(rom, secondModel);

        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void StateHashIncludesCgbMemoryBankSelection(GameBoyModel model)
    {
        var rom = MakeRom();
        var first = NewEmulator(rom, model);
        var second = NewEmulator(rom, model);
        second.WriteMemory(0xFF4F, 0x01); // select VRAM bank 1
        second.WriteMemory(0xFF70, 0x02); // select WRAM bank 2

        Assert.Equal((byte)0xFE, first.PeekMemory(0xFF4F));
        Assert.Equal((byte)0xFF, second.PeekMemory(0xFF4F));
        Assert.NotEqual(first.PeekMemory(0xFF70), second.PeekMemory(0xFF70));
        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void StateHashIncludesCgbSpeedSwitchPreparation(GameBoyModel model)
    {
        var rom = MakeRom();
        var first = NewEmulator(rom, model);
        var second = NewEmulator(rom, model);
        second.WriteMemory(0xFF4D, 0x01); // prepare the next STOP for speed switching

        Assert.Equal((byte)0x7E, first.PeekMemory(0xFF4D));
        Assert.Equal((byte)0x7F, second.PeekMemory(0xFF4D));
        Assert.Equal(first.CycleCount, second.CycleCount);
        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void StateHashIncludesCgbCurrentSpeed(GameBoyModel model)
    {
        var rom = MakeRom();
        new byte[] { 0x10, 0x00 }.CopyTo(rom, 0x100); // STOP
        var first = NewEmulator(rom, model);
        var second = NewEmulator(rom, model);
        second.WriteMemory(0xFF4D, 0x01);
        first.StepInstruction();
        second.StepInstruction();

        Assert.Equal(first.CycleCount, second.CycleCount);
        Assert.Equal((byte)0x7E, first.PeekMemory(0xFF4D));
        Assert.Equal((byte)0xFE, second.PeekMemory(0xFF4D));
        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void StateHashIncludesCgbDmaProgress(GameBoyModel model)
    {
        var rom = MakeRom();
        var first = NewEmulator(rom, model);
        var second = NewEmulator(rom, model);
        first.WriteMemory(0xFF40, 0x80); // enable LCD timing
        second.WriteMemory(0xFF40, 0x80);
        second.WriteMemory(0xFF51, 0xC0);
        second.WriteMemory(0xFF53, 0x80);
        second.WriteMemory(0xFF55, 0x80); // one HBlank block
        first.RunCycles(252);
        second.RunCycles(252); // complete the block at the same HBlank

        Assert.Equal(first.CycleCount, second.CycleCount);
        Assert.Equal((byte)0xFF, first.PeekMemory(0xFF55));
        Assert.Equal((byte)0xFF, second.PeekMemory(0xFF55));
        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Theory]
    [InlineData(GameBoyModel.DmgB)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void StateHashIncludesHighRamState(GameBoyModel model)
    {
        var rom = MakeRom();
        var first = NewEmulator(rom, model);
        var second = NewEmulator(rom, model);
        second.WriteMemory(0xFF80, 0xA5);

        Assert.Equal((byte)0, first.PeekMemory(0xFF80));
        Assert.Equal((byte)0xA5, second.PeekMemory(0xFF80));
        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Theory]
    [InlineData(GameBoyModel.DmgB)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void StateHashIncludesInterruptRegisters(GameBoyModel model)
    {
        var rom = MakeRom();
        var first = NewEmulator(rom, model);
        var second = NewEmulator(rom, model);
        second.WriteMemory(0xFFFF, 0x1F); // enable all maskable interrupts
        second.WriteMemory(0xFF0F, 0x1F); // request all maskable interrupts

        Assert.Equal((byte)0, first.PeekMemory(0xFFFF));
        Assert.Equal((byte)0x1F, second.PeekMemory(0xFFFF));
        Assert.Equal((byte)0xE0, first.PeekMemory(0xFF0F));
        Assert.Equal((byte)0xFF, second.PeekMemory(0xFF0F));
        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Fact]
    public void StateHashIncludesDelayedImeBoundary()
    {
        var rom = MakeRom();
        new byte[] { 0xFB, 0x00 }.CopyTo(rom, 0x100); // EI, then NOP
        var first = NewEmulator(rom);
        var second = NewEmulator(rom);
        first.StepInstruction();
        second.StepInstruction();
        second.StepInstruction();

        Assert.False(first.Registers.InterruptMasterEnable);
        Assert.True(second.Registers.InterruptMasterEnable);
        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Fact]
    public void StateHashIncludesHaltState()
    {
        var rom = MakeRom();
        rom[0x100] = 0x76; // HALT
        var first = NewEmulator(rom);
        var second = NewEmulator(rom);
        first.StepInstruction();

        Assert.True(first.Registers.Halted);
        Assert.False(second.Registers.Halted);
        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Theory]
    [InlineData(GameBoyModel.DmgB)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void StateHashIncludesPpuLcdState(GameBoyModel model)
    {
        var rom = MakeRom();
        var first = NewEmulator(rom, model);
        var second = NewEmulator(rom, model);
        first.WriteMemory(0xFF40, 0x80); // enable LCD timing

        Assert.Equal((byte)0x80, first.PeekMemory(0xFF40));
        Assert.Equal((byte)0x00, second.PeekMemory(0xFF40));
        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Theory]
    [InlineData(GameBoyModel.DmgB)]
    [InlineData(GameBoyModel.Mgb)]
    public void StateHashIncludesDmgPaletteRegister(GameBoyModel model)
    {
        var rom = MakeRom();
        var first = NewEmulator(rom, model);
        var second = NewEmulator(rom, model);
        second.WriteMemory(0xFF47, 0xE4);

        Assert.Equal((byte)0x00, first.PeekMemory(0xFF47));
        Assert.Equal((byte)0xE4, second.PeekMemory(0xFF47));
        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void StateHashIncludesCgbPaletteIndexState(GameBoyModel model)
    {
        var rom = MakeRom();
        var first = NewEmulator(rom, model);
        var second = NewEmulator(rom, model);
        second.WriteMemory(0xFF68, 0x80); // select BG palette byte 0 with auto-increment

        Assert.Equal((byte)0xC0, second.PeekMemory(0xFF68));
        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Theory]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void StateHashIncludesCgbObjectPriorityMode(GameBoyModel model)
    {
        var rom = MakeRom();
        var first = NewEmulator(rom, model);
        var second = NewEmulator(rom, model);
        second.WriteMemory(0xFF6C, 0x01); // select X-coordinate priority

        Assert.Equal((byte)0xFE, first.PeekMemory(0xFF6C));
        Assert.Equal((byte)0xFF, second.PeekMemory(0xFF6C));
        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Theory]
    [InlineData(GameBoyModel.DmgB)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void StateHashIncludesPpuCoincidenceState(GameBoyModel model)
    {
        var rom = MakeRom();
        var first = NewEmulator(rom, model);
        var second = NewEmulator(rom, model);
        first.WriteMemory(0xFF40, 0x80);
        second.WriteMemory(0xFF40, 0x80);
        second.WriteMemory(0xFF45, 0x01); // match LY after one visible line
        first.RunCycles(456);
        second.RunCycles(456);

        Assert.Equal((byte)0x01, first.PeekMemory(0xFF44));
        Assert.Equal(first.PeekMemory(0xFF44), second.PeekMemory(0xFF44));
        Assert.Equal((byte)0x00, first.PeekMemory(0xFF45));
        Assert.Equal((byte)0x01, second.PeekMemory(0xFF45));
        Assert.NotEqual(first.ComputeStateHash(), second.ComputeStateHash());
    }

    [Theory]
    [InlineData(GameBoyModel.DmgB)]
    [InlineData(GameBoyModel.CgbE)]
    [InlineData(GameBoyModel.AgbA)]
    [InlineData(GameBoyModel.GbpA)]
    public void StateHashIncludesApuPowerState(GameBoyModel model)
    {
        var rom = MakeRom();
        var first = NewEmulator(rom, model);
        var second = NewEmulator(rom, model);
        second.WriteMemory(0xFF26, 0x80); // power on the APU

        Assert.Equal((byte)0x70, first.PeekMemory(0xFF26));
        Assert.Equal((byte)0xF0, second.PeekMemory(0xFF26));
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
    public void PpuIgnoresWritesToReadOnlyLyRegister()
    {
        var emulator = NewEmulator(MakeRom());
        emulator.WriteMemory(0xFF40, 0x80); // enable LCD
        emulator.RunCycles(456); // advance to line 1

        Assert.Equal((byte)1, emulator.PeekMemory(0xFF44));
        emulator.WriteMemory(0xFF44, 0x99);
        Assert.Equal((byte)1, emulator.PeekMemory(0xFF44));
    }

    [Fact]
    public void DmgOamReadDuringSearchCorruptsTheActiveOamRow()
    {
        var emulator = NewEmulator(MakeRom());
        byte[] firstRow = [0x34, 0x12, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0];
        byte[] activeRow = [0xAA, 0x55, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66];
        for (var i = 0; i < firstRow.Length; i++)
        {
            emulator.WriteMemory((ushort)(0xFE00 + i), firstRow[i]);
            emulator.WriteMemory((ushort)(0xFE08 + i), activeRow[i]);
        }

        emulator.WriteMemory(0xFF40, 0x80);
        emulator.RunCycles(4); // first two OAM-search pairs still use row 0x08
        Assert.Equal((byte)0xFF, emulator.ReadMemory(0xFE00));
        emulator.WriteMemory(0xFF40, 0x00);

        var current = (ushort)(activeRow[0] | (activeRow[1] << 8));
        var previous = (ushort)(firstRow[0] | (firstRow[1] << 8));
        var preceding = (ushort)(firstRow[4] | (firstRow[5] << 8));
        var expected = (ushort)(((current ^ preceding) & (previous ^ preceding)) ^ preceding);
        Assert.Equal((byte)expected, emulator.PeekMemory(0xFE08));
        Assert.Equal((byte)(expected >> 8), emulator.PeekMemory(0xFE09));
        for (var i = 2; i < 8; i++)
            Assert.Equal(firstRow[i], emulator.PeekMemory((ushort)(0xFE08 + i)));
    }

    [Fact]
    public void DmgOamReadAtRowEightZeroCopiesThatRowToTheStart()
    {
        var emulator = NewEmulator(MakeRom());
        for (var i = 0; i < 8; i++)
        {
            emulator.WriteMemory((ushort)(0xFE00 + i), (byte)(0x10 + i));
            emulator.WriteMemory((ushort)(0xFE80 + i), (byte)(0xA0 + i));
        }

        emulator.WriteMemory(0xFF40, 0x80);
        emulator.RunCycles(62); // OAM search reaches row 0x80
        Assert.Equal((byte)0xFF, emulator.ReadMemory(0xFE00));
        emulator.WriteMemory(0xFF40, 0x00);

        for (var i = 0; i < 8; i++)
            Assert.Equal((byte)(0xA0 + i), emulator.PeekMemory((ushort)(0xFE00 + i)));
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
        Assert.Equal(32 * 1024 + 48, battery.Length);
        Assert.Equal((byte)5, battery.GetBuffer()[32 * 1024]);
        Assert.Equal((byte)0, battery.GetBuffer()[32 * 1024 + 1]);
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

    [Fact]
    public void BessRoundTripsMbc3RtcState()
    {
        var clock = new TestTimeProvider();
        clock.Advance(TimeSpan.FromSeconds(1_700_000_000));
        var rom = MakeRom(type: 0x10, romSizeCode: 1, ramSizeCode: 3);
        var emulator = new Emulator(GameBoyModel.DmgB, new EmulatorOptions { TimeProvider = clock });
        emulator.LoadRom(rom);
        emulator.WriteMemory(0, 0x0A);
        emulator.WriteMemory(0x4000, 0x08);
        emulator.WriteMemory(0xA000, 37);
        emulator.WriteMemory(0x4000, 0x09);
        emulator.WriteMemory(0xA000, 12);
        emulator.WriteMemory(0x6000, 0);
        emulator.WriteMemory(0x6000, 1);

        using var state = new MemoryStream();
        emulator.SaveBess(state);
        state.Position = 0;
        Assert.Equal((byte)37, BessReader.ReadRtc(state)!.Value.Seconds);
        state.Position = 0;

        var restored = new Emulator(GameBoyModel.DmgB, new EmulatorOptions { TimeProvider = clock });
        restored.LoadRom(rom);
        restored.LoadBess(state);
        restored.WriteMemory(0, 0x0A);
        restored.WriteMemory(0x4000, 0x08);
        Assert.Equal((byte)37, restored.PeekMemory(0xA000));
        restored.WriteMemory(0x4000, 0x09);
        Assert.Equal((byte)12, restored.PeekMemory(0xA000));
    }

    private static void ConfigurePulseChannel(Emulator emulator)
    {
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF11, 0x80);
        emulator.WriteMemory(0xFF12, 0xF3);
        emulator.WriteMemory(0xFF13, 0x40);
        emulator.WriteMemory(0xFF14, 0x80);
    }

    private static void ConfigureNoise(Emulator emulator, byte nr43)
    {
        emulator.WriteMemory(0xFF26, 0x80);
        emulator.WriteMemory(0xFF21, 0xF0);
        emulator.WriteMemory(0xFF22, nr43);
        emulator.WriteMemory(0xFF23, 0x80);
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

    private sealed class NonSeekableReadStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class NonSeekableWriteStream : Stream
    {
        private readonly MemoryStream _inner = new();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public byte[] ToArray() => _inner.ToArray();
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
