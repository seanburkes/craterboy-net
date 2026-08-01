namespace Craterboy;

internal sealed class ApuDevice : ICycleParticipant
{
    private readonly byte[] _io;
    private bool _powered;
    private int _frameCycles;
    private int _channel1Length;
    private bool _channel1Enabled;

    public ApuDevice(byte[] io) => _io = io;

    public void Reset()
    {
        _powered = false;
        _frameCycles = 0;
        _channel1Length = 0;
        _channel1Enabled = false;
        Array.Clear(_io, 0x10, 0x16);
        _io[0x26] = 0;
    }

    public byte Read(ushort address) => address == 0xFF26
        ? (byte)((_powered ? 0x80 : 0) | (_io[0x26] & 0x0F))
        : _io[address - 0xFF00];

    public void Write(ushort address, byte value)
    {
        if (address == 0xFF26)
        {
            var powered = (value & 0x80) != 0;
            if (!powered && _powered)
            {
                Array.Clear(_io, 0x10, 0x16);
                _channel1Length = 0;
                _channel1Enabled = false;
            }
            _powered = powered;
            _io[0x26] = (byte)(powered ? 0x80 : 0);
            return;
        }

        if (!_powered || address is < 0xFF10 or > 0xFF25) return;
        _io[address - 0xFF00] = value;
        switch (address)
        {
            case 0xFF11:
                _channel1Length = 64 - (value & 0x3F);
                break;
            case 0xFF14 when (value & 0x80) != 0:
                if (_channel1Length == 0) _channel1Length = 64;
                _channel1Enabled = (_io[0x12] & 0xF8) != 0;
                UpdateStatus();
                break;
        }
    }

    public void AdvanceTCycle()
    {
        if (!_powered) return;
        if (++_frameCycles < 8192) return;
        _frameCycles = 0;
        if ((_io[0x14] & 0x40) != 0 && _channel1Enabled && _channel1Length > 0 && --_channel1Length == 0)
        {
            _channel1Enabled = false;
            UpdateStatus();
        }
    }

    private void UpdateStatus() => _io[0x26] = (byte)(0x80 | (_channel1Enabled ? 0x01 : 0));
}
