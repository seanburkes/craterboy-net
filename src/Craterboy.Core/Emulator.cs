namespace Craterboy;

public sealed class Emulator
{
    private const int CyclesPerFrame = 70_224;
    private readonly GameBoyModel _model;
    private readonly EmulatorOptions _options;
    private readonly CpuState _cpu = new();
    private readonly byte[] _vram = new byte[0x4000];
    private readonly byte[] _wram = new byte[0x8000];
    private readonly byte[] _oam = new byte[0xA0];
    private readonly byte[] _io = new byte[0x80];
    private readonly byte[] _hram = new byte[0x7F];
    private Cartridge? _cartridge;
    private byte[]? _bootRom;
    private bool _bootMapped;

    public Emulator(GameBoyModel model, EmulatorOptions? options = null)
    {
        _model = model;
        _options = options ?? new EmulatorOptions();
        Reset();
    }

    public GameBoyModel Model => _model;
    public long CycleCount { get; private set; }
    public RomHeader? RomHeader { get; private set; }
    public CpuRegisterSnapshot Registers => _cpu.Snapshot;
    public bool BatteryDirty => _cartridge?.BatteryDirty ?? false;

    public void LoadRom(ReadOnlyMemory<byte> rom)
    {
        var header = RomHeader.Parse(rom.Span);
        if (header.RomSize != 0 && rom.Length < header.RomSize)
            throw new ArgumentException($"ROM header declares {header.RomSize} bytes but only {rom.Length} were supplied.", nameof(rom));
        var owned = rom.ToArray();
        _cartridge = Cartridge.Create(owned, header);
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
        CycleCount = 0;
        Array.Clear(_vram); Array.Clear(_wram); Array.Clear(_oam); Array.Clear(_io); Array.Clear(_hram);
        _bootMapped = _bootRom is not null && !_options.SkipBootRom;
        _cpu.A = _model.IsColor() ? (byte)0x11 : (byte)0x01;
        _cpu.F = 0xB0; _cpu.B = 0; _cpu.C = 0x13; _cpu.D = 0; _cpu.E = 0xD8;
        _cpu.H = 0x01; _cpu.L = 0x4D; _cpu.SP = 0xFFFE;
        _cpu.PC = _bootMapped ? (ushort)0 : (ushort)0x100;
        _cpu.Ime = false; _cpu.Halted = false;
        _io[0x50] = _bootMapped ? (byte)0 : (byte)1;
    }

    public byte PeekMemory(ushort address) => Read(address);
    public byte ReadMemory(ushort address) => Read(address);
    public void WriteMemory(ushort address, byte value) => Write(address, value);

    public int StepInstruction()
    {
        EnsureRom();
        if (_cpu.Halted) { Advance(4); return 4; }
        var opcode = Read(_cpu.PC++);
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
            if (remaining < 4) { Advance((int)remaining); break; }
            StepInstruction();
        }
    }

    public void RunFrame() => RunCycles(CyclesPerFrame);

    public byte[] SaveBattery() => (_cartridge ?? throw new InvalidOperationException("No ROM is loaded.")).SaveBattery();
    public void LoadBattery(ReadOnlySpan<byte> data) =>
        (_cartridge ?? throw new InvalidOperationException("No ROM is loaded.")).LoadBattery(data);

    private int Execute(byte opcode) => opcode switch
    {
        0x00 => 4,
        0x01 => Load16(v => _cpu.BC = v),
        0x11 => Load16(v => _cpu.DE = v),
        0x21 => Load16(v => _cpu.HL = v),
        0x31 => Load16(v => _cpu.SP = v),
        0x3E => Load8(v => _cpu.A = v),
        0x06 => Load8(v => _cpu.B = v),
        0x0E => Load8(v => _cpu.C = v),
        0x16 => Load8(v => _cpu.D = v),
        0x1E => Load8(v => _cpu.E = v),
        0x26 => Load8(v => _cpu.H = v),
        0x2E => Load8(v => _cpu.L = v),
        0x77 => WriteHl(),
        0x7E => ReadHl(),
        0xAF => XorA(),
        0xC3 => Jump(),
        0x76 => Halt(),
        _ => throw new NotSupportedException($"SM83 opcode 0x{opcode:X2} at 0x{_cpu.PC - 1:X4} is not ported yet."),
    };

    private int Load8(Action<byte> setter) { setter(Read(_cpu.PC++)); return 8; }
    private int Load16(Action<ushort> setter)
    {
        var low = Read(_cpu.PC++); var high = Read(_cpu.PC++);
        setter((ushort)(low | high << 8)); return 12;
    }
    private int WriteHl() { Write(_cpu.HL, _cpu.A); return 8; }
    private int ReadHl() { _cpu.A = Read(_cpu.HL); return 8; }
    private int XorA() { _cpu.A = 0; _cpu.F = (byte)CpuFlags.Zero; return 4; }
    private int Jump() { var lo = Read(_cpu.PC); var hi = Read((ushort)(_cpu.PC + 1)); _cpu.PC = (ushort)(lo | hi << 8); return 16; }
    private int Halt() { _cpu.Halted = true; return 4; }
    private void Advance(int cycles) => CycleCount = checked(CycleCount + cycles);
    private void EnsureRom() { if (_cartridge is null) throw new InvalidOperationException("Load a ROM before executing."); }

    private byte Read(ushort address)
    {
        if (_bootMapped && _bootRom is not null && address < _bootRom.Length) return _bootRom[address];
        return address switch
        {
            < 0x8000 => _cartridge?.Read(address) ?? 0xFF,
            < 0xA000 => _vram[address - 0x8000],
            < 0xC000 => _cartridge?.Read(address) ?? 0xFF,
            < 0xE000 => _wram[address - 0xC000],
            < 0xFE00 => _wram[address - 0xE000],
            < 0xFEA0 => _oam[address - 0xFE00],
            < 0xFF00 when !_model.IsColor() => 0,
            < 0xFF00 => 0xFF,
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
            case < 0xA000: _vram[address - 0x8000] = value; break;
            case < 0xC000: _cartridge?.Write(address, value); break;
            case < 0xE000: _wram[address - 0xC000] = value; break;
            case < 0xFE00: _wram[address - 0xE000] = value; break;
            case < 0xFEA0: _oam[address - 0xFE00] = value; break;
            case < 0xFF00: break;
            case < 0xFF80:
                _io[address - 0xFF00] = value;
                if (address == 0xFF50 && value != 0) _bootMapped = false;
                break;
            case < 0xFFFF: _hram[address - 0xFF80] = value; break;
            default: _io[0x7F] = value; break;
        }
    }
}
