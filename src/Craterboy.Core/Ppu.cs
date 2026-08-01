namespace Craterboy;

internal sealed class PpuDevice : ICycleParticipant
{
    public const int Width = 160;
    public const int Height = 144;

    private readonly byte[] _io;
    private readonly byte[] _vram;
    private readonly byte[] _oam;
    private readonly byte[] _frame = new byte[Width * Height];
    private readonly byte[] _backgroundColors = new byte[Width];
    private bool _enabled;
    private int _lineCycles;
    private byte _line;
    private int _windowLine;
    private int _mode;
    private bool _coincidence;
    private bool _statLine;

    private readonly record struct SpriteCandidate(int OamIndex, int X, int Row, byte Tile, byte Attributes);

    public PpuDevice(byte[] io, byte[] vram, byte[] oam)
    {
        _io = io;
        _vram = vram;
        _oam = oam;
    }

    public bool CpuCanAccessVram => !_enabled || _mode != 3;

    public bool CpuCanAccessOam => !_enabled || (_mode != 2 && _mode != 3);

    public void Reset()
    {
        _enabled = false;
        _lineCycles = 0;
        _line = 0;
        _windowLine = 0;
        _mode = 0;
        _coincidence = false;
        _statLine = false;
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
            else if (_lineCycles == 252)
            {
                RenderBackgroundLine(_line);
                SetMode(0);
            }
        }
        if (_lineCycles < 456) return;

        _lineCycles = 0;
        _line++;
        if (_line == 144) SetMode(1);
        else if (_line >= 154)
        {
            _line = 0;
            _windowLine = 0;
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
            _windowLine = 0;
            _lineCycles = 0;
            _io[0x44] = 0;
            SetMode(2);
            UpdateCoincidence();
        }
        else if (wasEnabled && !_enabled)
        {
            _line = 0;
            _lineCycles = 0;
            _windowLine = 0;
            Array.Clear(_frame);
            _io[0x44] = 0;
            SetMode(0);
            UpdateCoincidence();
        }
    }

    private void SetMode(int mode)
    {
        if (_mode == mode)
        {
            UpdateStatLine();
            return;
        }
        _mode = mode;
        UpdateStatLine();
    }

    private void UpdateCoincidence()
    {
        var matching = _line == _io[0x45];
        _coincidence = matching;
        UpdateStatLine();
    }

    private void UpdateStatLine()
    {
        var modeMask = _mode switch { 0 => 0x08, 1 => 0x10, 2 => 0x20, _ => 0 };
        var active = (_coincidence && (_io[0x41] & 0x40) != 0) ||
                     (modeMask != 0 && (_io[0x41] & modeMask) != 0);
        if (active && !_statLine) _io[0x0F] |= 0x02;
        _statLine = active;
    }

    private void RenderBackgroundLine(byte line)
    {
        if ((_io[0x40] & 0x01) == 0)
        {
            Array.Clear(_backgroundColors);
            RenderSprites(line, line * Width);
            return;
        }
        var mapBase = (_io[0x40] & 0x08) != 0 ? 0x1C00 : 0x1800;
        var unsignedTiles = (_io[0x40] & 0x10) != 0;
        var worldY = (line + _io[0x42]) & 0xFF;
        var windowStart = _io[0x4B] - 7;
        var windowActive = (_io[0x40] & 0x20) != 0 && line >= _io[0x4A] && _io[0x4A] < 144 && windowStart < Width;
        var windowUsed = false;
        var tileRow = worldY >> 3;
        var row = worldY & 7;
        var output = line * Width;
        for (var x = 0; x < Width; x++)
        {
            var useWindow = windowActive && x >= windowStart;
            var worldX = useWindow ? (x - windowStart) & 0xFF : (x + _io[0x43]) & 0xFF;
            var pixelY = useWindow ? _windowLine : worldY;
            var tileColumn = worldX >> 3;
            var selectedMap = useWindow && (_io[0x40] & 0x40) != 0 ? 0x1C00 : mapBase;
            if (useWindow && (_io[0x40] & 0x40) == 0) selectedMap = 0x1800;
            var selectedTileRow = pixelY >> 3;
            var tile = _vram[selectedMap + selectedTileRow * 32 + tileColumn];
            var tileAddress = unsignedTiles
                ? tile * 16
                : 0x1000 + (sbyte)tile * 16;
            var selectedRow = pixelY & 7;
            var low = _vram[tileAddress + selectedRow * 2];
            var high = _vram[tileAddress + selectedRow * 2 + 1];
            var bit = 7 - (worldX & 7);
            var color = ((high >> bit) & 1) << 1 | ((low >> bit) & 1);
            _backgroundColors[x] = (byte)color;
            _frame[output + x] = (byte)((_io[0x47] >> (color * 2)) & 0x03);
            windowUsed |= useWindow;
        }
        if (windowUsed) _windowLine++;
        RenderSprites(line, output);
    }

    private void RenderSprites(byte line, int output)
    {
        if ((_io[0x40] & 0x02) == 0) return;
        var written = new bool[Width];
        var height = (_io[0x40] & 0x04) != 0 ? 16 : 8;
        var candidates = new SpriteCandidate[10];
        var selected = 0;
        for (var sprite = 0; sprite < 40 && selected < candidates.Length; sprite++)
        {
            var oam = sprite * 4;
            var y = _oam[oam] - 16;
            var x = _oam[oam + 1] - 8;
            var tile = _oam[oam + 2];
            var attributes = _oam[oam + 3];
            var row = line - y;
            if (row < 0 || row >= height) continue;
            if ((attributes & 0x40) != 0) row = height - 1 - row;
            candidates[selected++] = new SpriteCandidate(sprite, x, row, tile, attributes);
        }

        // DMG resolves overlapping sprites by lower screen X, then OAM order.
        for (var i = 1; i < selected; i++)
        {
            var candidate = candidates[i];
            var j = i - 1;
            while (j >= 0 && (candidates[j].X > candidate.X ||
                              candidates[j].X == candidate.X && candidates[j].OamIndex > candidate.OamIndex))
            {
                candidates[j + 1] = candidates[j--];
            }
            candidates[j + 1] = candidate;
        }

        for (var index = 0; index < selected; index++)
        {
            var candidate = candidates[index];
            var x = candidate.X;
            var row = candidate.Row;
            var tile = candidate.Tile;
            var attributes = candidate.Attributes;
            if (height == 16)
            {
                tile &= 0xFE;
                if (row >= 8)
                {
                    tile++;
                    row -= 8;
                }
            }
            var tileAddress = tile * 16 + row * 2;
            var low = _vram[tileAddress];
            var high = _vram[tileAddress + 1];
            var palette = (attributes & 0x10) != 0 ? _io[0x49] : _io[0x48];
            for (var pixel = 0; pixel < 8; pixel++)
            {
                var screenX = x + pixel;
                if (screenX < 0 || screenX >= Width || written[screenX]) continue;
                var bit = (attributes & 0x20) != 0 ? pixel : 7 - pixel;
                var color = ((high >> bit) & 1) << 1 | ((low >> bit) & 1);
                if (color == 0) continue;
                if ((attributes & 0x80) != 0 && _backgroundColors[screenX] != 0) continue;
                _frame[output + screenX] = (byte)((palette >> (color * 2)) & 0x03);
                written[screenX] = true;
            }
        }
    }
}
