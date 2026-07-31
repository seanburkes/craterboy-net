namespace Craterboy;

internal sealed class PpuDevice : ICycleParticipant
{
    public const int Width = 160;
    public const int Height = 144;

    private readonly byte[] _io;
    private readonly byte[] _frame = new byte[Width * Height];
    private bool _enabled;
    private int _lineCycles;
    private byte _line;
    private int _mode;
    private bool _coincidence;

    public PpuDevice(byte[] io) => _io = io;

    public void Reset()
    {
        _enabled = false;
        _lineCycles = 0;
        _line = 0;
        _mode = 0;
        _coincidence = false;
        Array.Clear(_frame);
        _io[0x40] = 0;
        _io[0x41] = 0x80;
        _io[0x44] = 0;
        _io[0x45] = 0;
    }

    public byte Read(ushort address) => address switch
    {
        0xFF40 => _io[0x40],
        0xFF41 => (byte)(0x80 | (_io[0x41] & 0x78) | (_coincidence ? 0x04 : 0) | _mode),
        0xFF44 => _line,
        0xFF45 => _io[0x45],
        _ => 0xFF,
    };

    public void Write(ushort address, byte value)
    {
        switch (address)
        {
            case 0xFF40:
                WriteLcdc(value);
                break;
            case 0xFF41:
                _io[0x41] = (byte)(0x80 | (value & 0x78));
                UpdateCoincidence();
                break;
            case 0xFF44:
                _line = 0;
                _lineCycles = 0;
                UpdateCoincidence();
                break;
            case 0xFF45:
                _io[0x45] = value;
                UpdateCoincidence();
                break;
        }
    }

    public void CopyFrame(Span<byte> destination)
    {
        if (destination.Length < _frame.Length)
            throw new ArgumentException($"Frame destination must be at least {_frame.Length} bytes.", nameof(destination));
        _frame.AsSpan().CopyTo(destination);
    }

    public void AdvanceTCycle()
    {
        if (!_enabled) return;
        _lineCycles++;
        if (_line < 144)
        {
            if (_lineCycles == 80) SetMode(3);
            else if (_lineCycles == 252) SetMode(0);
        }
        if (_lineCycles < 456) return;

        _lineCycles = 0;
        _line++;
        if (_line == 144) SetMode(1);
        else if (_line >= 154)
        {
            _line = 0;
            SetMode(2);
        }
        else if (_line < 144) SetMode(2);
        _io[0x44] = _line;
        UpdateCoincidence();
    }

    private void WriteLcdc(byte value)
    {
        var wasEnabled = _enabled;
        _io[0x40] = value;
        _enabled = (value & 0x80) != 0;
        if (!wasEnabled && _enabled)
        {
            _line = 0;
            _lineCycles = 0;
            _io[0x44] = 0;
            SetMode(2);
            UpdateCoincidence();
        }
        else if (wasEnabled && !_enabled)
        {
            _line = 0;
            _lineCycles = 0;
            _io[0x44] = 0;
            SetMode(0);
            UpdateCoincidence();
        }
    }

    private void SetMode(int mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        var mask = mode switch { 0 => 0x08, 1 => 0x10, 2 => 0x20, _ => 0 };
        if ((_io[0x41] & mask) != 0) _io[0x0F] |= 0x02;
    }

    private void UpdateCoincidence()
    {
        var matching = _line == _io[0x45];
        if (matching && !_coincidence && (_io[0x41] & 0x40) != 0) _io[0x0F] |= 0x02;
        _coincidence = matching;
    }
}
