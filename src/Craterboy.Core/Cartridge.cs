using System.Buffers.Binary;

namespace Craterboy;

internal abstract class Cartridge
{
    protected readonly byte[] Rom;
    protected readonly byte[] Ram;
    private bool _batteryDirty;

    protected Cartridge(byte[] rom, int ramSize)
    {
        Rom = rom;
        Ram = new byte[ramSize];
    }

    public bool BatteryDirty => _batteryDirty;
    public abstract byte Read(ushort address);
    public abstract void Write(ushort address, byte value);
    public virtual void AdvanceCycles(int cycles) { }
    protected void Dirty() => _batteryDirty = true;
    public virtual BessRtc? SaveBessRtc() => null;
    public virtual void ValidateBessRtc(BessRtc state) => throw new InvalidDataException("BESS RTC state is not supported by this cartridge.");
    public virtual void LoadBessRtc(BessRtc state) => throw new InvalidDataException("BESS RTC state is not supported by this cartridge.");
    public virtual BessHuc3? SaveBessHuc3() => null;
    public virtual void ValidateBessHuc3(BessHuc3 state) => throw new InvalidDataException("BESS HuC3 state is not supported by this cartridge.");
    public virtual void LoadBessHuc3(BessHuc3 state) => throw new InvalidDataException("BESS HuC3 state is not supported by this cartridge.");
    public virtual BessMbc7? SaveBessMbc7() => null;
    public virtual void ValidateBessMbc7(BessMbc7 state) => throw new InvalidDataException("BESS MBC7 state is not supported by this cartridge.");
    public virtual void LoadBessMbc7(BessMbc7 state) => throw new InvalidDataException("BESS MBC7 state is not supported by this cartridge.");
    public virtual void WriteStateHash(BinaryWriter writer)
    {
        writer.Write(System.Security.Cryptography.SHA256.HashData(Rom));
        writer.Write(_batteryDirty);
    }

    public virtual void LoadBattery(ReadOnlySpan<byte> data)
    {
        if (data.Length != Ram.Length)
            throw new ArgumentException($"Battery data must be exactly {Ram.Length} bytes.", nameof(data));
        data.CopyTo(Ram);
        _batteryDirty = false;
    }

    public virtual byte[] SaveBattery()
    {
        _batteryDirty = false;
        return (byte[])Ram.Clone();
    }

    public static Cartridge Create(
        byte[] rom, RomHeader header, ITimeProvider timeProvider, IInfraredEndpoint? infraredEndpoint,
        IMotionProvider? motionProvider, ICameraSource? cameraSource) =>
        header.CartridgeType switch
    {
        0x00 or 0x08 or 0x09 => new RomOnlyCartridge(rom, header.RamSize),
        0x01 or 0x02 or 0x03 => new Mbc1Cartridge(rom, header.RamSize, IsMbc1Multicart(rom)),
        0x05 or 0x06 => new Mbc2Cartridge(rom),
        0x0B or 0x0C or 0x0D => new Mmm01Cartridge(rom, header.RamSize),
        0x0F or 0x10 or 0x11 or 0x12 or 0x13 => new Mbc3Cartridge(rom, header.RamSize, timeProvider),
        0x19 or 0x1A or 0x1B or 0x1C or 0x1D or 0x1E => new Mbc5Cartridge(rom, header.RamSize),
        0x20 => new Mbc6Cartridge(rom, header.RamSize),
        0x22 => new Mbc7Cartridge(rom, motionProvider),
        0xFC => new PocketCameraCartridge(rom, header.RamSize, cameraSource),
        0xFD => new Tama5Cartridge(rom, timeProvider),
        0xFF => new Huc1Cartridge(rom, header.RamSize, infraredEndpoint),
        0xFE => new Huc3Cartridge(rom, header.RamSize, timeProvider, infraredEndpoint),
        _ => throw new NotSupportedException($"Cartridge type 0x{header.CartridgeType:X2} is not implemented."),
    };

    private static bool IsMbc1Multicart(byte[] rom) =>
        rom.Length >= 0x44000 && rom.AsSpan(0x104, 0x30).SequenceEqual(rom.AsSpan(0x40104, 0x30));

    protected byte ReadRom(int index) => Rom[index % Rom.Length];
    protected byte ReadRam(int index) => Ram.Length == 0 ? (byte)0xFF : Ram[index % Ram.Length];
    protected void WriteRam(int index, byte value)
    {
        if (Ram.Length == 0) return;
        Ram[index % Ram.Length] = value;
        Dirty();
    }
}

internal sealed class PocketCameraCartridge : Cartridge
{
    private static readonly double[] GainValues =
    [
        0.8809390, 0.9149149, 0.9457498, 0.9739758, 1.0000000, 1.0241412, 1.0466537, 1.0677433,
        1.0875793, 1.1240310, 1.1568911, 1.1868043, 1.2142561, 1.2396208, 1.2743837, 1.3157323,
        1.3525190, 1.3856512, 1.4157897, 1.4434309, 1.4689574, 1.4926697, 1.5148087, 1.5355703,
        1.5551159, 1.5735801, 1.5910762, 1.6077008, 1.6235366, 1.6386550, 1.6531183, 1.6669808,
    ];
    private static readonly double[] EdgeRatios = [0.5, 0.75, 1, 1.25, 2, 3, 4, 5];
    private readonly byte[] _registers = new byte[0x36];
    private readonly ICameraSource? _cameraSource;
    private int _romBank = 1;
    private int _ramBank;
    private int _captureCycles;
    private byte _alignment;
    private bool _ramEnabled;
    private bool _registersMapped;

    public PocketCameraCartridge(byte[] rom, int ramSize, ICameraSource? cameraSource) : base(rom, ramSize)
    {
        _cameraSource = cameraSource;
    }

    public override byte Read(ushort address) => address switch
    {
        < 0x4000 => ReadRom(address),
        < 0x8000 => ReadRom(_romBank * 0x4000 + address - 0x4000),
        >= 0xA000 and < 0xC000 when _registersMapped => ReadRegister(address),
        >= 0xA000 and < 0xC000 when (_registers[0] & 1) != 0 => 0,
        >= 0xA100 and < 0xAF00 when _ramBank == 0 => ReadImage(address - 0xA100),
        >= 0xA000 and < 0xC000 => ReadRam(_ramBank * 0x2000 + address - 0xA000),
        _ => 0xFF,
    };

    public override void Write(ushort address, byte value)
    {
        switch (address)
        {
            case < 0x2000:
                _ramEnabled = (value & 0x0F) == 0x0A;
                break;
            case < 0x4000:
                _romBank = value;
                break;
            case < 0x6000:
                _ramBank = value & 0x0F;
                _registersMapped = (value & 0x10) != 0;
                break;
            case >= 0xA000 and < 0xC000 when _registersMapped:
                WriteRegister(address, value);
                break;
            case >= 0xA000 and < 0xC000 when _ramEnabled && (_registers[0] & 1) == 0:
                WriteRam(_ramBank * 0x2000 + address - 0xA000, value);
                break;
        }
    }

    public override void WriteStateHash(BinaryWriter writer)
    {
        base.WriteStateHash(writer);
        writer.Write(_romBank);
        writer.Write(_ramBank);
        writer.Write(_ramEnabled);
        writer.Write(_registersMapped);
        writer.Write(_registers);
        writer.Write(_captureCycles);
        writer.Write(_alignment);
    }

    public override void AdvanceCycles(int cycles)
    {
        _alignment = unchecked((byte)(_alignment + cycles));
        if (_captureCycles == 0) return;
        _captureCycles = Math.Max(0, _captureCycles - cycles);
        if (_captureCycles == 0) _registers[0] &= 0xFE;
    }

    private byte ReadRegister(ushort address) => (address & 0x7F) == 0 ? _registers[0] : (byte)0;

    private void WriteRegister(ushort address, byte value)
    {
        var register = address & 0x7F;
        if (register >= _registers.Length) return;
        if (register == 0)
        {
            value &= 7;
            if ((_registers[0] & 1) != 0) value |= 1;
            else if ((value & 1) != 0)
            {
                var exposure = (_registers[2] << 8) | _registers[3];
                _captureCycles = 129792 + ((_registers[1] & 0x80) != 0 ? 0 : 2048) + exposure * 64 + (_alignment & 4);
            }
        }
        _registers[register] = value;
    }

    private byte ReadImage(int address)
    {
        var tileX = address / 0x10 % 0x10;
        var tileY = address / 0x100;
        var y = ((address >> 1) & 7) + tileY * 8;
        var bit = address & 1;
        byte result = 0;
        for (var x = tileX * 8; x < tileX * 8 + 8; x++)
        {
            var color = ProcessedColor(x, y);
            if ((_registers[1] & 0xE0) == 0xE0)
            {
                var ratio = EdgeRatios[(_registers[4] >> 4) & 7];
                color += color * 4 * ratio;
                color -= ProcessedColor(x - 1, y) * ratio;
                color -= ProcessedColor(x + 1, y) * ratio;
                color -= ProcessedColor(x, y - 1) * ratio;
                color -= ProcessedColor(x, y + 1) * ratio;
            }
            var pattern = 6 + ((x & 3) + (y & 3) * 4) * 3;
            var shade = color < _registers[pattern] ? 3 : color < _registers[pattern + 1] ? 2 : color < _registers[pattern + 2] ? 1 : 0;
            result = (byte)((result << 1) | ((shade >> bit) & 1));
        }
        return result;
    }

    private double ProcessedColor(int x, int y)
    {
        x = x == 128 ? 127 : x > 128 || x < 0 ? 0 : x;
        y = y == 112 ? 111 : y >= 112 || y < 0 ? 0 : y;
        double color = _cameraSource?.GetPixel(x, y) ?? 0;
        color *= GainValues[_registers[1] & 0x1F];
        return color * ((_registers[2] << 8) | _registers[3]) / 0x1000;
    }
}

internal sealed class Tama5Cartridge : Cartridge
{
    private const int RtcSaveLength = 40;
    private readonly byte[] _registers = new byte[8];
    private readonly byte[][] _rtcPages = [new byte[16], new byte[16], new byte[16], new byte[16]];
    private readonly ITimeProvider _timeProvider;
    private DateTimeOffset _lastRtcUpdate;
    private int _romBank = 1;
    private byte _selectedRegister;
    private bool _timerEnabled = true;

    public Tama5Cartridge(byte[] rom, ITimeProvider timeProvider) : base(rom, 0x20)
    {
        _timeProvider = timeProvider;
        _lastRtcUpdate = timeProvider.UtcNow;
        Array.Fill(Ram, (byte)0xFF);
        _rtcPages[0][7] = 1;
        _rtcPages[0][9] = 1;
        _rtcPages[1][13] = 1;
        _rtcPages[2][13] = 2;
        _rtcPages[3][13] = 3;
        SetPageFlags(8, true);
    }

    public override byte Read(ushort address) => address switch
    {
        < 0x4000 => ReadRom(address),
        < 0x8000 => ReadRom(_romBank * 0x4000 + address - 0x4000),
        0xA000 => ReadData(),
        _ => 0xFF,
    };

    public override void Write(ushort address, byte value)
    {
        if (address == 0xA001)
        {
            _selectedRegister = value;
            return;
        }
        if (address != 0xA000 || _selectedRegister >= _registers.Length)
            return;

        _registers[_selectedRegister] = (byte)(value & 0x0F);
        switch (_selectedRegister)
        {
            case 0:
            case 1:
                _romBank = _registers[0] | (_registers[1] << 4);
                break;
            case 7 when (_registers[6] >> 1) == 0:
                WriteRam(EepromAddress, (byte)(_registers[4] | (_registers[5] << 4)));
                break;
            case 7 when (_registers[6] >> 1) == 2:
                WriteClockCommand();
                break;
            case 7 when (_registers[6] >> 1) == 4 && (_registers[7] & 1) == 0:
                WriteRtcPage();
                break;
        }
    }

    public override byte[] SaveBattery()
    {
        UpdateClock();
        var result = new byte[Ram.Length + RtcSaveLength];
        base.SaveBattery().CopyTo(result, 0);
        for (var page = 0; page < 4; page++)
            for (var index = 0; index < 8; index++)
                result[Ram.Length + page * 8 + index] = (byte)(_rtcPages[page][index * 2] | (_rtcPages[page][index * 2 + 1] << 4));
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(Ram.Length + 32), _lastRtcUpdate.ToUnixTimeSeconds());
        return result;
    }

    public override void LoadBattery(ReadOnlySpan<byte> data)
    {
        if (data.Length == Ram.Length)
        {
            base.LoadBattery(data);
            _lastRtcUpdate = _timeProvider.UtcNow;
            return;
        }
        if (data.Length != Ram.Length + RtcSaveLength)
            throw new ArgumentException($"TAMA5 battery data must be exactly {Ram.Length} or {Ram.Length + RtcSaveLength} bytes.", nameof(data));
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64LittleEndian(data[(Ram.Length + 32)..]));
        base.LoadBattery(data[..Ram.Length]);
        for (var page = 0; page < 4; page++)
            for (var index = 0; index < 8; index++)
            {
                var packed = data[Ram.Length + page * 8 + index];
                _rtcPages[page][index * 2] = (byte)(packed & 0x0F);
                _rtcPages[page][index * 2 + 1] = (byte)(packed >> 4);
            }
        _lastRtcUpdate = timestamp;
        _timerEnabled = (_rtcPages[0][13] & 8) != 0;
        UpdateClock();
    }

    public override void WriteStateHash(BinaryWriter writer)
    {
        base.WriteStateHash(writer);
        writer.Write(_romBank);
        writer.Write(_selectedRegister);
        writer.Write(_registers);
        writer.Write(Ram);
        writer.Write(_timerEnabled);
        foreach (var page in _rtcPages) writer.Write(page);
    }

    private int EepromAddress => ((_registers[6] << 4) & 0x10) | _registers[7];

    private byte ReadData()
    {
        if (_selectedRegister == 0x0A)
            return 0xF1;
        if (_selectedRegister is not (0x0C or 0x0D))
            return 0xF1;

        byte value;
        if ((_registers[6] >> 1) == 1)
            value = ReadRam(EepromAddress);
        else if ((_registers[6] >> 1) == 2)
        {
            UpdateClock();
            value = (((_registers[6] & 1) << 4) | _registers[7]) switch
            {
                6 => (byte)(_rtcPages[0][2] | (_rtcPages[0][3] << 4)),
                7 => (byte)(_rtcPages[0][4] | (_rtcPages[0][5] << 4)),
                _ => (byte)0xFF,
            };
        }
        else if ((_registers[6] >> 1) == 4 && _selectedRegister == 0x0C)
        {
            UpdateClock();
            var page = _registers[7] >> 1;
            value = page < _rtcPages.Length && _registers[4] < 16 ? _rtcPages[page][_registers[4]] : (byte)0;
        }
        else
            return 0xF1;
        return (byte)(0xF0 | (_selectedRegister == 0x0C ? value & 0x0F : value >> 4));
    }

    private void WriteClockCommand()
    {
        UpdateClock();
        var command = ((_registers[6] & 1) << 4) | _registers[7];
        switch (command)
        {
            case 0: _timerEnabled = false; SetPageFlags(8, false); break;
            case 1: _timerEnabled = true; _lastRtcUpdate = _timeProvider.UtcNow; SetPageFlags(8, true); break;
            case 4: SetRawBcd(2); break;
            case 5: SetRawBcd(4); break;
            case 0x10: SetPageFlags(4, false); break;
            case 0x11: SetPageFlags(4, true); break;
        }
    }

    private void WriteRtcPage()
    {
        var page = _registers[7] >> 1;
        var index = _registers[4];
        if (page >= _rtcPages.Length || index >= 13) return;
        UpdateClock();
        _rtcPages[page][index] = (byte)(_registers[5] & RtcMask(page, index));
        _lastRtcUpdate = _timeProvider.UtcNow;
        Dirty();
    }

    private void UpdateClock()
    {
        var now = _timeProvider.UtcNow;
        var seconds = (long)(now - _lastRtcUpdate).TotalSeconds;
        if (!_timerEnabled || seconds <= 0) return;
        _lastRtcUpdate = _lastRtcUpdate.AddSeconds(seconds);
        var page = _rtcPages[0];
        try
        {
            var clock = new DateTimeOffset(2000 + Bcd(page, 11), Bcd(page, 9), Bcd(page, 7), Bcd(page, 4), Bcd(page, 2), Bcd(page, 0), TimeSpan.Zero).AddSeconds(seconds);
            SetBcd(0, clock.Second);
            SetBcd(2, clock.Minute);
            SetBcd(4, clock.Hour);
            page[6] = (byte)clock.DayOfWeek;
            SetBcd(7, clock.Day);
            SetBcd(9, clock.Month);
            SetBcd(11, clock.Year % 100);
        }
        catch (ArgumentOutOfRangeException) { }
    }

    private int Bcd(byte[] page, int index) => page[index] + page[index + 1] * 10;
    private void SetBcd(int index, int value)
    {
        _rtcPages[0][index] = (byte)(value % 10);
        _rtcPages[0][index + 1] = (byte)(value / 10);
    }
    private void SetRawBcd(int index)
    {
        _rtcPages[0][index] = _registers[4];
        _rtcPages[0][index + 1] = _registers[5];
        _lastRtcUpdate = _timeProvider.UtcNow;
        Dirty();
    }
    private void SetPageFlags(byte flag, bool enabled)
    {
        foreach (var page in _rtcPages)
            page[13] = enabled ? (byte)(page[13] | flag) : (byte)(page[13] & ~flag);
    }
    private static byte RtcMask(int page, int index) => page switch
    {
        0 => index switch { 1 or 3 or 5 => 7, 10 => 1, 13 => 0, _ => 0x0F },
        1 => index switch { 3 or 5 => 7, 10 => 1, 11 => 3, 13 => 0, _ => 0x0F },
        _ => 0x0F,
    };
}

internal sealed class Mbc7Cartridge : Cartridge
{
    private readonly IMotionProvider? _motionProvider;
    private bool _ramEnabled;
    private bool _secondaryRamEnabled;
    private int _romBank = 1;
    private ushort _xLatch = 0x8000;
    private ushort _yLatch = 0x8000;
    private bool _latchReady = true;
    private bool _eepromDo = true;
    private bool _eepromDi;
    private bool _eepromClock;
    private bool _eepromChipSelect;
    private bool _eepromWriteEnabled;
    private ushort _eepromCommand;
    private ushort _readBits = 0xFFFF;
    private byte _argumentBitsLeft;

    public Mbc7Cartridge(byte[] rom, IMotionProvider? motionProvider) : base(rom, 0x100)
    {
        _motionProvider = motionProvider;
        Array.Fill(Ram, (byte)0xFF);
    }

    public override byte Read(ushort address) => address switch
    {
        < 0x4000 => ReadRom(address),
        < 0x8000 => ReadRom(_romBank * 0x4000 + address - 0x4000),
        >= 0xA000 and < 0xB000 when _ramEnabled && _secondaryRamEnabled => ReadRegister(address),
        _ => 0xFF,
    };

    public override void Write(ushort address, byte value)
    {
        switch (address)
        {
            case < 0x2000: _ramEnabled = value == 0x0A; break;
            case < 0x4000: _romBank = value; break;
            case < 0x6000: _secondaryRamEnabled = value == 0x40; break;
            case >= 0xA000 and < 0xB000 when _ramEnabled && _secondaryRamEnabled:
                WriteRegister(address, value);
                break;
        }
    }

    public override BessMbc7? SaveBessMbc7() => new(
        (byte)((_latchReady ? 1 : 0) |
            (_eepromDo ? 2 : 0) |
            (_eepromDi ? 4 : 0) |
            (_eepromClock ? 8 : 0) |
            (_eepromChipSelect ? 0x10 : 0) |
            (_eepromWriteEnabled ? 0x20 : 0)),
        _argumentBitsLeft, _eepromCommand, _readBits, _xLatch, _yLatch);

    public override void ValidateBessMbc7(BessMbc7 state)
    {
        if ((state.Flags & 0xC0) != 0 || state.ArgumentBitsLeft > 16 || state.EepromCommand > 0x7FF)
            throw new InvalidDataException("BESS MBC7 state is invalid.");
    }

    public override void LoadBessMbc7(BessMbc7 state)
    {
        ValidateBessMbc7(state);
        _latchReady = (state.Flags & 1) != 0;
        _eepromDo = (state.Flags & 2) != 0;
        _eepromDi = (state.Flags & 4) != 0;
        _eepromClock = (state.Flags & 8) != 0;
        _eepromChipSelect = (state.Flags & 0x10) != 0;
        _eepromWriteEnabled = (state.Flags & 0x20) != 0;
        _argumentBitsLeft = state.ArgumentBitsLeft;
        _eepromCommand = state.EepromCommand;
        _readBits = state.PendingReadBits;
        _xLatch = state.LatchedGyroX;
        _yLatch = state.LatchedGyroY;
    }

    public override void WriteStateHash(BinaryWriter writer)
    {
        base.WriteStateHash(writer);
        writer.Write(_ramEnabled);
        writer.Write(_secondaryRamEnabled);
        writer.Write(_romBank);
        writer.Write(_xLatch);
        writer.Write(_yLatch);
        writer.Write(_latchReady);
        writer.Write(_eepromDo);
        writer.Write(_eepromDi);
        writer.Write(_eepromClock);
        writer.Write(_eepromChipSelect);
        writer.Write(_eepromWriteEnabled);
        writer.Write(_eepromCommand);
        writer.Write(_readBits);
        writer.Write(_argumentBitsLeft);
        writer.Write(_motionProvider?.X ?? 0);
        writer.Write(_motionProvider?.Y ?? 0);
    }

    private byte ReadRegister(ushort address) => ((address >> 4) & 0x0F) switch
    {
        2 => (byte)_xLatch,
        3 => (byte)(_xLatch >> 8),
        4 => (byte)_yLatch,
        5 => (byte)(_yLatch >> 8),
        6 => 0,
        8 => (byte)((_eepromDo ? 1 : 0) |
            (_eepromDi ? 2 : 0) |
            (_eepromClock ? 0x40 : 0) |
            (_eepromChipSelect ? 0x80 : 0)),
        _ => 0xFF,
    };

    private void WriteRegister(ushort address, byte value)
    {
        switch ((address >> 4) & 0x0F)
        {
            case 0 when value == 0x55:
                _latchReady = true;
                _xLatch = _yLatch = 0x8000;
                break;
            case 1 when value == 0xAA:
                _latchReady = false;
                _xLatch = MotionValue(_motionProvider?.X ?? 0);
                _yLatch = MotionValue(_motionProvider?.Y ?? 0);
                break;
            case 8:
                ClockEeprom(value);
                break;
        }
    }

    private void ClockEeprom(byte value)
    {
        _eepromChipSelect = (value & 0x80) != 0;
        _eepromDi = (value & 2) != 0;
        if (_eepromChipSelect && !_eepromClock && (value & 0x40) != 0)
        {
            _eepromDo = (_readBits & 0x8000) != 0;
            _readBits = (ushort)((_readBits << 1) | 1);
            if (_argumentBitsLeft == 0) ShiftCommandBit();
            else ShiftArgumentBit();
        }
        _eepromClock = (value & 0x40) != 0;
    }

    private void ShiftCommandBit()
    {
        _eepromCommand = (ushort)((_eepromCommand << 1) | (_eepromDi ? 1 : 0));
        if ((_eepromCommand & 0x400) == 0) return;
        switch ((_eepromCommand >> 6) & 0x0F)
        {
            case >= 8 and <= 0x0B:
                _readBits = ReadEepromWord(_eepromCommand & 0x7F);
                _eepromCommand = 0;
                break;
            case 3:
                _eepromWriteEnabled = true;
                _eepromCommand = 0;
                break;
            case 0:
                _eepromWriteEnabled = false;
                _eepromCommand = 0;
                break;
            case >= 4 and <= 7:
                if (_eepromWriteEnabled) WriteEepromWord(_eepromCommand & 0x7F, 0);
                _argumentBitsLeft = 16;
                break;
            case >= 0x0C:
                if (_eepromWriteEnabled)
                {
                    WriteEepromWord(_eepromCommand & 0x7F, 0xFFFF);
                    _readBits = 0x3FFF;
                }
                _eepromCommand = 0;
                break;
            case 2:
                if (_eepromWriteEnabled)
                {
                    Array.Fill(Ram, (byte)0xFF);
                    Dirty();
                    _readBits = 0x00FF;
                }
                _eepromCommand = 0;
                break;
            case 1:
                if (_eepromWriteEnabled)
                {
                    Array.Clear(Ram);
                    Dirty();
                }
                _argumentBitsLeft = 16;
                break;
        }
    }

    private void ShiftArgumentBit()
    {
        _argumentBitsLeft--;
        _eepromDo = true;
        if (_eepromDi)
        {
            var bit = (ushort)(1 << _argumentBitsLeft);
            if ((_eepromCommand & 0x100) != 0)
                WriteEepromWord(_eepromCommand & 0x7F, (ushort)(ReadEepromWord(_eepromCommand & 0x7F) | bit));
            else
                for (var index = 0; index < 0x7F; index++)
                    WriteEepromWord(index, (ushort)(ReadEepromWord(index) | bit));
        }
        if (_argumentBitsLeft == 0)
        {
            var writingWord = (_eepromCommand & 0x100) != 0;
            _eepromCommand = 0;
            _readBits = writingWord ? (ushort)0x00FF : (ushort)0x3FFF;
        }
    }

    private ushort ReadEepromWord(int address) =>
        BinaryPrimitives.ReadUInt16LittleEndian(Ram.AsSpan(address * 2, 2));

    private void WriteEepromWord(int address, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(Ram.AsSpan(address * 2, 2), value);
        Dirty();
    }

    private static ushort MotionValue(double value) => unchecked((ushort)(int)(0x81D0 + 0x70 * value));
}

internal sealed class Mbc6Cartridge(byte[] rom, int ramSize) : Cartridge(rom, ramSize)
{
    private bool _ramEnabled;
    private int _ramBankA;
    private int _ramBankB;
    private bool _flashEnabled;
    private bool _flashWriteEnabled;
    private int _romBankA;
    private int _romBankB;
    private bool _flashBankA;
    private bool _flashBankB;

    public override byte Read(ushort address) => address switch
    {
        < 0x4000 => ReadRom(address),
        < 0x6000 when _flashBankA => ReadFlash(),
        < 0x6000 => ReadRom(_romBankA * 0x2000 + address - 0x4000),
        < 0x8000 when _flashBankB => ReadFlash(),
        < 0x8000 => ReadRom(_romBankB * 0x2000 + address - 0x6000),
        >= 0xA000 and < 0xB000 when _ramEnabled =>
            ReadRam(_ramBankA * 0x1000 + address - 0xA000),
        >= 0xB000 and < 0xC000 when _ramEnabled =>
            ReadRam(_ramBankB * 0x1000 + address - 0xB000),
        _ => 0xFF,
    };

    public override void Write(ushort address, byte value)
    {
        switch (address)
        {
            case < 0x0400: _ramEnabled = (value & 0x0F) == 0x0A; break;
            case < 0x0800: _ramBankA = value & 7; break;
            case < 0x0C00: _ramBankB = value & 7; break;
            case < 0x1000: _flashEnabled = (value & 1) != 0; break;
            case 0x1000: _flashWriteEnabled = (value & 1) != 0; break;
            case >= 0x2000 and < 0x2800: _romBankA = value & 0x7F; break;
            case >= 0x2800 and < 0x3000: _flashBankA = value == 0x08; break;
            case >= 0x3000 and < 0x3800: _romBankB = value & 0x7F; break;
            case >= 0x3800 and < 0x4000: _flashBankB = value == 0x08; break;
            case >= 0xA000 and < 0xB000 when _ramEnabled:
                WriteRam(_ramBankA * 0x1000 + address - 0xA000, value);
                break;
            case >= 0xB000 and < 0xC000 when _ramEnabled:
                WriteRam(_ramBankB * 0x1000 + address - 0xB000, value);
                break;
        }
    }

    public override void WriteStateHash(BinaryWriter writer)
    {
        base.WriteStateHash(writer);
        writer.Write(_ramEnabled);
        writer.Write(_ramBankA);
        writer.Write(_ramBankB);
        writer.Write(_flashEnabled);
        writer.Write(_flashWriteEnabled);
        writer.Write(_romBankA);
        writer.Write(_romBankB);
        writer.Write(_flashBankA);
        writer.Write(_flashBankB);
    }

    private static byte ReadFlash() => 0xFF;
}

internal sealed class Huc3Cartridge(
    byte[] rom, int ramSize, ITimeProvider timeProvider, IInfraredEndpoint? infraredEndpoint) : Cartridge(rom, ramSize)
{
    private const int RtcLength = 17;
    private DateTimeOffset _lastRtcUpdate = timeProvider.UtcNow;
    private int _romBank = 1;
    private int _ramBank;
    private byte _mode;
    private ushort _minutes = 0x0FFF;
    private ushort _days = 0xFFFF;
    private ushort _alarmMinutes;
    private ushort _alarmDays;
    private byte _accessIndex;
    private byte _read;
    private byte _accessFlags;
    private bool _alarmEnabled;
    private bool _infraredOutput;

    public override byte Read(ushort address) => address switch
    {
        < 0x4000 => ReadRom(address),
        < 0x8000 => ReadRom(_romBank * 0x4000 + address - 0x4000),
        >= 0xA000 and < 0xC000 => ReadExternal(address),
        _ => 0xFF,
    };

    public override void Write(ushort address, byte value)
    {
        switch (address)
        {
            case < 0x2000: _mode = (byte)(value & 0x0F); break;
            case < 0x4000: _romBank = value & 0x7F; break;
            case < 0x6000: _ramBank = value & 0x0F; break;
            case >= 0xA000 and < 0xC000: WriteExternal(address, value); break;
        }
    }

    public override byte[] SaveBattery()
    {
        UpdateClock();
        var ram = base.SaveBattery();
        var result = new byte[ram.Length + RtcLength];
        ram.CopyTo(result, 0);
        WriteRtc(result.AsSpan(ram.Length));
        return result;
    }

    public override void LoadBattery(ReadOnlySpan<byte> data)
    {
        if (data.Length != Ram.Length + RtcLength)
            throw new ArgumentException($"HuC3 battery data must be exactly {Ram.Length + RtcLength} bytes.", nameof(data));
        ValidateRtc(data[Ram.Length..], nameof(data));
        base.LoadBattery(data[..Ram.Length]);
        LoadRtc(data[Ram.Length..]);
    }

    public override BessHuc3? SaveBessHuc3()
    {
        UpdateClock();
        return new(
            checked((ulong)_lastRtcUpdate.ToUnixTimeSeconds()), _minutes, _days,
            _alarmMinutes, _alarmDays, _alarmEnabled);
    }

    public override void ValidateBessHuc3(BessHuc3 state)
    {
        try
        {
            _ = DateTimeOffset.FromUnixTimeSeconds(checked((long)state.LastUnixSecond));
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
        {
            throw new InvalidDataException("BESS HuC3 timestamp is invalid.", exception);
        }
    }

    public override void LoadBessHuc3(BessHuc3 state)
    {
        ValidateBessHuc3(state);
        _lastRtcUpdate = DateTimeOffset.FromUnixTimeSeconds((long)state.LastUnixSecond);
        _minutes = state.Minutes;
        _days = state.Days;
        _alarmMinutes = state.AlarmMinutes;
        _alarmDays = state.AlarmDays;
        _alarmEnabled = state.AlarmEnabled;
        UpdateClock();
    }

    public override void WriteStateHash(BinaryWriter writer)
    {
        base.WriteStateHash(writer);
        writer.Write(_romBank);
        writer.Write(_ramBank);
        writer.Write(_mode);
        writer.Write(_minutes);
        writer.Write(_days);
        writer.Write(_alarmMinutes);
        writer.Write(_alarmDays);
        writer.Write(_accessIndex);
        writer.Write(_read);
        writer.Write(_accessFlags);
        writer.Write(_alarmEnabled);
        writer.Write(_infraredOutput);
        writer.Write(infraredEndpoint?.Input == true);
    }

    private byte ReadExternal(ushort address)
    {
        UpdateClock();
        return _mode switch
        {
            0 or 0x0A => ReadRam(_ramBank * 0x2000 + address - 0xA000),
            0x0C => _accessFlags == 2 ? (byte)1 : _read,
            0x0D => 1,
            0x0E => infraredEndpoint?.Input == true ? (byte)1 : (byte)0,
            _ => 1,
        };
    }

    private void WriteExternal(ushort address, byte value)
    {
        switch (_mode)
        {
            case 0x0A:
                WriteRam(_ramBank * 0x2000 + address - 0xA000, value);
                break;
            case 0x0B:
                UpdateClock();
                WriteRtcCommand(value);
                break;
            case 0x0E:
                var output = (value & 1) != 0;
                if (output != _infraredOutput)
                {
                    _infraredOutput = output;
                    infraredEndpoint?.SetOutput(output);
                }
                break;
        }
    }

    private void WriteRtcCommand(byte value)
    {
        switch (value >> 4)
        {
            case 1:
                if (_accessIndex < 3) _read = (byte)((_minutes >> (_accessIndex * 4)) & 0x0F);
                else if (_accessIndex < 7) _read = (byte)((_days >> ((_accessIndex - 3) * 4)) & 0x0F);
                _accessIndex++;
                break;
            case 2:
            case 3:
                WriteRtcNibble((byte)(value & 0x0F));
                if ((value >> 4) == 3) _accessIndex++;
                break;
            case 4: _accessIndex = (byte)((_accessIndex & 0xF0) | (value & 0x0F)); break;
            case 5: _accessIndex = (byte)((_accessIndex & 0x0F) | ((value & 0x0F) << 4)); break;
            case 6: _accessFlags = (byte)(value & 0x0F); break;
        }
    }

    private void WriteRtcNibble(byte value)
    {
        if (_accessIndex < 3) _minutes = SetNibble(_minutes, _accessIndex, value);
        else if (_accessIndex < 7) _days = SetNibble(_days, _accessIndex - 3, value);
        else if (_accessIndex is >= 0x58 and <= 0x5A)
        {
            _alarmMinutes = SetNibble(_alarmMinutes, _accessIndex - 0x58, value);
            Dirty();
        }
        else if (_accessIndex is >= 0x5B and <= 0x5E)
        {
            _alarmDays = SetNibble(_alarmDays, _accessIndex - 0x5B, value);
            Dirty();
        }
        else if (_accessIndex == 0x5F)
        {
            _alarmEnabled = (value & 1) != 0;
            Dirty();
        }
    }

    private void UpdateClock()
    {
        var now = timeProvider.UtcNow;
        var elapsedMinutes = (long)((now - _lastRtcUpdate).TotalSeconds / 60);
        if (elapsedMinutes <= 0) return;
        _lastRtcUpdate = _lastRtcUpdate.AddMinutes(elapsedMinutes);
        var totalMinutes = _minutes + elapsedMinutes;
        _days = unchecked((ushort)(_days + totalMinutes / 1440));
        _minutes = (ushort)(totalMinutes % 1440);
    }

    private void WriteRtc(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, checked((ulong)_lastRtcUpdate.ToUnixTimeSeconds()));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], _minutes);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[0x0A..], _days);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[0x0C..], _alarmMinutes);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[0x0E..], _alarmDays);
        destination[0x10] = _alarmEnabled ? (byte)1 : (byte)0;
    }

    private void LoadRtc(ReadOnlySpan<byte> source)
    {
        var timestamp = BinaryPrimitives.ReadUInt64LittleEndian(source);
        _lastRtcUpdate = DateTimeOffset.FromUnixTimeSeconds((long)timestamp);
        _minutes = BinaryPrimitives.ReadUInt16LittleEndian(source[8..]);
        _days = BinaryPrimitives.ReadUInt16LittleEndian(source[0x0A..]);
        _alarmMinutes = BinaryPrimitives.ReadUInt16LittleEndian(source[0x0C..]);
        _alarmDays = BinaryPrimitives.ReadUInt16LittleEndian(source[0x0E..]);
        _alarmEnabled = source[0x10] != 0;
        UpdateClock();
    }

    private static void ValidateRtc(ReadOnlySpan<byte> source, string parameterName)
    {
        try
        {
            _ = DateTimeOffset.FromUnixTimeSeconds(checked((long)BinaryPrimitives.ReadUInt64LittleEndian(source)));
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
        {
            throw new ArgumentException("HuC3 battery timestamp is invalid.", parameterName, exception);
        }
        if (source[0x10] > 1)
            throw new ArgumentException("HuC3 battery alarm flag is invalid.", parameterName);
    }

    private static ushort SetNibble(ushort current, int index, byte value) =>
        (ushort)((current & ~(0x0F << (index * 4))) | ((value & 0x0F) << (index * 4)));
}

internal sealed class Huc1Cartridge(
    byte[] rom, int ramSize, IInfraredEndpoint? infraredEndpoint) : Cartridge(rom, ramSize)
{
    private int _romBank = 1;
    private int _ramBank;
    private bool _infraredMode;
    private bool _infraredOutput;

    public override byte Read(ushort address) => address switch
    {
        < 0x4000 => ReadRom(address),
        < 0x8000 => ReadRom(_romBank * 0x4000 + address - 0x4000),
        >= 0xA000 and < 0xC000 when _infraredMode =>
            (byte)(0xC0 | (infraredEndpoint?.Input == true ? 1 : 0)),
        >= 0xA000 and < 0xC000 => ReadRam(_ramBank * 0x2000 + address - 0xA000),
        _ => 0xFF,
    };

    public override void Write(ushort address, byte value)
    {
        switch (address)
        {
            case < 0x2000:
                _infraredMode = (value & 0x0F) == 0x0E;
                break;
            case < 0x4000:
                _romBank = value & 0x3F;
                break;
            case < 0x6000:
                _ramBank = value & 7;
                break;
            case >= 0xA000 and < 0xC000 when _infraredMode:
                var output = (value & 1) != 0;
                if (output != _infraredOutput)
                {
                    _infraredOutput = output;
                    infraredEndpoint?.SetOutput(output);
                }
                break;
            case >= 0xA000 and < 0xC000:
                WriteRam(_ramBank * 0x2000 + address - 0xA000, value);
                break;
        }
    }

    public override void WriteStateHash(BinaryWriter writer)
    {
        base.WriteStateHash(writer);
        writer.Write(_romBank);
        writer.Write(_ramBank);
        writer.Write(_infraredMode);
        writer.Write(_infraredOutput);
        writer.Write(infraredEndpoint?.Input == true);
    }
}

internal sealed class Mmm01Cartridge : Cartridge
{
    private bool _ramEnabled;
    private int _romBankLow;
    private int _romBankMid;
    private int _romBankMask;
    private int _romBankHigh;
    private int _ramBankLow;
    private int _ramBankHigh;
    private int _ramBankMask = 3;
    private bool _mbc1Mode;
    private bool _locked;
    private bool _mbc1ModeDisabled;
    private bool _multiplexMode;

    public Mmm01Cartridge(byte[] rom, int ramSize) : base(RotateStartupBanks(rom), ramSize) { }

    public override byte Read(ushort address) => address switch
    {
        < 0x4000 => ReadRom(Rom0Bank() * 0x4000 + address),
        < 0x8000 => ReadRom(RomBank() * 0x4000 + address - 0x4000),
        >= 0xA000 and < 0xC000 when _ramEnabled =>
            ReadRam(RamBank() * 0x2000 + address - 0xA000),
        _ => 0xFF,
    };

    public override void Write(ushort address, byte value)
    {
        switch (address)
        {
            case < 0x2000:
                _ramEnabled = (value & 0x0F) == 0x0A;
                if (!_locked)
                {
                    _ramBankMask = (value >> 4) & 3;
                    _locked = (value & 0x40) != 0;
                }
                break;
            case < 0x4000:
                if (!_locked) _romBankMid = (value >> 5) & 3;
                var mask = (_romBankMask << 1) & 0x1F;
                _romBankLow = ((_romBankLow & mask) | (value & ~mask)) & 0x1F;
                break;
            case < 0x6000:
                _ramBankLow = (value | ~_ramBankMask) & 3;
                if (!_locked)
                {
                    _ramBankHigh = (value >> 2) & 3;
                    _romBankHigh = (value >> 4) & 3;
                    _mbc1ModeDisabled = (value & 0x40) != 0;
                }
                break;
            case < 0x8000:
                if (!_mbc1ModeDisabled) _mbc1Mode = (value & 1) != 0;
                if (!_locked)
                {
                    _romBankMask = (value >> 2) & 0x0F;
                    _multiplexMode = (value & 0x40) != 0;
                }
                break;
            case >= 0xA000 and < 0xC000 when _ramEnabled:
                WriteRam(RamBank() * 0x2000 + address - 0xA000, value);
                break;
        }
    }

    public override void WriteStateHash(BinaryWriter writer)
    {
        base.WriteStateHash(writer);
        writer.Write(_ramEnabled);
        writer.Write(_romBankLow);
        writer.Write(_romBankMid);
        writer.Write(_romBankMask);
        writer.Write(_romBankHigh);
        writer.Write(_ramBankLow);
        writer.Write(_ramBankHigh);
        writer.Write(_ramBankMask);
        writer.Write(_mbc1Mode);
        writer.Write(_locked);
        writer.Write(_mbc1ModeDisabled);
        writer.Write(_multiplexMode);
    }

    private int Rom0Bank()
    {
        if (!_locked) return Rom.Length / 0x4000 - 2;
        if (_multiplexMode)
            return (_romBankLow & (_romBankMask << 1)) |
                ((_mbc1Mode ? 0 : _ramBankLow) << 5) | (_romBankHigh << 7);
        return (_romBankLow & (_romBankMask << 1)) |
            (_romBankMid << 5) | (_romBankHigh << 7);
    }

    private int RomBank()
    {
        if (!_locked) return Rom.Length / 0x4000 - 1;
        var bank = _romBankLow |
            ((_multiplexMode ? _ramBankLow : _romBankMid) << 5) |
            (_romBankHigh << 7);
        return bank == Rom0Bank() ? bank + 1 : bank;
    }

    private int RamBank() => _multiplexMode
        ? _romBankMid | (_ramBankHigh << 2)
        : _ramBankLow | (_ramBankHigh << 2);

    private static byte[] RotateStartupBanks(byte[] rom)
    {
        if (rom.Length <= 0x8000) return rom;
        var rotated = new byte[rom.Length];
        rom.AsSpan(0x8000).CopyTo(rotated);
        rom.AsSpan(0, 0x8000).CopyTo(rotated.AsSpan(rom.Length - 0x8000));
        return rotated;
    }
}

internal sealed class Mbc3Cartridge(byte[] rom, int ramSize, ITimeProvider timeProvider) : Cartridge(rom, ramSize)
{
    private const int CompactRtcLength = 5;
    private const int SameBoyRtcLength = 48;
    private const int SameBoyRtc32Length = 44;
    private readonly ITimeProvider _timeProvider = timeProvider;
    private readonly byte[] _rtc = new byte[5];
    private readonly byte[] _latchedRtc = new byte[5];
    private DateTimeOffset _lastRtcUpdate = timeProvider.UtcNow;
    private bool _ramEnabled;
    private int _romBank = 1;
    private byte _select;
    private byte _latchValue;

    public override byte Read(ushort address) => address switch
    {
        < 0x4000 => ReadRom(address),
        < 0x8000 => ReadRom(_romBank * 0x4000 + address - 0x4000),
        >= 0xA000 and < 0xC000 when _ramEnabled && _select <= 0x03 => ReadRam(_select * 0x2000 + address - 0xA000),
        >= 0xA000 and < 0xC000 when _ramEnabled && _select is >= 0x08 and <= 0x0C => _latchedRtc[_select - 0x08],
        _ => 0xFF,
    };

    public override void Write(ushort address, byte value)
    {
        switch (address)
        {
            case < 0x2000:
                _ramEnabled = (value & 0x0F) == 0x0A;
                break;
            case < 0x4000:
                _romBank = (value & 0x7F) is 0 ? 1 : value & 0x7F;
                break;
            case < 0x6000:
                _select = value;
                break;
            case < 0x8000:
                if (_latchValue == 0 && value == 1)
                {
                    UpdateClock();
                    _rtc.CopyTo(_latchedRtc, 0);
                }
                _latchValue = value;
                break;
            case >= 0xA000 and < 0xC000 when _ramEnabled && _select <= 0x03:
                WriteRam(_select * 0x2000 + address - 0xA000, value);
                break;
            case >= 0xA000 and < 0xC000 when _ramEnabled && _select is >= 0x08 and <= 0x0C:
                UpdateClock();
                _rtc[_select - 0x08] = value;
                Dirty();
                break;
        }
    }

    public override byte[] SaveBattery()
    {
        UpdateClock();
        var result = new byte[Ram.Length + SameBoyRtcLength];
        Ram.CopyTo(result, 0);
        WriteBatteryRtc(result.AsSpan(Ram.Length));
        return result;
    }

    public override void LoadBattery(ReadOnlySpan<byte> data)
    {
        var rtcLength = data.Length - Ram.Length;
        if (data.Length < Ram.Length || rtcLength is not (0 or CompactRtcLength or SameBoyRtc32Length or SameBoyRtcLength))
            throw new ArgumentException($"MBC3 battery data must be {Ram.Length}, {Ram.Length + CompactRtcLength}, {Ram.Length + SameBoyRtc32Length}, or {Ram.Length + SameBoyRtcLength} bytes.", nameof(data));
        data[..Ram.Length].CopyTo(Ram);
        if (rtcLength == CompactRtcLength)
        {
            data[Ram.Length..].CopyTo(_rtc);
            _rtc.CopyTo(_latchedRtc, 0);
            _lastRtcUpdate = _timeProvider.UtcNow;
        }
        else if (rtcLength is SameBoyRtc32Length or SameBoyRtcLength)
        {
            var rtc = data[Ram.Length..];
            ReadPaddedRtc(rtc, _rtc);
            ReadPaddedRtc(rtc[20..], _latchedRtc);
            var timestamp = rtcLength == SameBoyRtcLength
                ? BinaryPrimitives.ReadUInt64LittleEndian(rtc[40..])
                : BinaryPrimitives.ReadUInt32LittleEndian(rtc[40..]);
            try
            {
                var savedTime = DateTimeOffset.FromUnixTimeSeconds(checked((long)timestamp));
                _lastRtcUpdate = savedTime > _timeProvider.UtcNow ? _timeProvider.UtcNow : savedTime;
            }
            catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
            {
                throw new ArgumentException("MBC3 battery RTC timestamp is invalid.", nameof(data), exception);
            }
        }
        else
        {
            Array.Clear(_rtc);
            Array.Clear(_latchedRtc);
            _lastRtcUpdate = _timeProvider.UtcNow;
        }
    }

    public override BessRtc? SaveBessRtc()
    {
        UpdateClock();
        return ToBessRtc();
    }

    public override void ValidateBessRtc(BessRtc state)
    {
        if (state.Seconds > 59 || state.Minutes > 59 || state.Hours > 23 ||
            state.LatchedSeconds > 59 || state.LatchedMinutes > 59 || state.LatchedHours > 23 ||
            (state.High & 0x3E) != 0 || (state.LatchedHigh & 0x3E) != 0)
            throw new InvalidDataException("BESS MBC3 RTC fields are invalid.");

        try
        {
            _ = DateTimeOffset.FromUnixTimeSeconds(checked((long)state.LastUnixSecond));
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
        {
            throw new InvalidDataException("BESS MBC3 RTC timestamp is invalid.", exception);
        }
    }

    public override void LoadBessRtc(BessRtc state)
    {
        ValidateBessRtc(state);
        _rtc[0] = state.Seconds;
        _rtc[1] = state.Minutes;
        _rtc[2] = state.Hours;
        _rtc[3] = state.Days;
        _rtc[4] = state.High;
        _latchedRtc[0] = state.LatchedSeconds;
        _latchedRtc[1] = state.LatchedMinutes;
        _latchedRtc[2] = state.LatchedHours;
        _latchedRtc[3] = state.LatchedDays;
        _latchedRtc[4] = state.LatchedHigh;
        _lastRtcUpdate = DateTimeOffset.FromUnixTimeSeconds((long)state.LastUnixSecond);
    }

    private BessRtc ToBessRtc() => new(
        _rtc[0], _rtc[1], _rtc[2], _rtc[3], _rtc[4],
        _latchedRtc[0], _latchedRtc[1], _latchedRtc[2], _latchedRtc[3], _latchedRtc[4],
        checked((ulong)_lastRtcUpdate.ToUnixTimeSeconds()));

    private void WriteBatteryRtc(Span<byte> destination)
    {
        WritePaddedRtc(destination, _rtc);
        WritePaddedRtc(destination[20..], _latchedRtc);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[40..], checked((ulong)_lastRtcUpdate.ToUnixTimeSeconds()));
    }

    private static void WritePaddedRtc(Span<byte> destination, byte[] rtc)
    {
        destination[0] = rtc[0];
        destination[4] = rtc[1];
        destination[8] = rtc[2];
        destination[12] = rtc[3];
        destination[16] = rtc[4];
    }

    private static void ReadPaddedRtc(ReadOnlySpan<byte> source, byte[] rtc)
    {
        rtc[0] = source[0];
        rtc[1] = source[4];
        rtc[2] = source[8];
        rtc[3] = source[12];
        rtc[4] = source[16];
    }

    private void UpdateClock()
    {
        var elapsed = _timeProvider.UtcNow - _lastRtcUpdate;
        _lastRtcUpdate = _timeProvider.UtcNow;
        if ((_rtc[4] & 0x40) != 0 || elapsed <= TimeSpan.Zero) return;
        var total = _rtc[0] + 60L * _rtc[1] + 3600L * _rtc[2] + 86400L * ((_rtc[4] & 1) << 8 | _rtc[3]);
        total += (long)elapsed.TotalSeconds;
        if (total >= 512L * 86400) _rtc[4] |= 0x80;
        total %= 512L * 86400;
        _rtc[0] = (byte)(total % 60); total /= 60;
        _rtc[1] = (byte)(total % 60); total /= 60;
        _rtc[2] = (byte)(total % 24); total /= 24;
        _rtc[3] = (byte)total;
        _rtc[4] = (byte)((_rtc[4] & 0xC0) | (total >= 256 ? 1 : 0));
    }

    public override void WriteStateHash(BinaryWriter writer)
    {
        base.WriteStateHash(writer);
        writer.Write(_rtc);
        writer.Write(_latchedRtc);
        writer.Write(_ramEnabled);
        writer.Write(_romBank);
        writer.Write(_select);
        writer.Write(_latchValue);
    }
}

internal sealed class RomOnlyCartridge(byte[] rom, int ramSize) : Cartridge(rom, ramSize)
{
    public override byte Read(ushort address) => address switch
    {
        < 0x8000 => ReadRom(address),
        >= 0xA000 and < 0xC000 => ReadRam(address - 0xA000),
        _ => 0xFF,
    };
    public override void Write(ushort address, byte value)
    {
        if (address is >= 0xA000 and < 0xC000) WriteRam(address - 0xA000, value);
    }
}

internal sealed class Mbc1Cartridge : Cartridge
{
    private readonly bool _multicart;
    private bool _ramEnabled;
    private int _lowBank = 1, _highBank;
    private bool _ramMode;

    public Mbc1Cartridge(byte[] rom, int ramSize, bool multicart) : base(rom, ramSize) => _multicart = multicart;

    public override byte Read(ushort address) => address switch
    {
        < 0x4000 => ReadRom(((_ramMode ? _highBank << (_multicart ? 4 : 5) : 0) * 0x4000) + address),
        < 0x8000 => ReadRom((RomBank() * 0x4000) + address - 0x4000),
        >= 0xA000 and < 0xC000 when _ramEnabled =>
            ReadRam(((!_multicart && _ramMode ? _highBank : 0) * 0x2000) + address - 0xA000),
        _ => 0xFF,
    };

    public override void Write(ushort address, byte value)
    {
        switch (address)
        {
            case < 0x2000: _ramEnabled = (value & 0x0F) == 0x0A; break;
            case < 0x4000: _lowBank = _multicart ? value : (value & 0x1F) is 0 ? 1 : value & 0x1F; break;
            case < 0x6000: _highBank = value & 3; break;
            case < 0x8000: _ramMode = (value & 1) != 0; break;
            case >= 0xA000 and < 0xC000 when _ramEnabled:
                WriteRam(((_ramMode ? _highBank : 0) * 0x2000) + address - 0xA000, value);
                break;
        }
    }

    public override void WriteStateHash(BinaryWriter writer)
    {
        base.WriteStateHash(writer);
        writer.Write(_ramEnabled);
        writer.Write(_lowBank);
        writer.Write(_highBank);
        writer.Write(_ramMode);
        writer.Write(_multicart);
    }

    private int RomBank()
    {
        if (!_multicart) return (_highBank << 5) | _lowBank;
        var bank = (_lowBank & 0x0F) | (_highBank << 4);
        return (_lowBank & 0x1F) == 0 ? bank + 1 : bank;
    }
}

internal sealed class Mbc2Cartridge(byte[] rom) : Cartridge(rom, 512)
{
    private bool _ramEnabled;
    private int _bank = 1;
    public override byte Read(ushort address) => address switch
    {
        < 0x4000 => ReadRom(address),
        < 0x8000 => ReadRom(_bank * 0x4000 + address - 0x4000),
        >= 0xA000 and < 0xC000 when _ramEnabled => (byte)(0xF0 | ReadRam(address & 0x1FF)),
        _ => 0xFF,
    };
    public override void Write(ushort address, byte value)
    {
        if (address < 0x4000)
        {
            if ((address & 0x100) == 0) _ramEnabled = (value & 0x0F) == 0x0A;
            else _bank = (value & 0x0F) is 0 ? 1 : value & 0x0F;
        }
        else if (address is >= 0xA000 and < 0xC000 && _ramEnabled)
            WriteRam(address & 0x1FF, (byte)(value & 0x0F));
    }

    public override void WriteStateHash(BinaryWriter writer)
    {
        base.WriteStateHash(writer);
        writer.Write(_ramEnabled);
        writer.Write(_bank);
    }
}

internal sealed class Mbc5Cartridge(byte[] rom, int ramSize) : Cartridge(rom, ramSize)
{
    private bool _ramEnabled;
    private int _romBank = 1, _ramBank;
    public override byte Read(ushort address) => address switch
    {
        < 0x4000 => ReadRom(address),
        < 0x8000 => ReadRom(_romBank * 0x4000 + address - 0x4000),
        >= 0xA000 and < 0xC000 when _ramEnabled => ReadRam(_ramBank * 0x2000 + address - 0xA000),
        _ => 0xFF,
    };
    public override void Write(ushort address, byte value)
    {
        switch (address)
        {
            case < 0x2000: _ramEnabled = (value & 0x0F) == 0x0A; break;
            case < 0x3000: _romBank = (_romBank & 0x100) | value; break;
            case < 0x4000: _romBank = (_romBank & 0xFF) | ((value & 1) << 8); break;
            case < 0x6000: _ramBank = value & 0x0F; break;
            case >= 0xA000 and < 0xC000 when _ramEnabled:
                WriteRam(_ramBank * 0x2000 + address - 0xA000, value); break;
        }
    }

    public override void WriteStateHash(BinaryWriter writer)
    {
        base.WriteStateHash(writer);
        writer.Write(_ramEnabled);
        writer.Write(_romBank);
        writer.Write(_ramBank);
    }
}
