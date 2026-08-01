namespace Craterboy;

internal sealed class ApuDevice : ICycleParticipant
{
    private readonly byte[] _io;
    private bool _powered;

    public ApuDevice(byte[] io) => _io = io;

    public void Reset()
    {
        _powered = false;
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
            if (!powered && _powered) Array.Clear(_io, 0x10, 0x16);
            _powered = powered;
            _io[0x26] = (byte)(powered ? 0x80 : 0);
            return;
        }

        if (_powered && address is >= 0xFF10 and <= 0xFF25)
            _io[address - 0xFF00] = value;
    }

    public void AdvanceTCycle()
    {
        // Channel sequencers and sample generation are added in later APU slices.
    }
}
