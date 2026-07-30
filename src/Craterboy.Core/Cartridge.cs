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

    public void LoadBattery(ReadOnlySpan<byte> data)
    {
        if (data.Length != Ram.Length)
            throw new ArgumentException($"Battery data must be exactly {Ram.Length} bytes.", nameof(data));
        data.CopyTo(Ram);
        _batteryDirty = false;
    }

    public byte[] SaveBattery()
    {
        _batteryDirty = false;
        return (byte[])Ram.Clone();
    }

    public static Cartridge Create(byte[] rom, RomHeader header) => header.CartridgeType switch
    {
        0x00 or 0x08 or 0x09 => new RomOnlyCartridge(rom, header.RamSize),
        0x01 or 0x02 or 0x03 => new Mbc1Cartridge(rom, header.RamSize),
        0x05 or 0x06 => new Mbc2Cartridge(rom),
        0x19 or 0x1A or 0x1B or 0x1C or 0x1D or 0x1E => new Mbc5Cartridge(rom, header.RamSize),
        _ => throw new NotSupportedException($"Cartridge type 0x{header.CartridgeType:X2} is not implemented."),
    };

    protected byte ReadRom(int index) => Rom[index % Rom.Length];
    protected byte ReadRam(int index) => Ram.Length == 0 ? (byte)0xFF : Ram[index % Ram.Length];
    protected void WriteRam(int index, byte value)
    {
        if (Ram.Length == 0) return;
        Ram[index % Ram.Length] = value;
        Dirty();
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

internal sealed class Mbc1Cartridge(byte[] rom, int ramSize) : Cartridge(rom, ramSize)
{
    private bool _ramEnabled;
    private int _lowBank = 1, _highBank;
    private bool _ramMode;

    public override byte Read(ushort address) => address switch
    {
        < 0x4000 => ReadRom(((_ramMode ? _highBank << 5 : 0) * 0x4000) + address),
        < 0x8000 => ReadRom((((_highBank << 5) | _lowBank) * 0x4000) + address - 0x4000),
        >= 0xA000 and < 0xC000 when _ramEnabled =>
            ReadRam(((_ramMode ? _highBank : 0) * 0x2000) + address - 0xA000),
        _ => 0xFF,
    };

    public override void Write(ushort address, byte value)
    {
        switch (address)
        {
            case < 0x2000: _ramEnabled = (value & 0x0F) == 0x0A; break;
            case < 0x4000: _lowBank = (value & 0x1F) is 0 ? 1 : value & 0x1F; break;
            case < 0x6000: _highBank = value & 3; break;
            case < 0x8000: _ramMode = (value & 1) != 0; break;
            case >= 0xA000 and < 0xC000 when _ramEnabled:
                WriteRam(((_ramMode ? _highBank : 0) * 0x2000) + address - 0xA000, value);
                break;
        }
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
}
