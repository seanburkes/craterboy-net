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
    private readonly byte[] _io;
    private readonly GameBoyModel _model;
    private readonly bool[] _pressed = new bool[8];
    private byte _select;
    private byte _activeSelect;
    private byte _pendingSelect;
    private int _switchingDelay;

    public JoypadDevice(GameBoyModel model, byte[] io)
    {
        _model = model;
        _io = io;
    }

    public void Reset()
    {
        Array.Clear(_pressed);
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
        _pressed[(int)button] = pressed;
        _io[0x00] = Read();
        RequestInterruptOnFallingEdge(previous);
    }

    public void WriteStateHash(BinaryWriter writer)
    {
        writer.Write(_pressed.Length);
        foreach (var pressed in _pressed) writer.Write(pressed);
        writer.Write(_select);
        writer.Write(_activeSelect);
        writer.Write(_pendingSelect);
        writer.Write(_switchingDelay);
    }

    public void AdvanceTCycle()
    {
        if (_switchingDelay == 0) return;
        if (--_switchingDelay != 0) return;
        ApplySelection(_pendingSelect, Read());
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
            if (_pressed[(int)GameBoyButton.Right]) result &= ~0x01;
            if (_pressed[(int)GameBoyButton.Left]) result &= ~0x02;
            if (_pressed[(int)GameBoyButton.Up]) result &= ~0x04;
            if (_pressed[(int)GameBoyButton.Down]) result &= ~0x08;
            if ((result & 0x01) == 0) result |= 0x02;
            if ((result & 0x04) == 0) result |= 0x08;
        }
        if ((_activeSelect & 0x20) == 0)
        {
            if (_pressed[(int)GameBoyButton.A]) result &= ~0x01;
            if (_pressed[(int)GameBoyButton.B]) result &= ~0x02;
            if (_pressed[(int)GameBoyButton.Select]) result &= ~0x04;
            if (_pressed[(int)GameBoyButton.Start]) result &= ~0x08;
        }
        return (byte)result;
    }

    private void RequestInterruptOnFallingEdge(byte previous)
    {
        if ((previous & 0x0F & ~Read()) != 0) _io[0x0F] |= 0x10;
    }
}
