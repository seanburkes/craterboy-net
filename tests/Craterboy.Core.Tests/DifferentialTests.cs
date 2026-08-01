using Craterboy;
using Xunit;

namespace Craterboy.Core.Tests;

public sealed class DifferentialTests
{
    [Fact]
    public void OracleIsThePinnedSameBoyRevision()
    {
        Assert.Equal(
            "SameBoy 1.0.3 213a12ce93d66b105a113debd9396306066a7cfc",
            SameBoyOracle.Baseline);
    }

    [Theory]
    [InlineData(GameBoyModel.DmgB, 0x002)]
    [InlineData(GameBoyModel.Mgb, 0x100)]
    [InlineData(GameBoyModel.CgbE, 0x205)]
    [InlineData(GameBoyModel.AgbA, 0x207)]
    [InlineData(GameBoyModel.GbpA, 0x227)]
    public void PublicModelsMapToPinnedSameBoyIdentifiers(GameBoyModel model, int nativeModel)
    {
        using var oracle = new SameBoyOracle(model, MakeRom());
        Assert.Equal(nativeModel, oracle.NativeModel);
    }

    [Fact]
    public void PostBootCpuRegistersMatchOracle()
    {
        var rom = MakeRom();
        var managed = CreateManaged(rom);
        using var oracle = new SameBoyOracle(GameBoyModel.DmgB, rom);

        AssertRegistersEqual(managed.Registers, oracle.Registers);
    }

    [Fact]
    public void WorkRamAndEchoRoutingMatchOracle()
    {
        var rom = MakeRom();
        var managed = CreateManaged(rom);
        using var oracle = new SameBoyOracle(GameBoyModel.DmgB, rom);

        managed.WriteMemory(0xC123, 0xA5);
        oracle.Write(0xC123, 0xA5);

        Assert.Equal(oracle.Read(0xC123), managed.PeekMemory(0xC123));
        Assert.Equal(oracle.Read(0xE123), managed.PeekMemory(0xE123));
        Assert.Equal(oracle.Read(0xFEA0), managed.PeekMemory(0xFEA0));
    }

    [Fact]
    public void JoypadSelectRegisterBusBehaviorMatchesOracle()
    {
        var rom = MakeRom();
        var managed = CreateManaged(rom);
        using var oracle = new SameBoyOracle(GameBoyModel.DmgB, rom);

        managed.WriteMemory(0xFF00, 0x20);
        oracle.Write(0xFF00, 0x20);
        Assert.Equal(oracle.Read(0xFF00), managed.PeekMemory(0xFF00));
        managed.WriteMemory(0xFF00, 0x10);
        oracle.Write(0xFF00, 0x10);
        Assert.Equal(oracle.Read(0xFF00), managed.PeekMemory(0xFF00));
    }

    [Fact]
    public void InitialCpuSliceMatchesRegistersAndCyclesAtEachInstruction()
    {
        var rom = MakeRom();
        new byte[] {
            0x21, 0x00, 0xC0, // LD HL,$C000
            0x3E, 0x42,       // LD A,$42
            0x77,             // LD (HL),A
            0x7E,             // LD A,(HL)
            0xAF,             // XOR A
            0x00,             // NOP
        }.CopyTo(rom, 0x100);
        var managed = CreateManaged(rom);
        using var oracle = new SameBoyOracle(GameBoyModel.DmgB, rom);

        for (var instruction = 0; instruction < 6; instruction++)
        {
            Assert.Equal(oracle.StepInstruction(), (uint)managed.StepInstruction());
            AssertRegistersEqual(managed.Registers, oracle.Registers);
        }
        Assert.Equal(oracle.Read(0xC000), managed.PeekMemory(0xC000));
    }

    [Fact]
    public void AllCbPrefixedInstructionsMatchOracle()
    {
        for (var cbOpcode = 0; cbOpcode <= byte.MaxValue; cbOpcode++)
        {
            var rom = MakeRom();
            new byte[] { 0x21, 0x00, 0xC0, 0xCB, (byte)cbOpcode }.CopyTo(rom, 0x100);
            var managed = CreateManaged(rom);
            using var oracle = new SameBoyOracle(GameBoyModel.DmgB, rom);

            managed.WriteMemory(0xC000, 0x81);
            oracle.Write(0xC000, 0x81);
            Assert.Equal(oracle.StepInstruction(), (uint)managed.StepInstruction());
            Assert.Equal(oracle.StepInstruction(), (uint)managed.StepInstruction());
            AssertRegistersEqual(managed.Registers, oracle.Registers);
            Assert.Equal(oracle.Read(0xC000), managed.PeekMemory(0xC000));
        }
    }

    [Fact]
    public void RegisterTransfersMatchOracle()
    {
        for (var opcode = 0x40; opcode <= 0x7F; opcode++)
        {
            if (opcode == 0x76) continue;
            var rom = MakeRom();
            new byte[] { 0x21, 0x00, 0xC0, (byte)opcode }.CopyTo(rom, 0x100);
            var managed = CreateManaged(rom);
            using var oracle = new SameBoyOracle(GameBoyModel.DmgB, rom);

            managed.WriteMemory(0xC000, 0x81);
            oracle.Write(0xC000, 0x81);
            Assert.Equal(oracle.StepInstruction(), (uint)managed.StepInstruction());
            Assert.Equal(oracle.StepInstruction(), (uint)managed.StepInstruction());
            AssertRegistersEqual(managed.Registers, oracle.Registers);
            Assert.Equal(oracle.Read(0xC000), managed.PeekMemory(0xC000));
        }
    }

    [Fact]
    public void ImmediateAccumulatorOperationsMatchOracle()
    {
        foreach (var opcode in new byte[] { 0xC6, 0xCE, 0xD6, 0xDE, 0xE6, 0xEE, 0xF6, 0xFE })
        {
            var rom = MakeRom();
            rom[0x100] = opcode;
            rom[0x101] = 0x01;
            var managed = CreateManaged(rom);
            using var oracle = new SameBoyOracle(GameBoyModel.DmgB, rom);

            Assert.Equal(oracle.StepInstruction(), (uint)managed.StepInstruction());
            AssertRegistersEqual(managed.Registers, oracle.Registers);
        }
    }

    [Theory]
    [InlineData(0x34, 0x0F, 0x10)]
    [InlineData(0x35, 0x10, 0x0F)]
    public void MemoryIncrementAndDecrementMatchOracle(byte opcode, byte initial, byte expected)
    {
        var rom = MakeRom();
        new byte[] { 0x21, 0x00, 0xC0, opcode }.CopyTo(rom, 0x100);
        var managed = CreateManaged(rom);
        using var oracle = new SameBoyOracle(GameBoyModel.DmgB, rom);
        managed.WriteMemory(0xC000, initial);
        oracle.Write(0xC000, initial);

        Assert.Equal(oracle.StepInstruction(), (uint)managed.StepInstruction());
        Assert.Equal(oracle.StepInstruction(), (uint)managed.StepInstruction());
        Assert.Equal(expected, managed.PeekMemory(0xC000));
        Assert.Equal(expected, oracle.Read(0xC000));
        AssertRegistersEqual(managed.Registers, oracle.Registers);
    }

    [Theory]
    [InlineData(0x03)] [InlineData(0x13)] [InlineData(0x23)] [InlineData(0x33)]
    [InlineData(0x0B)] [InlineData(0x1B)] [InlineData(0x2B)] [InlineData(0x3B)]
    [InlineData(0x09)] [InlineData(0x19)] [InlineData(0x29)] [InlineData(0x39)]
    public void SixteenBitArithmeticMatchesOracle(byte opcode)
    {
        var rom = MakeRom();
        new byte[] { 0x21, 0x00, 0xC0, opcode }.CopyTo(rom, 0x100);
        var managed = CreateManaged(rom);
        using var oracle = new SameBoyOracle(GameBoyModel.DmgB, rom);

        Assert.Equal(oracle.StepInstruction(), (uint)managed.StepInstruction());
        Assert.Equal(oracle.StepInstruction(), (uint)managed.StepInstruction());
        AssertRegistersEqual(managed.Registers, oracle.Registers);
    }

    [Theory]
    [InlineData(0x07)] [InlineData(0x0F)] [InlineData(0x17)] [InlineData(0x1F)]
    public void AccumulatorRotatesMatchOracle(byte opcode)
    {
        var rom = MakeRom();
        new byte[] { 0x3E, 0x81, opcode }.CopyTo(rom, 0x100);
        var managed = CreateManaged(rom);
        using var oracle = new SameBoyOracle(GameBoyModel.DmgB, rom);

        Assert.Equal(oracle.StepInstruction(), (uint)managed.StepInstruction());
        Assert.Equal(oracle.StepInstruction(), (uint)managed.StepInstruction());
        AssertRegistersEqual(managed.Registers, oracle.Registers);
    }

    [Theory]
    [InlineData(0x2F)] [InlineData(0x37)] [InlineData(0x3F)]
    public void AccumulatorStatusOperationsMatchOracle(byte opcode)
    {
        var rom = MakeRom();
        new byte[] { 0x3E, 0x3C, opcode }.CopyTo(rom, 0x100);
        var managed = CreateManaged(rom);
        using var oracle = new SameBoyOracle(GameBoyModel.DmgB, rom);

        Assert.Equal(oracle.StepInstruction(), (uint)managed.StepInstruction());
        Assert.Equal(oracle.StepInstruction(), (uint)managed.StepInstruction());
        AssertRegistersEqual(managed.Registers, oracle.Registers);
    }

    [Fact]
    public void DecimalAdjustAfterAdditionMatchesOracle()
    {
        var rom = MakeRom();
        new byte[] { 0x3E, 0x15, 0xC6, 0x27, 0x27 }.CopyTo(rom, 0x100);
        var managed = CreateManaged(rom);
        using var oracle = new SameBoyOracle(GameBoyModel.DmgB, rom);

        for (var index = 0; index < 3; index++)
        {
            Assert.Equal(oracle.StepInstruction(), (uint)managed.StepInstruction());
            AssertRegistersEqual(managed.Registers, oracle.Registers);
        }
    }

    [Fact]
    public void DecimalAdjustAfterSubtractionMatchesOracle()
    {
        var rom = MakeRom();
        new byte[] { 0x3E, 0x42, 0xD6, 0x27, 0x27 }.CopyTo(rom, 0x100);
        var managed = CreateManaged(rom);
        using var oracle = new SameBoyOracle(GameBoyModel.DmgB, rom);

        for (var index = 0; index < 3; index++)
        {
            Assert.Equal(oracle.StepInstruction(), (uint)managed.StepInstruction());
            AssertRegistersEqual(managed.Registers, oracle.Registers);
        }
    }

    [Theory]
    [InlineData(0xC4)] [InlineData(0xCC)] [InlineData(0xD4)] [InlineData(0xDC)]
    public void ConditionalCallsMatchOracle(byte opcode)
    {
        var rom = MakeRom();
        new byte[] { opcode, 0x06, 0x01 }.CopyTo(rom, 0x100);
        var managed = CreateManaged(rom);
        using var oracle = new SameBoyOracle(GameBoyModel.DmgB, rom);

        Assert.Equal(oracle.StepInstruction(), (uint)managed.StepInstruction());
        AssertRegistersEqual(managed.Registers, oracle.Registers);
    }

    [Theory]
    [InlineData(0xC0)] [InlineData(0xC8)] [InlineData(0xD0)] [InlineData(0xD8)]
    public void ConditionalReturnsMatchOracle(byte opcode)
    {
        var rom = MakeRom();
        rom[0x100] = opcode;
        var managed = CreateManaged(rom);
        using var oracle = new SameBoyOracle(GameBoyModel.DmgB, rom);
        managed.WriteMemory(0xFFFC, 0x34); oracle.Write(0xFFFC, 0x34);
        managed.WriteMemory(0xFFFD, 0x01); oracle.Write(0xFFFD, 0x01);

        Assert.Equal(oracle.StepInstruction(), (uint)managed.StepInstruction());
        AssertRegistersEqual(managed.Registers, oracle.Registers);
    }

    [Fact]
    public void ReturnAndRestartInstructionsMatchOracle()
    {
        foreach (var opcode in new byte[] { 0xD9, 0xC7, 0xCF, 0xD7, 0xDF, 0xE7, 0xEF, 0xF7, 0xFF })
        {
            var rom = MakeRom();
            rom[0x100] = opcode;
            var managed = CreateManaged(rom);
            using var oracle = new SameBoyOracle(GameBoyModel.DmgB, rom);
            managed.WriteMemory(0xFFFC, 0x34); oracle.Write(0xFFFC, 0x34);
            managed.WriteMemory(0xFFFD, 0x01); oracle.Write(0xFFFD, 0x01);

            Assert.Equal(oracle.StepInstruction(), (uint)managed.StepInstruction());
            AssertRegistersEqual(managed.Registers, oracle.Registers);
        }
    }

    [Theory]
    [InlineData(0xE0)] [InlineData(0xE2)] [InlineData(0xEA)]
    public void AbsoluteAndHighPageWritesMatchOracle(byte opcode)
    {
        var rom = MakeRom();
        var prefix = opcode == 0xE2 ? new byte[] { 0x0E, 0x80 } : Array.Empty<byte>();
        var program = new byte[prefix.Length + (opcode == 0xEA ? 3 : 2)];
        prefix.CopyTo(program, 0);
        program[prefix.Length] = opcode;
        if (opcode == 0xEA) { program[^2] = 0x00; program[^1] = 0xC0; }
        else program[^1] = 0x80;
        program.CopyTo(rom, 0x100);
        var managed = CreateManaged(rom);
        using var oracle = new SameBoyOracle(GameBoyModel.DmgB, rom);

        if (prefix.Length != 0)
        {
            Assert.Equal(oracle.StepInstruction(), (uint)managed.StepInstruction());
        }
        Assert.Equal(oracle.StepInstruction(), (uint)managed.StepInstruction());
        AssertRegistersEqual(managed.Registers, oracle.Registers);
        var address = opcode == 0xEA ? (ushort)0xC000 : (ushort)0xFF80;
        Assert.Equal(oracle.Read(address), managed.PeekMemory(address));
    }

    [Theory]
    [InlineData(0xF0)] [InlineData(0xF2)] [InlineData(0xFA)]
    public void AbsoluteAndHighPageReadsMatchOracle(byte opcode)
    {
        var rom = MakeRom();
        var prefix = opcode == 0xF2 ? new byte[] { 0x0E, 0x80 } : Array.Empty<byte>();
        var program = new byte[prefix.Length + (opcode == 0xFA ? 3 : 2)];
        prefix.CopyTo(program, 0);
        program[prefix.Length] = opcode;
        if (opcode == 0xFA) { program[^2] = 0x00; program[^1] = 0xC0; }
        else program[^1] = 0x80;
        program.CopyTo(rom, 0x100);
        var managed = CreateManaged(rom);
        using var oracle = new SameBoyOracle(GameBoyModel.DmgB, rom);
        var address = opcode == 0xFA ? (ushort)0xC000 : (ushort)0xFF80;
        managed.WriteMemory(address, 0xA5);
        oracle.Write(address, 0xA5);

        if (prefix.Length != 0)
        {
            Assert.Equal(oracle.StepInstruction(), (uint)managed.StepInstruction());
        }
        Assert.Equal(oracle.StepInstruction(), (uint)managed.StepInstruction());
        AssertRegistersEqual(managed.Registers, oracle.Registers);
    }

    [Fact]
    public void StoreStackPointerAbsoluteMatchesOracle()
    {
        var rom = MakeRom();
        new byte[] { 0x08, 0x00, 0xC0 }.CopyTo(rom, 0x100);
        var managed = CreateManaged(rom);
        using var oracle = new SameBoyOracle(GameBoyModel.DmgB, rom);

        Assert.Equal(oracle.StepInstruction(), (uint)managed.StepInstruction());
        Assert.Equal(oracle.Read(0xC000), managed.PeekMemory(0xC000));
        Assert.Equal(oracle.Read(0xC001), managed.PeekMemory(0xC001));
        AssertRegistersEqual(managed.Registers, oracle.Registers);
    }

    [Fact]
    public void ExpandedAluAndIncrementInstructionsMatchOracle()
    {
        var rom = MakeRom();
        new byte[] {
            0x3E, 0x12, // LD A,$12
            0x06, 0x03, // LD B,$03
            0x80,       // ADD A,B
            0x04,       // INC B
            0x05,       // DEC B
            0x90,       // SUB B
            0xA0,       // AND B
            0xB0,       // OR B
            0xB8,       // CP B
            0x00,       // NOP
        }.CopyTo(rom, 0x100);
        var managed = CreateManaged(rom);
        using var oracle = new SameBoyOracle(GameBoyModel.DmgB, rom);

        for (var instruction = 0; instruction < 10; instruction++)
        {
            Assert.Equal(oracle.StepInstruction(), (uint)managed.StepInstruction());
            AssertRegistersEqual(managed.Registers, oracle.Registers);
        }
    }

    [Fact]
    public void Mbc3RomBankAndRamEnableMatchOracle()
    {
        var rom = MakeRom(type: 0x10, romSizeCode: 1, ramSizeCode: 3);
        rom[0x4000] = 0x11;
        rom[0x8000] = 0x22;
        var managed = CreateManaged(rom);
        using var oracle = new SameBoyOracle(GameBoyModel.DmgB, rom);

        managed.WriteMemory(0x2000, 2); oracle.Write(0x2000, 2);
        Assert.Equal(oracle.Read(0x4000), managed.PeekMemory(0x4000));
        managed.WriteMemory(0, 0x0A); oracle.Write(0, 0x0A);
        managed.WriteMemory(0xA000, 0x5A); oracle.Write(0xA000, 0x5A);
        Assert.Equal(oracle.Read(0xA000), managed.PeekMemory(0xA000));
    }

    private static Emulator CreateManaged(byte[] rom)
    {
        var emulator = new Emulator(GameBoyModel.DmgB);
        emulator.LoadRom(rom);
        return emulator;
    }

    private static void AssertRegistersEqual(
        CpuRegisterSnapshot managed,
        SameBoyOracle.OracleRegisters native)
    {
        Assert.Equal(native.A, managed.A); Assert.Equal(native.F, managed.F);
        Assert.Equal(native.B, managed.B); Assert.Equal(native.C, managed.C);
        Assert.Equal(native.D, managed.D); Assert.Equal(native.E, managed.E);
        Assert.Equal(native.H, managed.H); Assert.Equal(native.L, managed.L);
        Assert.Equal(native.StackPointer, managed.StackPointer);
        Assert.Equal(native.ProgramCounter, managed.ProgramCounter);
    }

    private static byte[] MakeRom(byte type = 0, byte romSizeCode = 0, byte ramSizeCode = 0)
    {
        var rom = new byte[32 * 1024 << romSizeCode];
        rom[0x147] = type;
        rom[0x148] = romSizeCode;
        rom[0x149] = ramSizeCode;
        byte checksum = 0;
        for (var i = 0x134; i <= 0x14C; i++)
            checksum = unchecked((byte)(checksum - rom[i] - 1));
        rom[0x14D] = checksum;
        return rom;
    }
}
