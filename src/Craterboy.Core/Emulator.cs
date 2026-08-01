namespace Craterboy;

public sealed class Emulator
{
    private const int CyclesPerFrame = 70_224;
    private readonly GameBoyModel _model;
    private readonly EmulatorOptions _options;
    private readonly EmulatorState _state = new();
    private readonly byte[] _vram = new byte[0x4000];
    private readonly byte[] _wram = new byte[0x8000];
    private readonly byte[] _oam = new byte[0xA0];
    private readonly byte[] _io = new byte[0x80];
    private readonly byte[] _hram = new byte[0x7F];
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
        _dma = new OamDmaDevice(Read, (index, value) => _oam[index] = value);
        _serial = new SerialDevice(_io, _options.SerialEndpoint);
        _joypad = new JoypadDevice(_io);
        _ppu = new PpuDevice(_io, _vram, _oam);
        _apu = new ApuDevice(_io);
        _state.Scheduler.Register(_timer);
        _state.Scheduler.Register(_dma);
        _state.Scheduler.Register(_serial);
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
        cpu.Ime = false; cpu.Halted = false;
        _io[0x50] = _bootMapped ? (byte)0 : (byte)1;
    }

    public byte PeekMemory(ushort address) => Read(address);
    public byte ReadMemory(ushort address) => Read(address);
    public void WriteMemory(ushort address, byte value) => Write(address, value);

    public void SetButtonState(GameBoyButton button, bool pressed, int player = 0) =>
        _joypad.SetButtonState(button, pressed, player);

    public void CopyFrame(Span<byte> destination) => _ppu.CopyFrame(destination);

    public int StepInstruction()
    {
        EnsureRom();
        var cpu = _state.Cpu;
        if (cpu.Halted) { Advance(4); return 4; }
        var opcode = Read(cpu.PC++);
        var cycles = Execute(opcode);
        Advance(cycles);
        return cycles;
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
        >= 0x90 and <= 0x97 => SubA(ReadRegister(opcode & 7), (opcode & 7) == 6),
        >= 0xA0 and <= 0xA7 => AndA(ReadRegister(opcode & 7), (opcode & 7) == 6),
        >= 0xB0 and <= 0xB7 => OrA(ReadRegister(opcode & 7), (opcode & 7) == 6),
        >= 0xB8 and <= 0xBF => CompareA(ReadRegister(opcode & 7), (opcode & 7) == 6),
        0x18 => RelativeJump(true),
        0x20 => RelativeJump(!Flag(CpuFlags.Zero)),
        0x28 => RelativeJump(Flag(CpuFlags.Zero)),
        0x30 => RelativeJump(!Flag(CpuFlags.Carry)),
        0x38 => RelativeJump(Flag(CpuFlags.Carry)),
        0xAF => XorA(),
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
        if (_state.Cpu.Halted) return 4;
        var opcode = Read(_state.Cpu.PC);
        return opcode switch
        {
            0x01 or 0x11 or 0x21 or 0x31 => 12,
            0xCD => 24,
            0xC3 or 0xC2 or 0xCA or 0xD2 or 0xDA => 16,
            0xC5 or 0xD5 or 0xE5 or 0xF5 => 16,
            0xC1 or 0xD1 or 0xE1 or 0xF1 or 0xC9 => 12,
            0x77 or 0x7E or 0x02 or 0x0A or 0x12 or 0x1A => 8,
            >= 0x80 and <= 0x87 or >= 0x90 and <= 0x97 or >= 0xA0 and <= 0xA7 or >= 0xB0 and <= 0xBF
                => (opcode & 7) == 6 ? 16 : 4,
            0x18 or 0x20 or 0x28 or 0x30 or 0x38 => 12,
            _ => 8,
        };
    }

    private int Load8(Action<byte> setter) { setter(Read(_state.Cpu.PC++)); return 8; }
    private int Load16(Action<ushort> setter)
    {
        var low = Read(_state.Cpu.PC++); var high = Read(_state.Cpu.PC++);
        setter((ushort)(low | high << 8)); return 12;
    }
    private int WriteHl() { Write(_state.Cpu.HL, _state.Cpu.A); return 8; }
    private int ReadHl() { _state.Cpu.A = Read(_state.Cpu.HL); return 8; }
    private int XorA() { _state.Cpu.A = 0; _state.Cpu.F = (byte)CpuFlags.Zero; return 4; }
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
        return memory ? 16 : 4;
    }
    private int SubA(byte value, bool memory)
    {
        var a = _state.Cpu.A;
        var result = a - value;
        _state.Cpu.A = (byte)result;
        _state.Cpu.F = (byte)(CpuFlags.Subtract | ((result & 0xFF) == 0 ? CpuFlags.Zero : 0));
        if ((a & 0x0F) < (value & 0x0F)) _state.Cpu.F |= (byte)CpuFlags.HalfCarry;
        if (a < value) _state.Cpu.F |= (byte)CpuFlags.Carry;
        return memory ? 16 : 4;
    }
    private int AndA(byte value, bool memory) { _state.Cpu.A &= value; _state.Cpu.F = (byte)(CpuFlags.HalfCarry | (_state.Cpu.A == 0 ? CpuFlags.Zero : 0)); return memory ? 16 : 4; }
    private int OrA(byte value, bool memory) { _state.Cpu.A |= value; _state.Cpu.F = (byte)(_state.Cpu.A == 0 ? CpuFlags.Zero : 0); return memory ? 16 : 4; }
    private int CompareA(byte value, bool memory)
    {
        var a = _state.Cpu.A;
        var result = a - value;
        _state.Cpu.F = (byte)(CpuFlags.Subtract | ((result & 0xFF) == 0 ? CpuFlags.Zero : 0));
        if ((a & 0x0F) < (value & 0x0F)) _state.Cpu.F |= (byte)CpuFlags.HalfCarry;
        if (a < value) _state.Cpu.F |= (byte)CpuFlags.Carry;
        return memory ? 16 : 4;
    }
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
    private void Advance(int cycles) => _state.Scheduler.Advance(cycles);
    private void EnsureRom() { if (_cartridge is null) throw new InvalidOperationException("Load a ROM before executing."); }

    private byte Read(ushort address)
    {
        if (_bootMapped && _bootRom is not null && address < _bootRom.Length) return _bootRom[address];
        return address switch
        {
            < 0x8000 => _cartridge?.Read(address) ?? 0xFF,
            < 0xA000 => _ppu.CpuCanAccessVram ? _vram[address - 0x8000] : (byte)0xFF,
            < 0xC000 => _cartridge?.Read(address) ?? 0xFF,
            < 0xE000 => _wram[address - 0xC000],
            < 0xFE00 => _wram[address - 0xE000],
            < 0xFEA0 => _ppu.CpuCanAccessOam ? _oam[address - 0xFE00] : (byte)0xFF,
            < 0xFF00 when !_model.IsColor() => 0,
            < 0xFF00 => 0xFF,
            0xFF00 => _joypad.Read(),
            >= 0xFF10 and <= 0xFF26 => _apu.Read(address),
            >= 0xFF40 and <= 0xFF45 => _ppu.Read(address),
            >= 0xFF04 and <= 0xFF07 => _timer.Read(address),
            < 0xFF80 => _io[address - 0xFF00],
            < 0xFFFF => _hram[address - 0xFF80],
            _ => _io[0x7F],
        };
    }

    private void Write(ushort address, byte value)
    {
        switch (address)
        {
            case < 0x8000: _cartridge?.Write(address, value); break;
            case < 0xA000:
                if (_ppu.CpuCanAccessVram) _vram[address - 0x8000] = value;
                break;
            case < 0xC000: _cartridge?.Write(address, value); break;
            case < 0xE000: _wram[address - 0xC000] = value; break;
            case < 0xFE00: _wram[address - 0xE000] = value; break;
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
            case >= 0xFF10 and <= 0xFF26:
                _apu.Write(address, value);
                break;
            case >= 0xFF40 and <= 0xFF45:
                _ppu.Write(address, value);
                break;
            case 0xFF46:
                _io[0x46] = value;
                _dma.Start(value);
                break;
            case < 0xFF80:
                _io[address - 0xFF00] = value;
                if (address == 0xFF50 && value != 0) _bootMapped = false;
                break;
            case < 0xFFFF: _hram[address - 0xFF80] = value; break;
            default: _io[0x7F] = value; break;
        }
    }
}
