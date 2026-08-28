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
    private readonly SpriteCandidate[] _spriteCandidates = new SpriteCandidate[10];
    private readonly byte[] _fetchPenaltyByPixel = new byte[Width];
    private readonly byte[] _spritePenaltyByPixel = new byte[Width];
    private readonly byte[] _backgroundPaletteRam = new byte[0x40];
    private readonly byte[] _objectPaletteRam = new byte[0x40];
    private readonly Action? _hblankStarted;
    private readonly Action<bool>? _lcdDisabled;
    private readonly Func<bool> _isDoubleSpeed;
    private bool _enabled;
    private int _lineCycles;
    private byte _line;
    private int _windowLine;
    private int _mode;
    private bool _hblankBusBlocked;
    private bool _hblankOamReadBlocked;
    private bool _mode3EndPending;
    private int _paletteHblankCycles;
    private bool _coincidence;
    private bool _statLine;
    private bool _paletteAccessBlocked;
    private int _oamCorruptionRow;
    private int _renderedPixels;
    private int _mode3FineScroll;
    private bool _windowUsedOnLine;
    private bool _windowWyTriggered;
    private bool _windowTriggeredOnLine;
    private int _mode3WindowStart;
    private int _selectedSprites;
    private bool _mode3TallSprites;
    private int _mode3EndCycle;
    private int _fetchStall;
    private bool _spriteFetchActive;

    private readonly record struct SpriteCandidate(int OamIndex, int X, int Row, byte Tile, byte Attributes);

    public PpuDevice(GameBoyModel model, byte[] io, byte[] vram, byte[] oam,
        Action? hblankStarted = null, Action<bool>? lcdDisabled = null, Func<bool>? isDoubleSpeed = null)
    {
        _model = model;
        _io = io;
        _vram = vram;
        _oam = oam;
        _hblankStarted = hblankStarted;
        _lcdDisabled = lcdDisabled;
        _isDoubleSpeed = isDoubleSpeed ?? (() => false);
    }

    public bool CpuCanAccessVram => !_enabled || !_hblankBusBlocked && _mode != 3;

    public bool CpuCanReadOam => !_enabled || !_hblankBusBlocked && !_hblankOamReadBlocked &&
        (_mode != 2 && _mode != 3 || _mode == 2 && _isDoubleSpeed() && _model.IsColor() && _lineCycles < 76);

    public bool CpuCanWriteOam => !_enabled || !_hblankBusBlocked &&
        (_mode != 2 && _mode != 3 || _mode == 2 && _isDoubleSpeed() && _model.IsColor() && _lineCycles < 70);

    public bool IsVisibleHblank => _enabled && _line < Height && _mode == 0;

    public ReadOnlySpan<ushort> RawFrame => _colorFrame;

    public void Reset()
    {
        _enabled = false;
        _lineCycles = 0;
        _line = 0;
        _windowLine = 0;
        _mode = 0;
        _hblankBusBlocked = false;
        _hblankOamReadBlocked = false;
        _mode3EndPending = false;
        _paletteHblankCycles = 0;
        _coincidence = false;
        _statLine = false;
        _paletteAccessBlocked = false;
        _oamCorruptionRow = -1;
        _renderedPixels = 0;
        _mode3FineScroll = 0;
        _windowUsedOnLine = false;
        _windowWyTriggered = false;
        _windowTriggeredOnLine = false;
        _mode3WindowStart = 0;
        _selectedSprites = 0;
        _mode3TallSprites = false;
        _mode3EndCycle = 0;
        _fetchStall = 0;
        _spriteFetchActive = false;
        Array.Clear(_fetchPenaltyByPixel);
        Array.Clear(_spritePenaltyByPixel);
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
        0xFF42 or 0xFF43 or 0xFF47 or 0xFF48 or 0xFF49 or 0xFF4A or 0xFF4B => _io[address - 0xFF00],
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
            case 0xFF42 or 0xFF43 or 0xFF47 or 0xFF48 or 0xFF49 or 0xFF4B:
                _io[address - 0xFF00] = value;
                break;
            case 0xFF4A:
                _io[0x4A] = value;
                CheckWindowY();
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
            case 0xFF44: // LY is read-only.
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
        writer.Write(_windowWyTriggered);
        writer.Write(_mode);
        writer.Write(_hblankBusBlocked);
        writer.Write(_hblankOamReadBlocked);
        writer.Write(_mode3EndPending);
        writer.Write(_paletteHblankCycles);
        writer.Write(_coincidence);
        writer.Write(_statLine);
        writer.Write(_paletteAccessBlocked);
        writer.Write(_oamCorruptionRow);
        var rendering = _mode == 3 || _mode3EndPending;
        writer.Write(rendering ? _renderedPixels : 0);
        writer.Write(rendering ? _mode3FineScroll : 0);
        writer.Write(rendering && _windowUsedOnLine);
        writer.Write(rendering && _windowTriggeredOnLine);
        writer.Write(rendering ? _mode3WindowStart : 0);
        var selectedSprites = rendering ? _selectedSprites : 0;
        writer.Write(selectedSprites);
        writer.Write(rendering && _mode3TallSprites);
        writer.Write(rendering ? _mode3EndCycle : 0);
        writer.Write(rendering ? _fetchStall : 0);
        writer.Write(rendering && _spriteFetchActive);
        if (rendering) writer.Write(_fetchPenaltyByPixel);
        if (rendering) writer.Write(_spritePenaltyByPixel);
        for (var index = 0; index < selectedSprites; index++)
        {
            writer.Write(_spriteCandidates[index].OamIndex);
            writer.Write(_spriteCandidates[index].X);
            writer.Write(_spriteCandidates[index].Row);
            writer.Write(_spriteCandidates[index].Tile);
            writer.Write(_spriteCandidates[index].Attributes);
        }
        writer.Write(_backgroundPaletteRam);
        writer.Write(_objectPaletteRam);
    }

    public void AdvanceTCycle()
    {
        if (!_enabled) return;
        var transitionedToHblank = false;
        if (_mode3EndPending)
        {
            _mode3EndPending = false;
            SetMode(0);
            transitionedToHblank = true;
        }
        if (_hblankBusBlocked && !transitionedToHblank) _hblankBusBlocked = false;
        if (_hblankOamReadBlocked) _hblankOamReadBlocked = false;
        _lineCycles++;
        if (_paletteHblankCycles > 0 && --_paletteHblankCycles == 0)
            _paletteAccessBlocked = false;
        if (_line < 144)
        {
            if (_mode == 2 && (_lineCycles & 1) == 0 && _lineCycles is > 0 and < 80)
            {
                var searchIndex = (_lineCycles / 2) - 1;
                _oamCorruptionRow = Math.Min(0x98, ((searchIndex & ~1) * 4) + 8);
            }
            if (_lineCycles == 80)
            {
                _renderedPixels = 0;
                _mode3FineScroll = _io[0x43] & 0x07;
                _windowUsedOnLine = false;
                _windowTriggeredOnLine = false;
                _mode3WindowStart = 0;
                SelectSprites(_line);
                Array.Clear(_fetchPenaltyByPixel);
                Array.Clear(_spritePenaltyByPixel);
                var fetchPenalty = PrepareSpritePenalties(_line);
                _mode3EndCycle = Mode3End() + fetchPenalty;
                _fetchStall = 0;
                _spriteFetchActive = false;
                SetMode(3);
            }
            else if (_lineCycles == 85 && _model.IsColor()) _paletteAccessBlocked = true;
            else if (_mode == 3)
            {
                AdvancePixelTransfer();
                if (_lineCycles == _mode3EndCycle)
                {
                    FinishBackgroundLine();
                    if (_isDoubleSpeed()) _mode3EndPending = true;
                    else SetMode(0);
                }
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
            _windowWyTriggered = false;
            SetMode(2);
        }
        else if (_line < 144) SetMode(2);
        _io[0x44] = _line;
        CheckWindowY();
        UpdateCoincidence();
    }

    private int Mode3End()
    {
        return 252 + _mode3FineScroll;
    }

    private void AdvancePixelTransfer()
    {
        if (_lineCycles <= 92 + _mode3FineScroll || _renderedPixels == Width) return;
        if (_fetchStall != 0)
        {
            _fetchStall--;
            if (_fetchStall == 0) _spriteFetchActive = false;
            return;
        }
        TriggerWindowIfNeeded();
        var penalty = _fetchPenaltyByPixel[_renderedPixels];
        if (penalty != 0)
        {
            _fetchPenaltyByPixel[_renderedPixels] = 0;
            _fetchStall = penalty - 1;
            return;
        }
        penalty = _spritePenaltyByPixel[_renderedPixels];
        if (penalty != 0)
        {
            _spritePenaltyByPixel[_renderedPixels] = 0;
            if ((_io[0x40] & 0x02) != 0)
            {
                _fetchStall = penalty - 1;
                _spriteFetchActive = _fetchStall != 0;
                return;
            }
        }
        RenderBackgroundPixelsThrough(_renderedPixels + 1);
    }

    private void TriggerWindowIfNeeded()
    {
        if (_windowTriggeredOnLine || !_windowWyTriggered || (_io[0x40] & 0x21) != 0x21)
            return;

        var windowStart = _io[0x4B] - 7;
        if (windowStart >= Width || _renderedPixels != Math.Max(0, windowStart)) return;

        _windowTriggeredOnLine = true;
        _mode3WindowStart = windowStart;
        var penalty = 6;
        if (!_model.IsColor() && !_model.IsSuperGameBoy() && _io[0x4B] == 0 && _io[0x43] != 0)
            penalty++;
        _fetchPenaltyByPixel[_renderedPixels] = (byte)penalty;
        _mode3EndCycle += penalty;
    }

    private void WriteLcdc(byte value)
    {
        var wasEnabled = _enabled;
        UpdateDmgSpriteFetches(_io[0x40], value);
        UpdateWindowEnable(_io[0x40], value);
        _io[0x40] = value;
        _enabled = (value & 0x80) != 0;
        if (!wasEnabled && _enabled)
        {
            _line = 0;
            _windowLine = 0;
            _lineCycles = 0;
            _io[0x44] = 0;
            SetMode(2);
            CheckWindowY();
            UpdateCoincidence();
        }
        else if (wasEnabled && !_enabled)
        {
            _lcdDisabled?.Invoke(_mode != 0);
            _line = 0;
            _lineCycles = 0;
            _windowLine = 0;
            _windowWyTriggered = false;
            _hblankBusBlocked = false;
            _mode3EndPending = false;
            Array.Clear(_frame);
            Array.Clear(_colorFrame);
            _io[0x44] = 0;
            SetMode(0);
            UpdateCoincidence();
        }
        else if (_enabled)
        {
            CheckWindowY();
        }
    }

    private void UpdateWindowEnable(byte previous, byte value)
    {
        if (_mode == 3 && (previous & 0x20) != 0 && (value & 0x20) == 0)
            _windowTriggeredOnLine = false;
    }

    private void CheckWindowY()
    {
        if (_enabled && (_io[0x40] & 0x20) != 0 && _line < Height && _io[0x4A] == _line)
            _windowWyTriggered = true;
    }

    private void UpdateDmgSpriteFetches(byte previous, byte value)
    {
        if (_model.IsColor() || _model.IsSuperGameBoy() || _mode != 3 ||
            ((previous ^ value) & 0x02) == 0)
            return;

        var enabling = (value & 0x02) != 0;
        if (!enabling && _spriteFetchActive)
        {
            _mode3EndCycle -= _fetchStall;
            _fetchStall = 0;
            _spriteFetchActive = false;
        }

        for (var pixel = _renderedPixels; pixel < Width; pixel++)
        {
            var penalty = _spritePenaltyByPixel[pixel];
            _mode3EndCycle += enabling ? penalty : -penalty;
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
        if (mode == 1) _io[0x0F] |= 0x01;
        _hblankBusBlocked = _enabled && mode == 0 && _isDoubleSpeed();
        _hblankOamReadBlocked = mode == 0 && _enabled && IsLaterCgbRevision() && !_isDoubleSpeed();
        if (mode == 0 && _enabled && _model.IsColor() && !_isDoubleSpeed())
        {
            _paletteAccessBlocked = true;
            _paletteHblankCycles = 4;
        }
        else if (mode != 3)
        {
            _paletteAccessBlocked = false;
            _paletteHblankCycles = 0;
        }
        if (mode != 2) _oamCorruptionRow = -1;
        if (mode == 0 && _line < 144) _hblankStarted?.Invoke();
        UpdateStatLine();
    }

    private bool IsLaterCgbRevision() => _model is GameBoyModel.CgbD or GameBoyModel.CgbE or GameBoyModel.AgbA or GameBoyModel.GbpA;

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

    private void RenderBackgroundPixelsThrough(int target)
    {
        while (_renderedPixels < target) RenderBackgroundPixel(_line, _renderedPixels++);
    }

    private void FinishBackgroundLine()
    {
        RenderBackgroundPixelsThrough(Width);
        if (_windowUsedOnLine) _windowLine++;
    }

    private void RenderBackgroundPixel(byte line, int x)
    {
        var output = line * Width + x;
        if ((_io[0x40] & 0x01) == 0)
        {
            _backgroundColors[x] = 0;
            _backgroundPriority[x] = false;
            _frame[output] = 0;
            _colorFrame[output] = 0;
            RenderSpritePixel(x, output);
            return;
        }
        var mapBase = (_io[0x40] & 0x08) != 0 ? 0x1C00 : 0x1800;
        var unsignedTiles = (_io[0x40] & 0x10) != 0;
        var worldY = (line + _io[0x42]) & 0xFF;
        var useWindow = _windowTriggeredOnLine && (_io[0x40] & 0x20) != 0;
        var worldX = useWindow ? (x - _mode3WindowStart) & 0xFF : (x + _io[0x43]) & 0xFF;
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
        _frame[output] = (byte)((_io[0x47] >> (color * 2)) & 0x03);
        _colorFrame[output] = _model.IsColor()
            ? ReadColor(_backgroundPaletteRam, (attributes & 0x07) * 4 + color)
            : (ushort)_frame[output];
        _windowUsedOnLine |= useWindow;
        RenderSpritePixel(x, output);
    }

    private void SelectSprites(byte line)
    {
        _selectedSprites = 0;
        _mode3TallSprites = (_io[0x40] & 0x04) != 0;
        var height = _mode3TallSprites ? 16 : 8;
        var candidates = _spriteCandidates;
        for (var sprite = 0; sprite < 40 && _selectedSprites < candidates.Length; sprite++)
        {
            var oam = sprite * 4;
            var y = _oam[oam] - 16;
            var x = _oam[oam + 1] - 8;
            var tile = _oam[oam + 2];
            var attributes = _oam[oam + 3];
            var row = line - y;
            if (row < 0 || row >= height) continue;
            if ((attributes & 0x40) != 0) row = height - 1 - row;
            candidates[_selectedSprites++] = new SpriteCandidate(sprite, x, row, tile, attributes);
        }

        // DMG and CGB X-priority mode resolve overlaps by lower screen X, then OAM order.
        if (!_model.IsColor() || (_io[0x6C] & 0x01) != 0)
        {
            for (var i = 1; i < _selectedSprites; i++)
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
    }

    private int PrepareSpritePenalties(byte line)
    {
        if (_model.IsColor() || _model.IsSuperGameBoy() || (_io[0x40] & 0x02) == 0) return 0;

        Span<int> order = stackalloc int[_selectedSprites];
        for (var index = 0; index < order.Length; index++) order[index] = index;
        for (var index = 1; index < order.Length; index++)
        {
            var candidate = order[index];
            var previous = index - 1;
            while (previous >= 0 && ComesAfter(_spriteCandidates[order[previous]], _spriteCandidates[candidate]))
            {
                order[previous + 1] = order[previous];
                previous--;
            }
            order[previous + 1] = candidate;
        }

        Span<int> consideredTiles = stackalloc int[10];
        var consideredTileCount = 0;
        var total = 0;
        foreach (var index in order)
        {
            var sprite = _spriteCandidates[index];
            if (sprite.X >= Width || sprite.X < -8) continue;
            var trigger = Math.Max(0, sprite.X);
            var penalty = 6;
            if (sprite.X == -8)
            {
                penalty = 11;
            }
            else
            {
                var tile = SpriteFetchTile(sprite.X, line);
                if (!consideredTiles[..consideredTileCount].Contains(tile))
                {
                    consideredTiles[consideredTileCount++] = tile;
                    var pixelInTile = SpritePixelInTile(sprite.X, line);
                    penalty += Math.Max(5 - pixelInTile, 0);
                }
            }
            _spritePenaltyByPixel[trigger] += (byte)penalty;
            total += penalty;
        }
        return total;

        static bool ComesAfter(SpriteCandidate first, SpriteCandidate second) =>
            first.X > second.X || first.X == second.X && first.OamIndex > second.OamIndex;
    }

    private int SpriteFetchTile(int x, byte line)
    {
        var windowStart = _io[0x4B] - 7;
        var useWindow = (_io[0x40] & 0x20) != 0 && line >= _io[0x4A] && _io[0x4A] < Height &&
            windowStart < Width && x >= windowStart;
        return useWindow ? 0x100 + ((x - windowStart) >> 3) : (x + _io[0x43]) >> 3;
    }

    private int SpritePixelInTile(int x, byte line)
    {
        var windowStart = _io[0x4B] - 7;
        var useWindow = (_io[0x40] & 0x20) != 0 && line >= _io[0x4A] && _io[0x4A] < Height &&
            windowStart < Width && x >= windowStart;
        return useWindow ? (x - windowStart) & 7 : (x + _io[0x43]) & 7;
    }

    private void RenderSpritePixel(int screenX, int output)
    {
        if ((_io[0x40] & 0x02) == 0) return;
        for (var index = 0; index < _selectedSprites; index++)
        {
            var candidate = _spriteCandidates[index];
            var pixel = screenX - candidate.X;
            if (pixel is < 0 or >= 8) continue;
            var row = candidate.Row;
            var tile = candidate.Tile;
            var attributes = candidate.Attributes;
            if (_mode3TallSprites)
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
            var bit = (attributes & 0x20) != 0 ? pixel : 7 - pixel;
            var color = ((high >> bit) & 1) << 1 | ((low >> bit) & 1);
            if (color == 0) continue;
            if (_model.IsColor() && _backgroundColors[screenX] != 0 && _backgroundPriority[screenX]) continue;
            if ((attributes & 0x80) != 0 && _backgroundColors[screenX] != 0) continue;
            _frame[output] = (byte)((palette >> (color * 2)) & 0x03);
            _colorFrame[output] = _model.IsColor()
                ? ReadColor(_objectPaletteRam, (attributes & 0x07) * 4 + color)
                : (ushort)_frame[output];
            return;
        }
    }

    private static ushort ReadColor(byte[] paletteRam, int colorIndex)
    {
        var address = colorIndex * 2;
        return (ushort)(paletteRam[address] | (paletteRam[address + 1] << 8));
    }
}
