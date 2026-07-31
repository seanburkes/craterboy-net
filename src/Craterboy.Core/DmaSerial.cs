namespace Craterboy;

internal sealed class OamDmaDevice : ICycleParticipant
{
    private readonly Func<ushort, byte> _read;
    private readonly Action<int, byte> _writeOam;
    private int _index = -1;
    private int _phase;
    private ushort _source;

    public OamDmaDevice(Func<ushort, byte> read, Action<int, byte> writeOam)
    {
        _read = read;
        _writeOam = writeOam;
    }

    public bool Active => _index >= 0;

    public void Reset()
    {
        _index = -1;
        _phase = 0;
        _source = 0;
    }

    public void Start(byte page)
    {
        _source = (ushort)(page << 8);
        _index = 0;
        _phase = 0;
    }

    public void AdvanceTCycle()
    {
        if (_index < 0) return;
        if (++_phase < 4) return;
        _phase = 0;
        _writeOam(_index, _read((ushort)(_source + _index)));
        if (++_index == 0xA0) _index = -1;
    }
}

internal sealed class SerialDevice : ICycleParticipant
{
    private readonly byte[] _io;
    private readonly ISerialEndpoint? _endpoint;
    private int _cycles;

    public SerialDevice(byte[] io, ISerialEndpoint? endpoint)
    {
        _io = io;
        _endpoint = endpoint;
    }

    public void Reset() => _cycles = 0;

    public void WriteControl(byte value)
    {
        _io[0x02] = (byte)(value & 0x83);
        if ((_io[0x02] & 0x81) == 0x81) _cycles = 0;
    }

    public void AdvanceTCycle()
    {
        if ((_io[0x02] & 0x81) != 0x81) return;
        if (++_cycles < 512 * 8) return;
        _cycles = 0;
        _io[0x01] = _endpoint?.Exchange(_io[0x01]) ?? 0xFF;
        _io[0x02] &= 0x7F;
        _io[0x0F] |= 0x08;
    }
}
