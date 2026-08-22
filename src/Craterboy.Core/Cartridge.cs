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
    protected void Dirty() => _batteryDirty = true;
    public virtual BessRtc? SaveBessRtc() => null;
    public virtual void ValidateBessRtc(BessRtc state) => throw new InvalidDataException("BESS RTC state is not supported by this cartridge.");
    public virtual void LoadBessRtc(BessRtc state) => throw new InvalidDataException("BESS RTC state is not supported by this cartridge.");
    public virtual BessHuc3? SaveBessHuc3() => null;
    public virtual void ValidateBessHuc3(BessHuc3 state) => throw new InvalidDataException("BESS HuC3 state is not supported by this cartridge.");
    public virtual void LoadBessHuc3(BessHuc3 state) => throw new InvalidDataException("BESS HuC3 state is not supported by this cartridge.");
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
        byte[] rom, RomHeader header, ITimeProvider timeProvider, IInfraredEndpoint? infraredEndpoint) =>
        header.CartridgeType switch
    {
        0x00 or 0x08 or 0x09 => new RomOnlyCartridge(rom, header.RamSize),
        0x01 or 0x02 or 0x03 => new Mbc1Cartridge(rom, header.RamSize, IsMbc1Multicart(rom)),
        0x05 or 0x06 => new Mbc2Cartridge(rom),
        0x0B or 0x0C or 0x0D => new Mmm01Cartridge(rom, header.RamSize),
        0x0F or 0x10 or 0x11 or 0x12 or 0x13 => new Mbc3Cartridge(rom, header.RamSize, timeProvider),
        0x19 or 0x1A or 0x1B or 0x1C or 0x1D or 0x1E => new Mbc5Cartridge(rom, header.RamSize),
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
