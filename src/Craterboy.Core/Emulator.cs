namespace Craterboy;

public sealed class Emulator
{
    private const int CyclesPerFrame = 70_224;
    private const byte InterruptMask = 0x1F;
    private readonly GameBoyModel _model;
    private readonly EmulatorOptions _options;
    private readonly EmulatorState _state = new();
    private readonly byte[] _vram = new byte[0x4000];
    private readonly byte[] _wram = new byte[0x8000];
    private readonly byte[] _oam = new byte[0xA0];
    private readonly byte[] _io = new byte[0x80];
    private readonly byte[] _hram = new byte[0x7F];
    private byte _vramBank;
    private byte _wramBank;
    private bool _doubleSpeed;
    private bool _speedSwitchPrepared;
    private ushort _cgbDmaSource;
    private ushort _cgbDmaDestination;
    private byte _cgbDmaStatus = 0xFF;
    private bool _cgbDmaHblankActive;
    private TimerDevice _timer = null!;
    private OamDmaDevice _dma = null!;
    private SerialDevice _serial = null!;
    private JoypadDevice _joypad = null!;
    private PpuDevice _ppu = null!;
    private ApuDevice _apu = null!;
    private Cartridge? _cartridge;
    private byte[]? _bootRom;
    private bool _bootMapped;

    public Emulator(GameBoyModel model, EmulatorOptions? options = null)
    {
        _model = model;
        _options = options ?? new EmulatorOptions();
        _timer = new TimerDevice(_io);
        _dma = new OamDmaDevice(address => Read(address, true), (index, value) => _oam[index] = value);
        _serial = new SerialDevice(_io, _options.SerialEndpoint);
        _joypad = new JoypadDevice(_model, _io);
        _ppu = new PpuDevice(_model, _io, _vram, _oam, TransferCgbHblankBlock, CancelCgbHblankDma);
        _apu = new ApuDevice(_model, _io);
        _state.Scheduler.Register(_timer);
        _state.Scheduler.Register(_dma);
        _state.Scheduler.Register(_serial);
        _state.Scheduler.Register(_joypad);
        _state.Scheduler.Register(_ppu);
        _state.Scheduler.Register(_apu);
        Reset();
    }

    public GameBoyModel Model => _model;
    public long CycleCount => _state.Scheduler.CycleCount;
    public RomHeader? RomHeader { get; private set; }
    public CpuRegisterSnapshot Registers => _state.Cpu.Snapshot;
    public bool BatteryDirty => _cartridge?.BatteryDirty ?? false;

    public void LoadRom(ReadOnlyMemory<byte> rom)
    {
        var header = RomHeader.Parse(rom.Span);
        if (header.RomSize != 0 && rom.Length < header.RomSize)
            throw new ArgumentException($"ROM header declares {header.RomSize} bytes but only {rom.Length} were supplied.", nameof(rom));
        var owned = rom.ToArray();
        _cartridge = Cartridge.Create(owned, header, _options.TimeProvider);
        RomHeader = header;
        Reset();
    }

    public void LoadRom(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        LoadRom(memory.ToArray());
    }

    public void LoadBootRom(ReadOnlyMemory<byte> bootRom)
    {
        if (bootRom.IsEmpty) throw new ArgumentException("Boot ROM cannot be empty.", nameof(bootRom));
        _bootRom = bootRom.ToArray();
        Reset();
    }

    public void Reset()
    {
        _state.Scheduler.Reset();
        Array.Clear(_vram); Array.Clear(_wram); Array.Clear(_oam); Array.Clear(_io); Array.Clear(_hram);
        _vramBank = 0;
        _wramBank = 1;
        _doubleSpeed = false;
        _speedSwitchPrepared = false;
        _cgbDmaSource = 0;
        _cgbDmaDestination = 0x8000;
        _cgbDmaStatus = 0xFF;
        _cgbDmaHblankActive = false;
        _timer.Reset();
        _dma.Reset();
        _serial.Reset();
        _joypad.Reset();
        _ppu.Reset();
        _apu.Reset();
        _bootMapped = _bootRom is not null && !_options.SkipBootRom;
        var cpu = _state.Cpu;
        cpu.A = _model.IsColor() ? (byte)0x11 : (byte)0x01;
        cpu.F = 0xB0; cpu.B = 0; cpu.C = 0x13; cpu.D = 0; cpu.E = 0xD8;
        cpu.H = 0x01; cpu.L = 0x4D; cpu.SP = 0xFFFE;
        cpu.PC = _bootMapped ? (ushort)0 : (ushort)0x100;
        cpu.Ime = false; cpu.ImeEnablePending = false; cpu.Halted = false;
        _io[0x50] = _bootMapped ? (byte)0 : (byte)1;
    }

    public byte PeekMemory(ushort address) => Read(address);
    public byte ReadMemory(ushort address) => Read(address);
    public void WriteMemory(ushort address, byte value) => Write(address, value);

    public byte[] ComputeStateHash()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)_model);
            writer.Write(CycleCount);
            writer.Write(_bootMapped);
            writer.Write(_state.Cpu.A); writer.Write(_state.Cpu.F);
            writer.Write(_state.Cpu.B); writer.Write(_state.Cpu.C);
            writer.Write(_state.Cpu.D); writer.Write(_state.Cpu.E);
            writer.Write(_state.Cpu.H); writer.Write(_state.Cpu.L);
            writer.Write(_state.Cpu.SP); writer.Write(_state.Cpu.PC);
            writer.Write(_state.Cpu.Ime); writer.Write(_state.Cpu.ImeEnablePending); writer.Write(_state.Cpu.Halted);
            writer.Write(_vram); writer.Write(_wram); writer.Write(_oam);
            writer.Write(_vramBank); writer.Write(_wramBank);
            writer.Write(_doubleSpeed); writer.Write(_speedSwitchPrepared);
            writer.Write(_cgbDmaSource); writer.Write(_cgbDmaDestination);
            writer.Write(_cgbDmaStatus); writer.Write(_cgbDmaHblankActive);
            writer.Write(_io); writer.Write(_hram);
            _ppu.WriteStateHash(writer);
            _timer.WriteStateHash(writer);
            _apu.WriteStateHash(writer);
            _serial.WriteStateHash(writer);
            writer.Write(_cartridge?.SaveBattery() ?? Array.Empty<byte>());
        }
        return System.Security.Cryptography.SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }

    public void SetButtonState(GameBoyButton button, bool pressed, int player = 0) =>
        _joypad.SetButtonState(button, pressed, player);

    public void ClockSerialBit() => _serial.ClockExternalBit();

    public void CopyFrame(Span<byte> destination) => _ppu.CopyFrame(destination);
    public void CopyColorFrame(Span<ushort> destination) => _ppu.CopyColorFrame(destination);
    public int CopyAudioSamples(Span<short> destination) => _apu.CopySamples(destination);

    public int StepInstruction()
    {
        EnsureRom();
        var cpu = _state.Cpu;
        if (TryServiceInterrupt()) return ScaledCycles(20, _doubleSpeed);
        if (cpu.Halted && PendingInterrupts() != 0) cpu.Halted = false;
        if (cpu.Halted)
        {
            var haltedCycles = ScaledCycles(4, _doubleSpeed);
            Advance(haltedCycles);
            return haltedCycles;
        }
        var wasDoubleSpeed = _doubleSpeed;
        var opcode = Read(cpu.PC++);
        var enableImeAfterInstruction = cpu.ImeEnablePending;
        cpu.ImeEnablePending = false;
        var cycles = Execute(opcode);
        var elapsedCycles = ScaledCycles(cycles, wasDoubleSpeed);
        Advance(elapsedCycles);
        if (enableImeAfterInstruction && opcode != 0xF3) cpu.Ime = true;
        return elapsedCycles;
    }

    public void RunCycles(int cycles)
    {
        if (cycles < 0) throw new ArgumentOutOfRangeException(nameof(cycles));
        var target = checked(CycleCount + cycles);
        while (CycleCount < target)
        {
            var remaining = target - CycleCount;
            if (remaining < PredictNextInstructionCycles()) { Advance((int)remaining); break; }
            StepInstruction();
        }
    }

    public void RunFrame() => RunCycles(CyclesPerFrame);

    public void ReplayInputRecording(InputRecording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        foreach (var inputEvent in recording.Events)
        {
            if (inputEvent.Cycle < CycleCount)
                throw new InvalidOperationException("Input recording event precedes the current emulated cycle.");
            var remaining = inputEvent.Cycle - CycleCount;
            while (remaining > 0)
            {
                var step = (int)Math.Min(remaining, int.MaxValue);
                RunCycles(step);
                remaining -= step;
            }
            SetButtonState(inputEvent.Button, inputEvent.Pressed, inputEvent.Player);
        }
    }

    public byte[] SaveBattery() => (_cartridge ?? throw new InvalidOperationException("No ROM is loaded.")).SaveBattery();
    public void LoadBattery(ReadOnlySpan<byte> data) =>
        (_cartridge ?? throw new InvalidOperationException("No ROM is loaded.")).LoadBattery(data);

    public void SaveBattery(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(SaveBattery());
    }

    public void LoadBattery(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var memory = new MemoryStream();
        source.CopyTo(memory);
        LoadBattery(memory.GetBuffer().AsSpan(0, checked((int)memory.Length)));
    }

    private int Execute(byte opcode) => opcode switch
    {
        0x00 => 4,
        0x10 => Stop(),
        0xCB => ExecuteCb(Read(_state.Cpu.PC++)),
        >= 0x40 and <= 0x7F when opcode != 0x76 => LoadRegister(opcode),
        0xC6 => AddImmediate(),
        0xCE => AddCarryImmediate(),
        0xD6 => SubImmediate(),
        0xDE => SubCarryImmediate(),
        0xE6 => AndImmediate(),
        0xEE => XorImmediate(),
        0xF6 => OrImmediate(),
        0xFE => CompareImmediate(),
        0x34 => IncrementHl(),
        0x35 => DecrementHl(),
        0x03 => IncrementPair(() => _state.Cpu.BC, value => _state.Cpu.BC = value),
        0x13 => IncrementPair(() => _state.Cpu.DE, value => _state.Cpu.DE = value),
        0x23 => IncrementPair(() => _state.Cpu.HL, value => _state.Cpu.HL = value),
        0x33 => IncrementPair(() => _state.Cpu.SP, value => _state.Cpu.SP = value),
        0x0B => DecrementPair(() => _state.Cpu.BC, value => _state.Cpu.BC = value),
        0x1B => DecrementPair(() => _state.Cpu.DE, value => _state.Cpu.DE = value),
        0x2B => DecrementPair(() => _state.Cpu.HL, value => _state.Cpu.HL = value),
        0x3B => DecrementPair(() => _state.Cpu.SP, value => _state.Cpu.SP = value),
        0x09 => AddHl(_state.Cpu.BC),
        0x19 => AddHl(_state.Cpu.DE),
        0x29 => AddHl(_state.Cpu.HL),
        0x39 => AddHl(_state.Cpu.SP),
        0x08 => WriteSpAbsolute(),
        0xE0 => WriteHighPage(),
        0xE2 => WriteCPage(),
        0xEA => WriteAbsoluteA(),
        0xF0 => ReadHighPage(),
        0xF2 => ReadCPage(),
        0xFA => ReadAbsoluteA(),
        0xE8 => AddSignedToSp(),
        0xF8 => LoadHlFromSignedSp(),
        0xF9 => LoadSpFromHl(),
        0xE9 => JumpHl(),
        0x22 => StoreHlAndIncrement(),
        0x32 => StoreHlAndDecrement(),
        0x2A => LoadAAndIncrementHl(),
        0x3A => LoadAAndDecrementHl(),
        0x36 => StoreImmediateHl(),
        0x07 => RotateLeftCircularA(),
        0x0F => RotateRightCircularA(),
        0x17 => RotateLeftA(),
        0x1F => RotateRightA(),
        0x27 => DecimalAdjustA(),
        0x2F => ComplementA(),
        0x37 => SetCarryFlag(),
        0x3F => ComplementCarryFlag(),
        0xF3 => DisableInterrupts(),
        0xFB => EnableInterrupts(),
        0xC4 => ConditionalCall(!Flag(CpuFlags.Zero)),
        0xCC => ConditionalCall(Flag(CpuFlags.Zero)),
        0xD4 => ConditionalCall(!Flag(CpuFlags.Carry)),
        0xDC => ConditionalCall(Flag(CpuFlags.Carry)),
        0xC0 => ConditionalReturn(!Flag(CpuFlags.Zero)),
        0xC8 => ConditionalReturn(Flag(CpuFlags.Zero)),
        0xD0 => ConditionalReturn(!Flag(CpuFlags.Carry)),
        0xD8 => ConditionalReturn(Flag(CpuFlags.Carry)),
        0xD9 => ReturnAndEnableInterrupts(),
        0xC7 => Restart(0x00),
        0xCF => Restart(0x08),
        0xD7 => Restart(0x10),
        0xDF => Restart(0x18),
        0xE7 => Restart(0x20),
        0xEF => Restart(0x28),
        0xF7 => Restart(0x30),
        0xFF => Restart(0x38),
        0x01 => Load16(v => _state.Cpu.BC = v),
        0x11 => Load16(v => _state.Cpu.DE = v),
        0x21 => Load16(v => _state.Cpu.HL = v),
        0x31 => Load16(v => _state.Cpu.SP = v),
        0x3E => Load8(v => _state.Cpu.A = v),
        0x06 => Load8(v => _state.Cpu.B = v),
        0x0E => Load8(v => _state.Cpu.C = v),
        0x16 => Load8(v => _state.Cpu.D = v),
        0x1E => Load8(v => _state.Cpu.E = v),
        0x26 => Load8(v => _state.Cpu.H = v),
        0x2E => Load8(v => _state.Cpu.L = v),
        0x77 => WriteHl(),
        0x7E => ReadHl(),
        0x02 => WritePair(_state.Cpu.BC),
        0x0A => ReadPair(_state.Cpu.BC),
        0x12 => WritePair(_state.Cpu.DE),
        0x1A => ReadPair(_state.Cpu.DE),
        0x04 => Increment(() => _state.Cpu.B, v => _state.Cpu.B = v),
        0x05 => Decrement(() => _state.Cpu.B, v => _state.Cpu.B = v),
        0x0C => Increment(() => _state.Cpu.C, v => _state.Cpu.C = v),
        0x0D => Decrement(() => _state.Cpu.C, v => _state.Cpu.C = v),
        0x14 => Increment(() => _state.Cpu.D, v => _state.Cpu.D = v),
        0x15 => Decrement(() => _state.Cpu.D, v => _state.Cpu.D = v),
        0x1C => Increment(() => _state.Cpu.E, v => _state.Cpu.E = v),
        0x1D => Decrement(() => _state.Cpu.E, v => _state.Cpu.E = v),
        0x24 => Increment(() => _state.Cpu.H, v => _state.Cpu.H = v),
        0x25 => Decrement(() => _state.Cpu.H, v => _state.Cpu.H = v),
        0x2C => Increment(() => _state.Cpu.L, v => _state.Cpu.L = v),
        0x2D => Decrement(() => _state.Cpu.L, v => _state.Cpu.L = v),
        0x3C => Increment(() => _state.Cpu.A, v => _state.Cpu.A = v),
        0x3D => Decrement(() => _state.Cpu.A, v => _state.Cpu.A = v),
        >= 0x80 and <= 0x87 => AddA(ReadRegister(opcode & 7), (opcode & 7) == 6),
        >= 0x88 and <= 0x8F => AddCarryA(ReadRegister(opcode & 7), (opcode & 7) == 6),
        >= 0x90 and <= 0x97 => SubA(ReadRegister(opcode & 7), (opcode & 7) == 6),
        >= 0x98 and <= 0x9F => SubCarryA(ReadRegister(opcode & 7), (opcode & 7) == 6),
        >= 0xA0 and <= 0xA7 => AndA(ReadRegister(opcode & 7), (opcode & 7) == 6),
        >= 0xA8 and <= 0xAF => XorRegisterA(ReadRegister(opcode & 7), (opcode & 7) == 6),
        >= 0xB0 and <= 0xB7 => OrA(ReadRegister(opcode & 7), (opcode & 7) == 6),
        >= 0xB8 and <= 0xBF => CompareA(ReadRegister(opcode & 7), (opcode & 7) == 6),
        0x18 => RelativeJump(true),
        0x20 => RelativeJump(!Flag(CpuFlags.Zero)),
        0x28 => RelativeJump(Flag(CpuFlags.Zero)),
        0x30 => RelativeJump(!Flag(CpuFlags.Carry)),
        0x38 => RelativeJump(Flag(CpuFlags.Carry)),
        0xC3 => Jump(),
        0xC2 => AbsoluteJump(!Flag(CpuFlags.Zero)),
        0xCA => AbsoluteJump(Flag(CpuFlags.Zero)),
        0xD2 => AbsoluteJump(!Flag(CpuFlags.Carry)),
        0xDA => AbsoluteJump(Flag(CpuFlags.Carry)),
        0xC5 => Push(_state.Cpu.BC),
        0xD5 => Push(_state.Cpu.DE),
        0xE5 => Push(_state.Cpu.HL),
        0xF5 => Push((ushort)((_state.Cpu.A << 8) | (_state.Cpu.F & 0xF0))),
        0xC1 => Pop(v => _state.Cpu.BC = v),
        0xD1 => Pop(v => _state.Cpu.DE = v),
        0xE1 => Pop(v => _state.Cpu.HL = v),
        0xF1 => Pop(v => { _state.Cpu.A = (byte)(v >> 8); _state.Cpu.F = (byte)(v & 0xF0); }),
        0xC9 => Return(),
        0xCD => Call(),
        0x76 => Halt(),
        _ => throw new NotSupportedException($"SM83 opcode 0x{opcode:X2} at 0x{_state.Cpu.PC - 1:X4} is not ported yet."),
    };

    private int PredictNextInstructionCycles()
    {
        if (_state.Cpu.Halted) return ScaledCycles(4, _doubleSpeed);
        var opcode = Read(_state.Cpu.PC);
        var cycles = opcode switch
        {
            0x00 or 0x10 or 0x76 => 4,
            0xCB => PredictCbCycles(Read((ushort)(_state.Cpu.PC + 1))),
            >= 0x40 and <= 0x7F when opcode != 0x76 =>
                (opcode & 7) == 6 || ((opcode >> 3) & 7) == 6 ? 8 : 4,
            0xC6 or 0xCE or 0xD6 or 0xDE or 0xE6 or 0xEE or 0xF6 or 0xFE => 8,
            0x34 or 0x35 => 12,
            0x03 or 0x13 or 0x23 or 0x33 or 0x0B or 0x1B or 0x2B or 0x3B or
            0x09 or 0x19 or 0x29 or 0x39 => 8,
            0xE2 or 0xF2 => 8,
            0xE0 or 0xF0 => 12,
            0xEA or 0xFA => 16,
            0x08 => 20,
            0xE8 => 16,
            0xF8 => 12,
            0xF9 => 8,
            0xE9 => 4,
            0x22 or 0x32 or 0x2A or 0x3A => 8,
            0x36 => 12,
            0x07 or 0x0F or 0x17 or 0x1F => 4,
            0x27 or 0x2F or 0x37 or 0x3F => 4,
            0xF3 or 0xFB => 4,
            0xC4 => Flag(CpuFlags.Zero) ? 12 : 24,
            0xCC => Flag(CpuFlags.Zero) ? 24 : 12,
            0xD4 => Flag(CpuFlags.Carry) ? 12 : 24,
            0xDC => Flag(CpuFlags.Carry) ? 24 : 12,
            0xC0 => Flag(CpuFlags.Zero) ? 8 : 20,
            0xC8 => Flag(CpuFlags.Zero) ? 20 : 8,
            0xD0 => Flag(CpuFlags.Carry) ? 8 : 20,
            0xD8 => Flag(CpuFlags.Carry) ? 20 : 8,
            0xD9 => 16,
            0xC7 or 0xCF or 0xD7 or 0xDF or 0xE7 or 0xEF or 0xF7 or 0xFF => 16,
            0x01 or 0x11 or 0x21 or 0x31 => 12,
            0xCD => 24,
            0xC3 or 0xC2 or 0xCA or 0xD2 or 0xDA => 16,
            0xC5 or 0xD5 or 0xE5 or 0xF5 => 16,
            0xC1 or 0xD1 or 0xE1 or 0xF1 or 0xC9 => 12,
            0x77 or 0x7E or 0x02 or 0x0A or 0x12 or 0x1A => 8,
            >= 0x80 and <= 0x87 or >= 0x90 and <= 0x97 or >= 0xA0 and <= 0xA7 or >= 0xB0 and <= 0xBF
                => (opcode & 7) == 6 ? 16 : 4,
            >= 0x88 and <= 0x8F or >= 0x98 and <= 0x9F or >= 0xA8 and <= 0xAF
                => (opcode & 7) == 6 ? 16 : 4,
            0x18 or 0x20 or 0x28 or 0x30 or 0x38 => 12,
            _ => 8,
        };
        return ScaledCycles(cycles, _doubleSpeed);
    }

    private int ExecuteCb(byte opcode)
    {
        var register = opcode & 7;
        var memory = register == 6;
        var value = ReadRegister(register);
        var group = opcode >> 6;

        if (group == 1)
        {
            var bit = (opcode >> 3) & 7;
            _state.Cpu.F = (byte)((_state.Cpu.F & (byte)CpuFlags.Carry) | (byte)CpuFlags.HalfCarry |
                ((value & (1 << bit)) == 0 ? (byte)CpuFlags.Zero : (byte)0));
            return memory ? 12 : 8;
        }

        if (group == 2)
            value = (byte)(value & ~(1 << ((opcode >> 3) & 7)));
        else if (group == 3)
            value = (byte)(value | (1 << ((opcode >> 3) & 7)));
        else
        {
            var operation = (opcode >> 3) & 7;
            var carry = (byte)((value >> 7) & 1);
            switch (operation)
            {
                case 0: // RLC
                    value = (byte)((value << 1) | carry);
                    _state.Cpu.F = (byte)((value == 0 ? CpuFlags.Zero : 0) | (carry != 0 ? CpuFlags.Carry : 0));
                    break;
                case 1: // RRC
                    carry = (byte)(value & 1);
                    value = (byte)((value >> 1) | (carry << 7));
                    _state.Cpu.F = (byte)((value == 0 ? CpuFlags.Zero : 0) | (carry != 0 ? CpuFlags.Carry : 0));
                    break;
                case 2: // RL
                {
                    var oldCarry = Flag(CpuFlags.Carry) ? 1 : 0;
                    value = (byte)((value << 1) | oldCarry);
                    _state.Cpu.F = (byte)((value == 0 ? CpuFlags.Zero : 0) | (carry != 0 ? CpuFlags.Carry : 0));
                    break;
                }
                case 3: // RR
                {
                    var oldCarry = Flag(CpuFlags.Carry) ? 0x80 : 0;
                    carry = (byte)(value & 1);
                    value = (byte)((value >> 1) | oldCarry);
                    _state.Cpu.F = (byte)((value == 0 ? CpuFlags.Zero : 0) | (carry != 0 ? CpuFlags.Carry : 0));
                    break;
                }
                case 4: // SLA
                    value = (byte)(value << 1);
                    _state.Cpu.F = (byte)((value == 0 ? CpuFlags.Zero : 0) | (carry != 0 ? CpuFlags.Carry : 0));
                    break;
                case 5: // SRA
                    carry = (byte)(value & 1);
                    value = (byte)((value >> 1) | (value & 0x80));
                    _state.Cpu.F = (byte)((value == 0 ? CpuFlags.Zero : 0) | (carry != 0 ? CpuFlags.Carry : 0));
                    break;
                case 6: // SWAP
                    value = (byte)((value << 4) | (value >> 4));
                    _state.Cpu.F = (byte)(value == 0 ? CpuFlags.Zero : 0);
                    break;
                case 7: // SRL
                    carry = (byte)(value & 1);
                    value >>= 1;
                    _state.Cpu.F = (byte)((value == 0 ? CpuFlags.Zero : 0) | (carry != 0 ? CpuFlags.Carry : 0));
                    break;
            }
        }

        WriteRegister(register, value);
        return memory ? 16 : 8;
    }

    private int PredictCbCycles(byte opcode) => (opcode & 7) == 6
        ? ((opcode >> 6) == 1 ? 12 : 16)
        : 8;

    private void WriteRegister(int index, byte value)
    {
        switch (index)
        {
            case 0: _state.Cpu.B = value; break;
            case 1: _state.Cpu.C = value; break;
            case 2: _state.Cpu.D = value; break;
            case 3: _state.Cpu.E = value; break;
            case 4: _state.Cpu.H = value; break;
            case 5: _state.Cpu.L = value; break;
            case 6: Write(_state.Cpu.HL, value); break;
            case 7: _state.Cpu.A = value; break;
            default: throw new InvalidOperationException("Invalid SM83 register index.");
        }
    }

    private int Load8(Action<byte> setter) { setter(Read(_state.Cpu.PC++)); return 8; }
    private int LoadRegister(byte opcode)
    {
        var destination = (opcode >> 3) & 7;
        var source = opcode & 7;
        WriteRegister(destination, ReadRegister(source));
        return destination == 6 || source == 6 ? 8 : 4;
    }

    private int Load16(Action<ushort> setter)
    {
        var low = Read(_state.Cpu.PC++); var high = Read(_state.Cpu.PC++);
        setter((ushort)(low | high << 8)); return 12;
    }

    private ushort ReadImmediateAddress()
    {
        var low = Read(_state.Cpu.PC++);
        var high = Read(_state.Cpu.PC++);
        return (ushort)(low | high << 8);
    }

    private int WriteSpAbsolute()
    {
        var address = ReadImmediateAddress();
        Write(address, (byte)_state.Cpu.SP);
        Write((ushort)(address + 1), (byte)(_state.Cpu.SP >> 8));
        return 20;
    }

    private int WriteAbsoluteA()
    {
        Write(ReadImmediateAddress(), _state.Cpu.A);
        return 16;
    }

    private int ReadAbsoluteA()
    {
        _state.Cpu.A = Read(ReadImmediateAddress());
        return 16;
    }

    private int WriteHighPage()
    {
        Write((ushort)(0xFF00 | Read(_state.Cpu.PC++)), _state.Cpu.A);
        return 12;
    }

    private int ReadHighPage()
    {
        _state.Cpu.A = Read((ushort)(0xFF00 | Read(_state.Cpu.PC++)));
        return 12;
    }

    private int WriteCPage()
    {
        Write((ushort)(0xFF00 | _state.Cpu.C), _state.Cpu.A);
        return 8;
    }

    private int ReadCPage()
    {
        _state.Cpu.A = Read((ushort)(0xFF00 | _state.Cpu.C));
        return 8;
    }

    private int AddSignedToSp()
    {
        var offset = (sbyte)Read(_state.Cpu.PC++);
        _state.Cpu.SP = AddSignedOffset(_state.Cpu.SP, offset);
        return 16;
    }

    private int LoadHlFromSignedSp()
    {
        var offset = (sbyte)Read(_state.Cpu.PC++);
        _state.Cpu.HL = AddSignedOffset(_state.Cpu.SP, offset);
        return 12;
    }

    private int LoadSpFromHl()
    {
        _state.Cpu.SP = _state.Cpu.HL;
        return 8;
    }

    private int JumpHl()
    {
        _state.Cpu.PC = _state.Cpu.HL;
        return 4;
    }

    private int Stop()
    {
        _ = Read(_state.Cpu.PC++);
        if (_model.IsColor() && _speedSwitchPrepared)
        {
            _doubleSpeed = !_doubleSpeed;
            _speedSwitchPrepared = false;
            return 4;
        }
        _state.Cpu.Halted = true;
        return 4;
    }

    private int StoreHlAndIncrement()
    {
        Write(_state.Cpu.HL++, _state.Cpu.A);
        return 8;
    }

    private int StoreHlAndDecrement()
    {
        Write(_state.Cpu.HL--, _state.Cpu.A);
        return 8;
    }

    private int LoadAAndIncrementHl()
    {
        _state.Cpu.A = Read(_state.Cpu.HL++);
        return 8;
    }

    private int LoadAAndDecrementHl()
    {
        _state.Cpu.A = Read(_state.Cpu.HL--);
        return 8;
    }

    private int StoreImmediateHl()
    {
        Write(_state.Cpu.HL, Read(_state.Cpu.PC++));
        return 12;
    }

    private ushort AddSignedOffset(ushort value, sbyte offset)
    {
        var unsignedOffset = (byte)offset;
        var result = value + offset;
        _state.Cpu.F = (byte)
        (
            (((value & 0x0F) + (unsignedOffset & 0x0F)) > 0x0F ? (byte)CpuFlags.HalfCarry : 0) |
            (((value & 0xFF) + unsignedOffset) > 0xFF ? (byte)CpuFlags.Carry : 0)
        );
        return (ushort)result;
    }

    private int IncrementPair(Func<ushort> getter, Action<ushort> setter)
    {
        setter(unchecked((ushort)(getter() + 1)));
        return 8;
    }

    private int DecrementPair(Func<ushort> getter, Action<ushort> setter)
    {
        setter(unchecked((ushort)(getter() - 1)));
        return 8;
    }

    private int AddHl(ushort value)
    {
        var hl = _state.Cpu.HL;
        var result = hl + value;
        _state.Cpu.HL = (ushort)result;
        _state.Cpu.F = (byte)((_state.Cpu.F & (byte)CpuFlags.Zero) |
            (((hl & 0x0FFF) + (value & 0x0FFF)) > 0x0FFF ? (byte)CpuFlags.HalfCarry : 0) |
            (result > 0xFFFF ? (byte)CpuFlags.Carry : 0));
        return 8;
    }
    private int WriteHl() { Write(_state.Cpu.HL, _state.Cpu.A); return 8; }
    private int ReadHl() { _state.Cpu.A = Read(_state.Cpu.HL); return 8; }
    private int XorA() { _state.Cpu.A = 0; _state.Cpu.F = (byte)CpuFlags.Zero; return 4; }
    private int RotateLeftCircularA()
    {
        var carry = (byte)(_state.Cpu.A >> 7);
        _state.Cpu.A = (byte)((_state.Cpu.A << 1) | carry);
        _state.Cpu.F = (byte)(carry != 0 ? CpuFlags.Carry : 0);
        return 4;
    }

    private int RotateRightCircularA()
    {
        var carry = (byte)(_state.Cpu.A & 1);
        _state.Cpu.A = (byte)((_state.Cpu.A >> 1) | (carry << 7));
        _state.Cpu.F = (byte)(carry != 0 ? CpuFlags.Carry : 0);
        return 4;
    }

    private int RotateLeftA()
    {
        var carry = (byte)(_state.Cpu.A >> 7);
        _state.Cpu.A = (byte)((_state.Cpu.A << 1) | (Flag(CpuFlags.Carry) ? 1 : 0));
        _state.Cpu.F = (byte)(carry != 0 ? CpuFlags.Carry : 0);
        return 4;
    }

    private int RotateRightA()
    {
        var carry = (byte)(_state.Cpu.A & 1);
        _state.Cpu.A = (byte)((_state.Cpu.A >> 1) | (Flag(CpuFlags.Carry) ? 0x80 : 0));
        _state.Cpu.F = (byte)(carry != 0 ? CpuFlags.Carry : 0);
        return 4;
    }

    private int DecimalAdjustA()
    {
        var subtract = Flag(CpuFlags.Subtract);
        var halfCarry = Flag(CpuFlags.HalfCarry);
        var carry = Flag(CpuFlags.Carry);
        var value = _state.Cpu.A;
        if (!subtract)
        {
            if (carry || value > 0x99) { value += 0x60; carry = true; }
            if (halfCarry || (value & 0x0F) > 9) value += 0x06;
        }
        else
        {
            if (carry) value -= 0x60;
            if (halfCarry) value -= 0x06;
        }
        _state.Cpu.A = (byte)value;
        _state.Cpu.F = (byte)((subtract ? CpuFlags.Subtract : 0) |
            (value == 0 ? CpuFlags.Zero : 0) | (carry ? CpuFlags.Carry : 0));
        return 4;
    }

    private int ComplementA()
    {
        _state.Cpu.A = (byte)~_state.Cpu.A;
        _state.Cpu.F = (byte)((_state.Cpu.F & ((byte)CpuFlags.Zero | (byte)CpuFlags.Carry)) |
            (byte)CpuFlags.Subtract | (byte)CpuFlags.HalfCarry);
        return 4;
    }

    private int SetCarryFlag()
    {
        _state.Cpu.F = (byte)((_state.Cpu.F & (byte)CpuFlags.Zero) | (byte)CpuFlags.Carry);
        return 4;
    }

    private int ComplementCarryFlag()
    {
        _state.Cpu.F = (byte)((_state.Cpu.F & (byte)CpuFlags.Zero) |
            (Flag(CpuFlags.Carry) ? 0 : (byte)CpuFlags.Carry));
        return 4;
    }

    private int DisableInterrupts()
    {
        _state.Cpu.Ime = false;
        _state.Cpu.ImeEnablePending = false;
        return 4;
    }

    private int EnableInterrupts()
    {
        _state.Cpu.ImeEnablePending = true;
        return 4;
    }

    private byte PendingInterrupts() => (byte)(_io[0x0F] & _io[0x7F] & InterruptMask);

    private bool TryServiceInterrupt()
    {
        var cpu = _state.Cpu;
        var pending = PendingInterrupts();
        if (pending == 0 || !cpu.Ime || cpu.ImeEnablePending) return false;

        var (mask, vector) = pending switch
        {
            _ when (pending & 0x01) != 0 => ((byte)0x01, (ushort)0x0040),
            _ when (pending & 0x02) != 0 => ((byte)0x02, (ushort)0x0048),
            _ when (pending & 0x04) != 0 => ((byte)0x04, (ushort)0x0050),
            _ when (pending & 0x08) != 0 => ((byte)0x08, (ushort)0x0058),
            _ => ((byte)0x10, (ushort)0x0060),
        };
        _io[0x0F] &= (byte)~mask;
        cpu.Halted = false;
        cpu.Ime = false;
        cpu.ImeEnablePending = false;
        Push(cpu.PC);
        cpu.PC = vector;
        Advance(ScaledCycles(20, _doubleSpeed));
        return true;
    }

    private static int ScaledCycles(int cycles, bool doubleSpeed) =>
        doubleSpeed ? Math.Max(1, cycles / 2) : cycles;
    private int Jump() { var lo = Read(_state.Cpu.PC); var hi = Read((ushort)(_state.Cpu.PC + 1)); _state.Cpu.PC = (ushort)(lo | hi << 8); return 16; }
    private int Halt() { _state.Cpu.Halted = true; return 4; }
    private int WritePair(ushort address) { Write(address, _state.Cpu.A); return 8; }
    private int ReadPair(ushort address) { _state.Cpu.A = Read(address); return 8; }
    private int Increment(Func<byte> getter, Action<byte> setter)
    {
        var value = getter();
        var result = (byte)(value + 1);
        setter(result);
        _state.Cpu.F = (byte)((_state.Cpu.F & (byte)CpuFlags.Carry) |
            (result == 0 ? (byte)CpuFlags.Zero : (byte)0) |
            ((value & 0x0F) == 0x0F ? (byte)CpuFlags.HalfCarry : (byte)0));
        return 4;
    }

    private int IncrementHl()
    {
        var value = Read(_state.Cpu.HL);
        Increment(() => value, result => Write(_state.Cpu.HL, result));
        return 12;
    }
    private int Decrement(Func<byte> getter, Action<byte> setter)
    {
        var value = getter();
        var result = (byte)(value - 1);
        setter(result);
        _state.Cpu.F = (byte)((_state.Cpu.F & (byte)CpuFlags.Carry) | (byte)CpuFlags.Subtract |
            (result == 0 ? (byte)CpuFlags.Zero : (byte)0) |
            ((value & 0x0F) == 0 ? (byte)CpuFlags.HalfCarry : (byte)0));
        return 4;
    }

    private int DecrementHl()
    {
        var value = Read(_state.Cpu.HL);
        Decrement(() => value, result => Write(_state.Cpu.HL, result));
        return 12;
    }
    private byte ReadRegister(int index) => index switch
    {
        0 => _state.Cpu.B, 1 => _state.Cpu.C, 2 => _state.Cpu.D, 3 => _state.Cpu.E,
        4 => _state.Cpu.H, 5 => _state.Cpu.L, 6 => Read(_state.Cpu.HL), 7 => _state.Cpu.A,
        _ => throw new InvalidOperationException("Invalid SM83 register index."),
    };
    private int AddA(byte value, bool memory)
    {
        var a = _state.Cpu.A;
        var result = a + value;
        _state.Cpu.A = (byte)result;
        _state.Cpu.F = (byte)((result & 0xFF) == 0 ? CpuFlags.Zero : 0);
        if (((a & 0x0F) + (value & 0x0F)) > 0x0F) _state.Cpu.F |= (byte)CpuFlags.HalfCarry;
        if (result > 0xFF) _state.Cpu.F |= (byte)CpuFlags.Carry;
        return memory ? 8 : 4;
    }

    private int AddImmediate() { AddA(Read(_state.Cpu.PC++), false); return 8; }

    private int AddCarryA(byte value, bool memory)
    {
        var carry = Flag(CpuFlags.Carry) ? 1 : 0;
        var a = _state.Cpu.A;
        var result = a + value + carry;
        _state.Cpu.A = (byte)result;
        _state.Cpu.F = (byte)((result & 0xFF) == 0 ? CpuFlags.Zero : 0);
        if (((a & 0x0F) + (value & 0x0F) + carry) > 0x0F) _state.Cpu.F |= (byte)CpuFlags.HalfCarry;
        if (result > 0xFF) _state.Cpu.F |= (byte)CpuFlags.Carry;
        return memory ? 8 : 4;
    }

    private int AddCarryImmediate()
    {
        var value = Read(_state.Cpu.PC++);
        var carry = Flag(CpuFlags.Carry) ? 1 : 0;
        var a = _state.Cpu.A;
        var result = a + value + carry;
        _state.Cpu.A = (byte)result;
        _state.Cpu.F = (byte)((result & 0xFF) == 0 ? CpuFlags.Zero : 0);
        if (((a & 0x0F) + (value & 0x0F) + carry) > 0x0F) _state.Cpu.F |= (byte)CpuFlags.HalfCarry;
        if (result > 0xFF) _state.Cpu.F |= (byte)CpuFlags.Carry;
        return 8;
    }
    private int SubA(byte value, bool memory)
    {
        var a = _state.Cpu.A;
        var result = a - value;
        _state.Cpu.A = (byte)result;
        _state.Cpu.F = (byte)(CpuFlags.Subtract | ((result & 0xFF) == 0 ? CpuFlags.Zero : 0));
        if ((a & 0x0F) < (value & 0x0F)) _state.Cpu.F |= (byte)CpuFlags.HalfCarry;
        if (a < value) _state.Cpu.F |= (byte)CpuFlags.Carry;
        return memory ? 8 : 4;
    }

    private int SubImmediate() { SubA(Read(_state.Cpu.PC++), false); return 8; }

    private int SubCarryA(byte value, bool memory)
    {
        var carry = Flag(CpuFlags.Carry) ? 1 : 0;
        var a = _state.Cpu.A;
        var result = a - value - carry;
        _state.Cpu.A = (byte)result;
        _state.Cpu.F = (byte)(CpuFlags.Subtract | ((result & 0xFF) == 0 ? CpuFlags.Zero : 0));
        if ((a & 0x0F) < (value & 0x0F) + carry) _state.Cpu.F |= (byte)CpuFlags.HalfCarry;
        if (a < value + carry) _state.Cpu.F |= (byte)CpuFlags.Carry;
        return memory ? 8 : 4;
    }

    private int SubCarryImmediate()
    {
        var value = Read(_state.Cpu.PC++);
        var carry = Flag(CpuFlags.Carry) ? 1 : 0;
        var a = _state.Cpu.A;
        var result = a - value - carry;
        _state.Cpu.A = (byte)result;
        _state.Cpu.F = (byte)(CpuFlags.Subtract | ((result & 0xFF) == 0 ? CpuFlags.Zero : 0));
        if ((a & 0x0F) < (value & 0x0F) + carry) _state.Cpu.F |= (byte)CpuFlags.HalfCarry;
        if (a < value + carry) _state.Cpu.F |= (byte)CpuFlags.Carry;
        return 8;
    }
    private int AndA(byte value, bool memory) { _state.Cpu.A &= value; _state.Cpu.F = (byte)(CpuFlags.HalfCarry | (_state.Cpu.A == 0 ? CpuFlags.Zero : 0)); return memory ? 8 : 4; }
    private int XorRegisterA(byte value, bool memory) { _state.Cpu.A ^= value; _state.Cpu.F = (byte)(_state.Cpu.A == 0 ? CpuFlags.Zero : 0); return memory ? 8 : 4; }
    private int OrA(byte value, bool memory) { _state.Cpu.A |= value; _state.Cpu.F = (byte)(_state.Cpu.A == 0 ? CpuFlags.Zero : 0); return memory ? 8 : 4; }
    private int CompareA(byte value, bool memory)
    {
        var a = _state.Cpu.A;
        var result = a - value;
        _state.Cpu.F = (byte)(CpuFlags.Subtract | ((result & 0xFF) == 0 ? CpuFlags.Zero : 0));
        if ((a & 0x0F) < (value & 0x0F)) _state.Cpu.F |= (byte)CpuFlags.HalfCarry;
        if (a < value) _state.Cpu.F |= (byte)CpuFlags.Carry;
        return memory ? 8 : 4;
    }

    private int AndImmediate() { AndA(Read(_state.Cpu.PC++), false); return 8; }
    private int XorImmediate()
    {
        _state.Cpu.A ^= Read(_state.Cpu.PC++);
        _state.Cpu.F = (byte)(_state.Cpu.A == 0 ? CpuFlags.Zero : 0);
        return 8;
    }
    private int OrImmediate() { OrA(Read(_state.Cpu.PC++), false); return 8; }
    private int CompareImmediate() { CompareA(Read(_state.Cpu.PC++), false); return 8; }
    private bool Flag(CpuFlags flag) => (_state.Cpu.F & (byte)flag) != 0;
    private int RelativeJump(bool condition)
    {
        var offset = (sbyte)Read(_state.Cpu.PC++);
        if (condition) { _state.Cpu.PC = (ushort)(_state.Cpu.PC + offset); return 12; }
        return 8;
    }
    private int AbsoluteJump(bool condition)
    {
        var low = Read(_state.Cpu.PC++); var high = Read(_state.Cpu.PC++);
        if (condition) { _state.Cpu.PC = (ushort)(low | high << 8); return 16; }
        return 12;
    }
    private int Push(ushort value)
    {
        _state.Cpu.SP--; Write(_state.Cpu.SP, (byte)(value >> 8));
        _state.Cpu.SP--; Write(_state.Cpu.SP, (byte)value); return 16;
    }
    private int Pop(Action<ushort> setter)
    {
        var low = Read(_state.Cpu.SP++); var high = Read(_state.Cpu.SP++);
        setter((ushort)(low | high << 8)); return 12;
    }
    private int Call()
    {
        var low = Read(_state.Cpu.PC++); var high = Read(_state.Cpu.PC++);
        var returnAddress = _state.Cpu.PC;
        Push(returnAddress); _state.Cpu.PC = (ushort)(low | high << 8); return 24;
    }
    private int Return() { _state.Cpu.PC = (ushort)(Read(_state.Cpu.SP++) | Read(_state.Cpu.SP++) << 8); return 16; }
    private int ConditionalCall(bool condition)
    {
        var low = Read(_state.Cpu.PC++); var high = Read(_state.Cpu.PC++);
        if (!condition) return 12;
        var returnAddress = _state.Cpu.PC;
        Push(returnAddress); _state.Cpu.PC = (ushort)(low | high << 8); return 24;
    }

    private int ConditionalReturn(bool condition)
    {
        if (!condition) return 8;
        _state.Cpu.PC = (ushort)(Read(_state.Cpu.SP++) | Read(_state.Cpu.SP++) << 8);
        return 20;
    }

    private int ReturnAndEnableInterrupts()
    {
        _state.Cpu.PC = (ushort)(Read(_state.Cpu.SP++) | Read(_state.Cpu.SP++) << 8);
        _state.Cpu.Ime = true;
        return 16;
    }

    private int Restart(ushort address)
    {
        Push(_state.Cpu.PC);
        _state.Cpu.PC = address;
        return 16;
    }
    private void Advance(int cycles) => _state.Scheduler.Advance(cycles);
    private void EnsureRom() { if (_cartridge is null) throw new InvalidOperationException("Load a ROM before executing."); }

    private byte Read(ushort address) => Read(address, false);

    private byte Read(ushort address, bool dmaTransfer)
    {
        if (!dmaTransfer && _dma.Active && !DmaCpuCanAccess(address)) return 0xFF;
        if (_bootMapped && _bootRom is not null && address < _bootRom.Length) return _bootRom[address];
        return address switch
        {
            < 0x8000 => _cartridge?.Read(address) ?? 0xFF,
            < 0xA000 => _ppu.CpuCanAccessVram ? _vram[(_vramBank * 0x2000) + address - 0x8000] : (byte)0xFF,
            < 0xC000 => _cartridge?.Read(address) ?? 0xFF,
            < 0xFE00 when address >= 0xC000 => _wram[WramOffset(address)],
            < 0xFEA0 => _ppu.CpuCanAccessOam ? _oam[address - 0xFE00] : (byte)0xFF,
            < 0xFF00 when !_model.IsColor() => 0,
            < 0xFF00 => 0xFF,
            0xFF00 => _joypad.Read(),
            0xFF0F => (byte)(0xE0 | (_io[0x0F] & InterruptMask)),
            0xFF4F => _model.IsColor() ? (byte)(0xFE | _vramBank) : (byte)0xFF,
            0xFF70 => _model.IsColor() ? (byte)(0xF8 | _wramBank) : (byte)0xFF,
            0xFF4D => _model.IsColor()
                ? (byte)((_doubleSpeed ? 0xFE : 0x7E) | (_speedSwitchPrepared ? 0x01 : 0))
                : (byte)0xFF,
            0xFF6C => _model.IsColor() ? (byte)(0xFE | (_io[0x6C] & 0x01)) : (byte)0xFF,
            0xFF51 => _model.IsColor() ? (byte)(_cgbDmaSource >> 8) : (byte)0xFF,
            0xFF52 => _model.IsColor() ? (byte)(_cgbDmaSource & 0xF0) : (byte)0xFF,
            0xFF53 => _model.IsColor() ? (byte)(0xE0 | ((_cgbDmaDestination >> 8) & 0x1F)) : (byte)0xFF,
            0xFF54 => _model.IsColor() ? (byte)(_cgbDmaDestination & 0xF0) : (byte)0xFF,
            0xFF55 => _model.IsColor() ? _cgbDmaStatus : (byte)0xFF,
            >= 0xFF10 and <= 0xFF3F => _apu.Read(address),
            0xFF76 or 0xFF77 => _apu.Read(address),
            >= 0xFF40 and <= 0xFF45 or >= 0xFF47 and <= 0xFF49 or >= 0xFF68 and <= 0xFF6B => _ppu.Read(address),
            >= 0xFF04 and <= 0xFF07 => _timer.Read(address),
            < 0xFF80 => _io[address - 0xFF00],
            < 0xFFFF => _hram[address - 0xFF80],
            0xFFFF => _io[0x7F],
        };
    }

    private void Write(ushort address, byte value)
    {
        if (_dma.Active && address != 0xFF46 && !DmaCpuCanAccess(address)) return;
        switch (address)
        {
            case < 0x8000: _cartridge?.Write(address, value); break;
            case < 0xA000:
                if (_ppu.CpuCanAccessVram) _vram[(_vramBank * 0x2000) + address - 0x8000] = value;
                break;
            case < 0xC000: _cartridge?.Write(address, value); break;
            case < 0xFE00 when address >= 0xC000: _wram[WramOffset(address)] = value; break;
            case < 0xFEA0:
                if (_ppu.CpuCanAccessOam) _oam[address - 0xFE00] = value;
                break;
            case < 0xFF00: break;
            case >= 0xFF04 and <= 0xFF07:
                _timer.Write(address, value);
                break;
            case 0xFF02:
                _serial.WriteControl(value);
                break;
            case 0xFF00:
                _joypad.Write(value);
                break;
            case >= 0xFF10 and <= 0xFF3F:
                _apu.Write(address, value);
                break;
            case 0xFF76 or 0xFF77:
                _apu.Write(address, value);
                break;
            case >= 0xFF40 and <= 0xFF45:
                _ppu.Write(address, value);
                break;
            case >= 0xFF47 and <= 0xFF49:
                _ppu.Write(address, value);
                break;
            case >= 0xFF68 and <= 0xFF6B:
                _ppu.Write(address, value);
                break;
            case 0xFF46:
                _io[0x46] = value;
                _dma.Start(value);
                break;
            case 0xFF0F:
                _io[0x0F] = (byte)(value & InterruptMask);
                break;
            case 0xFF4F:
                if (_model.IsColor()) _vramBank = (byte)(value & 0x01);
                break;
            case 0xFF70:
                if (_model.IsColor())
                {
                    var bank = (byte)(value & 0x07);
                    _wramBank = bank == 0 ? (byte)1 : bank;
                }
                break;
            case 0xFF4D:
                if (_model.IsColor()) _speedSwitchPrepared = (value & 0x01) != 0;
                break;
            case 0xFF6C:
                if (_model.IsColor()) _io[0x6C] = (byte)(value & 0x01);
                break;
            case 0xFF51:
                if (_model.IsColor()) _cgbDmaSource = (ushort)((value << 8) | (_cgbDmaSource & 0x00F0));
                break;
            case 0xFF52:
                if (_model.IsColor()) _cgbDmaSource = (ushort)((_cgbDmaSource & 0xFF00) | (value & 0xF0));
                break;
            case 0xFF53:
                if (_model.IsColor()) _cgbDmaDestination = (ushort)(0x8000 | ((value & 0x1F) << 8) | (_cgbDmaDestination & 0x00F0));
                break;
            case 0xFF54:
                if (_model.IsColor()) _cgbDmaDestination = (ushort)((_cgbDmaDestination & 0xFF00) | (value & 0xF0));
                break;
            case 0xFF55:
                if (_model.IsColor()) StartCgbDma(value);
                break;
            case < 0xFF80:
                _io[address - 0xFF00] = value;
                if (address == 0xFF50 && value != 0) _bootMapped = false;
                break;
            case < 0xFFFF: _hram[address - 0xFF80] = value; break;
            case 0xFFFF:
                _io[0x7F] = value;
                break;
        }
    }

    private static bool DmaCpuCanAccess(ushort address) => address >= 0xFF80;

    private int WramOffset(ushort address)
    {
        var banked = (address & 0x1000) != 0;
        return banked ? 0x1000 + (_wramBank - 1) * 0x1000 + (address & 0x0FFF) : address & 0x0FFF;
    }

    private void StartCgbDma(byte value)
    {
        if (_cgbDmaHblankActive && (value & 0x80) == 0)
        {
            _cgbDmaHblankActive = false;
            _cgbDmaStatus = 0xFF;
            return;
        }

        if ((value & 0x80) != 0)
        {
            _cgbDmaHblankActive = true;
            _cgbDmaStatus = (byte)(value & 0x7F);
            return;
        }

        var blocks = (value & 0x7F) + 1;
        for (var block = 0; block < blocks; block++)
        {
            for (var offset = 0; offset < 0x10; offset++)
            {
                var source = (ushort)(_cgbDmaSource + offset);
                var destination = (ushort)(_cgbDmaDestination + offset);
                _vram[(_vramBank * 0x2000) + destination - 0x8000] = ReadCgbDmaSource(source);
            }

            _cgbDmaSource = (ushort)(_cgbDmaSource + 0x10);
            _cgbDmaDestination = (ushort)(_cgbDmaDestination + 0x10);
        }

        _cgbDmaStatus = 0xFF;
    }

    private void TransferCgbHblankBlock()
    {
        if (!_cgbDmaHblankActive) return;

        for (var offset = 0; offset < 0x10; offset++)
        {
            var source = (ushort)(_cgbDmaSource + offset);
            var destination = (ushort)(_cgbDmaDestination + offset);
            _vram[(_vramBank * 0x2000) + destination - 0x8000] = ReadCgbDmaSource(source);
        }

        _cgbDmaSource = (ushort)(_cgbDmaSource + 0x10);
        _cgbDmaDestination = (ushort)(_cgbDmaDestination + 0x10);
        if (_cgbDmaStatus == 0)
        {
            _cgbDmaHblankActive = false;
            _cgbDmaStatus = 0xFF;
        }
        else
        {
            _cgbDmaStatus--;
        }
    }

    private void CancelCgbHblankDma()
    {
        _cgbDmaHblankActive = false;
        _cgbDmaStatus = 0xFF;
    }

    private byte ReadCgbDmaSource(ushort address) => address switch
    {
        < 0x8000 => _cartridge?.Read(address) ?? 0xFF,
        < 0xA000 => _vram[(_vramBank * 0x2000) + address - 0x8000],
        < 0xC000 => _cartridge?.Read(address) ?? 0xFF,
        < 0xFE00 => _wram[WramOffset(address)],
        _ => 0xFF,
    };
}
