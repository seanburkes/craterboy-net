namespace Craterboy;

internal sealed class PpuDevice : ICycleParticipant
{
    public const int Width = 160;
    public const int Height = 144;

    private readonly GameBoyModel _model;
    private readonly byte[] _io;
    private readonly byte[] _vram;
    private readonly byte[] _oam;
    private readonly byte[] _frame = new byte[Width * Height];
    private readonly ushort[] _colorFrame = new ushort[Width * Height];
    private readonly byte[] _backgroundColors = new byte[Width];
    private readonly bool[] _backgroundPriority = new bool[Width];
    private readonly byte[] _backgroundPaletteRam = new byte[0x40];
    private readonly byte[] _objectPaletteRam = new byte[0x40];
    private readonly Action? _hblankStarted;
    private readonly Action? _lcdDisabled;
    private bool _enabled;
    private int _lineCycles;
    private byte _line;
    private int _windowLine;
    private int _mode;
    private bool _coincidence;
    private bool _statLine;
    private bool _paletteAccessBlocked;
    private int _oamCorruptionRow;

    private readonly record struct SpriteCandidate(int OamIndex, int X, int Row, byte Tile, byte Attributes);

    public PpuDevice(GameBoyModel model, byte[] io, byte[] vram, byte[] oam,
        Action? hblankStarted = null, Action? lcdDisabled = null)
    {
        _model = model;
        _io = io;
        _vram = vram;
        _oam = oam;
        _hblankStarted = hblankStarted;
        _lcdDisabled = lcdDisabled;
    }

    public bool CpuCanAccessVram => !_enabled || _mode != 3;

    public bool CpuCanAccessOam => !_enabled || (_mode != 2 && _mode != 3);

    public bool IsVisibleHblank => _enabled && _line < Height && _mode == 0;

    public void Reset()
    {
        _enabled = false;
        _lineCycles = 0;
        _line = 0;
        _windowLine = 0;
        _mode = 0;
        _coincidence = false;
        _statLine = false;
        _paletteAccessBlocked = false;
        _oamCorruptionRow = -1;
        Array.Clear(_frame);
        Array.Clear(_colorFrame);
        Array.Clear(_backgroundPaletteRam);
        Array.Clear(_objectPaletteRam);
        _io[0x40] = 0;
        _io[0x41] = 0x80;
        _io[0x44] = 0;
        _io[0x45] = 0;
    }

    public byte Read(ushort address) => address switch
    {
        0xFF40 => _io[0x40],
        0xFF41 => (byte)(0x80 | (_io[0x41] & 0x78) | (_coincidence ? 0x04 : 0) | _mode),
        0xFF42 or 0xFF43 or 0xFF47 or 0xFF48 or 0xFF49 => _io[address - 0xFF00],
        0xFF68 => ReadPaletteIndex(_io[0x68]),
        0xFF69 => ReadPaletteData(_backgroundPaletteRam, _io[0x68]),
        0xFF6A => ReadPaletteIndex(_io[0x6A]),
        0xFF6B => ReadPaletteData(_objectPaletteRam, _io[0x6A]),
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
            case 0xFF42 or 0xFF43 or 0xFF47 or 0xFF48 or 0xFF49:
                _io[address - 0xFF00] = value;
                break;
            case 0xFF68 when _model.IsColor():
                _io[0x68] = value;
                break;
            case 0xFF69 when _model.IsColor():
                WritePaletteData(_backgroundPaletteRam, 0x68, value);
                break;
            case 0xFF6A when _model.IsColor():
                _io[0x6A] = value;
                break;
            case 0xFF6B when _model.IsColor():
                WritePaletteData(_objectPaletteRam, 0x6A, value);
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

    public void CopyColorFrame(Span<ushort> destination)
    {
        if (destination.Length < _colorFrame.Length)
            throw new ArgumentException($"Color frame destination must be at least {_colorFrame.Length} pixels.", nameof(destination));
        _colorFrame.AsSpan().CopyTo(destination);
    }

    public void WriteStateHash(BinaryWriter writer)
    {
        writer.Write(_enabled);
        writer.Write(_lineCycles);
        writer.Write(_line);
        writer.Write(_windowLine);
        writer.Write(_mode);
        writer.Write(_coincidence);
        writer.Write(_statLine);
        writer.Write(_paletteAccessBlocked);
        writer.Write(_oamCorruptionRow);
        writer.Write(_backgroundPaletteRam);
        writer.Write(_objectPaletteRam);
    }

    public void AdvanceTCycle()
    {
        if (!_enabled) return;
        _lineCycles++;
        if (_line < 144)
        {
            if (_mode == 2 && (_lineCycles & 1) == 0 && _lineCycles is > 0 and < 80)
            {
                var searchIndex = (_lineCycles / 2) - 1;
                _oamCorruptionRow = Math.Min(0x98, ((searchIndex & ~1) * 4) + 8);
            }
            if (_lineCycles == 80) SetMode(3);
            else if (_lineCycles == 85 && _model.IsColor()) _paletteAccessBlocked = true;
            else if (_lineCycles == Mode3End())
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

    private int Mode3End()
    {
        var end = 252 + (_io[0x43] & 0x07);
        if (!_model.IsColor() && !_model.IsSuperGameBoy() && (_io[0x40] & 0x20) != 0 &&
            _io[0x4B] == 0 && _io[0x43] != 0 && _line >= _io[0x4A] && _io[0x4A] < 144)
        {
            end++;
        }
        return end;
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
            _lcdDisabled?.Invoke();
            _line = 0;
            _lineCycles = 0;
            _windowLine = 0;
            Array.Clear(_frame);
            Array.Clear(_colorFrame);
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
        if (mode != 3) _paletteAccessBlocked = false;
        if (mode != 2) _oamCorruptionRow = -1;
        if (mode == 0 && _line < 144) _hblankStarted?.Invoke();
        UpdateStatLine();
    }

    public void CorruptOamOnCpuAccess(ushort address)
    {
        if (_model.IsColor() || _mode != 2 || address >= 0xFEA0 || _oamCorruptionRow < 8) return;

        var row = _oamCorruptionRow;
        if (row == 0x80)
        {
            for (var i = 0; i < 8; i++) _oam[i] = _oam[row + i];
            return;
        }
        var current = ReadOamWord(row);
        var previous = ReadOamWord(row - 8);
        var preceding = ReadOamWord(row - 4);
        var glitched = (ushort)(((current ^ preceding) & (previous ^ preceding)) ^ preceding);
        _oam[row] = (byte)glitched;
        _oam[row + 1] = (byte)(glitched >> 8);
        for (var i = 2; i < 8; i++) _oam[row + i] = _oam[row - 8 + i];
    }

    private ushort ReadOamWord(int offset) => (ushort)(_oam[offset] | (_oam[offset + 1] << 8));

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

    private byte ReadPaletteIndex(byte register) => _model.IsColor()
        ? (byte)(0x40 | (register & 0xBF))
        : (byte)0xFF;

    private byte ReadPaletteData(byte[] paletteRam, byte indexRegister)
    {
        if (!_model.IsColor()) return 0xFF;
        return _paletteAccessBlocked ? (byte)0xFF : paletteRam[indexRegister & 0x3F];
    }

    private void WritePaletteData(byte[] paletteRam, int indexOffset, byte value)
    {
        var indexRegister = _io[indexOffset];
        if (_paletteAccessBlocked)
        {
            if ((indexRegister & 0x80) != 0)
                _io[indexOffset] = (byte)(0x80 | ((indexRegister + 1) & 0x3F));
            return;
        }
        paletteRam[indexRegister & 0x3F] = value;
        if ((indexRegister & 0x80) != 0)
            _io[indexOffset] = (byte)(0x80 | ((indexRegister + 1) & 0x3F));
    }

    private void RenderBackgroundLine(byte line)
    {
        if ((_io[0x40] & 0x01) == 0)
        {
            Array.Clear(_backgroundColors);
            Array.Clear(_backgroundPriority);
            Array.Clear(_colorFrame, line * Width, Width);
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
            var mapAddress = selectedMap + selectedTileRow * 32 + tileColumn;
            var tile = _vram[mapAddress];
            var attributes = _model.IsColor() ? _vram[0x2000 + mapAddress] : (byte)0;
            var tileAddress = (_model.IsColor() && (attributes & 0x08) != 0 ? 0x2000 : 0) +
                (unsignedTiles ? tile * 16 : 0x1000 + (sbyte)tile * 16);
            var selectedRow = pixelY & 7;
            if ((attributes & 0x40) != 0) selectedRow = 7 - selectedRow;
            var low = _vram[tileAddress + selectedRow * 2];
            var high = _vram[tileAddress + selectedRow * 2 + 1];
            var bit = (attributes & 0x20) != 0 ? worldX & 7 : 7 - (worldX & 7);
            var color = ((high >> bit) & 1) << 1 | ((low >> bit) & 1);
            _backgroundColors[x] = (byte)color;
            _backgroundPriority[x] = _model.IsColor() && (attributes & 0x80) != 0;
            _frame[output + x] = (byte)((_io[0x47] >> (color * 2)) & 0x03);
            _colorFrame[output + x] = _model.IsColor()
                ? ReadColor(_backgroundPaletteRam, (attributes & 0x07) * 4 + color)
                : (ushort)_frame[output + x];
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

        // DMG and CGB X-priority mode resolve overlaps by lower screen X, then OAM order.
        if (!_model.IsColor() || (_io[0x6C] & 0x01) != 0)
        {
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
            var tileAddress = (_model.IsColor() && (attributes & 0x08) != 0 ? 0x2000 : 0) + tile * 16 + row * 2;
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
                if (_model.IsColor() && _backgroundColors[screenX] != 0 && _backgroundPriority[screenX]) continue;
                if ((attributes & 0x80) != 0 && _backgroundColors[screenX] != 0) continue;
                _frame[output + screenX] = (byte)((palette >> (color * 2)) & 0x03);
                _colorFrame[output + screenX] = _model.IsColor()
                    ? ReadColor(_objectPaletteRam, (attributes & 0x07) * 4 + color)
                    : (ushort)_frame[output + screenX];
                written[screenX] = true;
            }
        }
    }

    private static ushort ReadColor(byte[] paletteRam, int colorIndex)
    {
        var address = colorIndex * 2;
        return (ushort)(paletteRam[address] | (paletteRam[address + 1] << 8));
    }
}
