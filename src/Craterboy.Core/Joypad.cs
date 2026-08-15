namespace Craterboy;

public enum GameBoyButton
{
    Right,
    Left,
    Up,
    Down,
    A,
    B,
    Select,
    Start,
}

internal sealed class JoypadDevice : ICycleParticipant
{
    private static readonly byte[] AnalogPatterns = { 0x01, 0x11, 0x94, 0x55, 0x6D, 0x77, 0x7F };
    private readonly byte[] _io;
    private readonly GameBoyModel _model;
    private readonly bool _bounceEnabled;
    private readonly bool[] _pressed = new bool[8];
    private readonly int[] _bounceTiming = new int[8];
    private readonly sbyte[] _analog = new sbyte[2];
    private bool _fauxAnalogEnabled;
    private byte _analogTicks;
    private int _frameCycles;
    private byte _select;
    private byte _activeSelect;
    private byte _pendingSelect;
    private int _switchingDelay;

    public JoypadDevice(GameBoyModel model, byte[] io, bool bounceEnabled)
    {
        _model = model;
        _io = io;
        _bounceEnabled = bounceEnabled && !model.IsSuperGameBoy() && !model.IsGbp();
    }

    public void Reset()
    {
        Array.Clear(_pressed);
        Array.Clear(_bounceTiming);
        Array.Clear(_analog);
        _fauxAnalogEnabled = false;
        _analogTicks = 0;
        _frameCycles = 0;
        _select = 0x30;
        _activeSelect = 0x30;
        _pendingSelect = 0x30;
        _switchingDelay = 0;
        _io[0x00] = 0xCF;
    }

    public byte Read() => (byte)(0xC0 | _select | (ReadButtons() & 0x0F));

    public void Write(byte value)
    {
        var previous = Read();
        var next = (byte)(value & 0x30);
        _select = next;
        var delay = SelectionDelay(_activeSelect, next);
        if (delay == 0)
        {
            ApplySelection(next, previous);
        }
        else
        {
            _pendingSelect = next;
            _switchingDelay = delay;
            _io[0x00] = Read();
        }
    }

    public void SetButtonState(GameBoyButton button, bool pressed, int player)
    {
        if (player != 0)
            throw new ArgumentOutOfRangeException(nameof(player), "Only the primary joypad is available before SGB multiplayer support.");
        var previous = Read();
        if (_bounceEnabled && pressed != _pressed[(int)button])
            _bounceTiming[(int)button] = button is GameBoyButton.Start or GameBoyButton.Select ? 0x1FFF : 0x0FFF;
        _pressed[(int)button] = pressed;
        _io[0x00] = Read();
        RequestInterruptOnFallingEdge(previous);
    }

    public void SetFauxAnalogInput(double x, double y)
    {
        x = Math.Clamp(x, -1, 1);
        y = Math.Clamp(y, -1, 1);
        var absoluteX = Math.Abs(x);
        var absoluteY = Math.Abs(y);
        if (absoluteX <= 0.1) x = absoluteX = 0;
        if (absoluteY <= 0.1) y = absoluteY = 0;
        if (x == 0 && y == 0)
        {
            _analog[0] = _analog[1] = 0;
        }
        else
        {
            if (x != 0)
            {
                absoluteX = (absoluteX - 0.1) / 0.9;
                x = x > 0 ? absoluteX : -absoluteX;
            }
            if (y != 0)
            {
                absoluteY = (absoluteY - 0.1) / 0.9;
                y = y > 0 ? absoluteY : -absoluteY;
            }
            var distance = Math.Min(Math.Sqrt(x * x + y * y), 1);
            var multiplier = 8 * distance / Math.Max(absoluteX, absoluteY);
            _analog[0] = (sbyte)Math.Clamp((int)Math.Round(x * multiplier, MidpointRounding.AwayFromZero), -8, 8);
            _analog[1] = (sbyte)Math.Clamp((int)Math.Round(y * multiplier, MidpointRounding.AwayFromZero), -8, 8);
        }
        Array.Clear(_pressed, 0, 4);
        _fauxAnalogEnabled = true;
        _io[0x00] = Read();
    }

    public void DisableFauxAnalogInput()
    {
        _fauxAnalogEnabled = false;
        _analog[0] = _analog[1] = 0;
        _io[0x00] = Read();
    }

    public void WriteStateHash(BinaryWriter writer)
    {
        writer.Write(_pressed.Length);
        foreach (var pressed in _pressed) writer.Write(pressed);
        foreach (var timing in _bounceTiming) writer.Write(timing);
        writer.Write(_fauxAnalogEnabled);
        writer.Write((sbyte)_analog[0]);
        writer.Write((sbyte)_analog[1]);
        writer.Write(_analogTicks);
        writer.Write(_frameCycles);
        writer.Write(_select);
        writer.Write(_activeSelect);
        writer.Write(_pendingSelect);
        writer.Write(_switchingDelay);
    }

    public void AdvanceTCycle()
    {
        var update = false;
        if (_switchingDelay != 0 && --_switchingDelay == 0)
        {
            ApplySelection(_pendingSelect, Read());
            update = true;
        }
        for (var index = 0; index < _bounceTiming.Length; index++)
        {
            if (_bounceTiming[index] == 0) continue;
            _bounceTiming[index]--;
            update = true;
        }
        if (update) _io[0x00] = Read();
        if (++_frameCycles == 70_224)
        {
            _frameCycles = 0;
            _analogTicks++;
            _io[0x00] = Read();
        }
    }

    private void ApplySelection(byte selection, byte previous)
    {
        _activeSelect = selection;
        _io[0x00] = Read();
        RequestInterruptOnFallingEdge(previous);
    }

    private int SelectionDelay(byte previous, byte next)
    {
        if (!_model.IsDmg() && !_model.IsMgb()) return 0;
        var key = ((previous & 0x30) >> 4) | ((next & 0x30) >> 2);
        var delay = key switch
        {
            0x04 or 0x06 or 0x0C or 0x0E => 48,
            0x08 or 0x09 or 0x0D => 24,
            _ => 0,
        };
        return _model.IsMgb() ? Math.Max(0, delay - 16) : delay;
    }

    private byte ReadButtons()
    {
        var result = 0x0F;
        if ((_activeSelect & 0x10) == 0)
        {
            if (IsPressed(GameBoyButton.Right)) result &= ~0x01;
            if (IsPressed(GameBoyButton.Left)) result &= ~0x02;
            if (IsPressed(GameBoyButton.Up)) result &= ~0x04;
            if (IsPressed(GameBoyButton.Down)) result &= ~0x08;
            if ((result & 0x01) == 0) result |= 0x02;
            if ((result & 0x04) == 0) result |= 0x08;
        }
        if ((_activeSelect & 0x20) == 0)
        {
            if (IsPressed(GameBoyButton.A)) result &= ~0x01;
            if (IsPressed(GameBoyButton.B)) result &= ~0x02;
            if (IsPressed(GameBoyButton.Select)) result &= ~0x04;
            if (IsPressed(GameBoyButton.Start)) result &= ~0x08;
        }
        return (byte)result;
    }

    private bool IsPressed(GameBoyButton button)
    {
        var index = (int)button;
        if (_fauxAnalogEnabled && index <= (int)GameBoyButton.Down && _pressed[index]) return true;
        if (_fauxAnalogEnabled && index <= (int)GameBoyButton.Down)
        {
            var input = index switch
            {
                (int)GameBoyButton.Right => _analog[0],
                (int)GameBoyButton.Left => -_analog[0],
                (int)GameBoyButton.Up => -_analog[1],
                _ => _analog[1],
            };
            if (input <= 0) return false;
            if (input >= 8) return true;
            var pattern = AnalogPatterns[input - 1];
            var offset = index is (int)GameBoyButton.Up or (int)GameBoyButton.Down ? 2 : 0;
            return (pattern & (1 << ((_analogTicks + offset) & 6))) != 0;
        }
        if (!_bounceEnabled || _bounceTiming[index] == 0 || (_bounceTiming[index] & 0x3FF) > 0x300)
            return _pressed[index];
        var sample = ((((index << 5) + _bounceTiming[index]) * 17) ^ ((_bounceTiming[index] ^ 0x5A5) * 13)) >> 3;
        return _pressed[index] ^ ((sample & 0x7FF) < _bounceTiming[index]);
    }

    private void RequestInterruptOnFallingEdge(byte previous)
    {
        if ((previous & 0x0F & ~Read()) != 0) _io[0x0F] |= 0x10;
    }
}
