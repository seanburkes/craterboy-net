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

internal sealed class JoypadDevice
{
    private readonly byte[] _io;
    private readonly bool[] _pressed = new bool[8];
    private byte _select;

    public JoypadDevice(byte[] io) => _io = io;

    public void Reset()
    {
        Array.Clear(_pressed);
        _select = 0x30;
        _io[0x00] = 0xCF;
    }

    public byte Read() => (byte)(0xC0 | _select | (ReadButtons() & 0x0F));

    public void Write(byte value)
    {
        var previous = Read();
        _select = (byte)(value & 0x30);
        _io[0x00] = Read();
        RequestInterruptOnFallingEdge(previous);
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

    private byte ReadButtons()
    {
        var result = 0x0F;
        if ((_select & 0x10) == 0)
        {
            if (_pressed[(int)GameBoyButton.Right]) result &= ~0x01;
            if (_pressed[(int)GameBoyButton.Left]) result &= ~0x02;
            if (_pressed[(int)GameBoyButton.Up]) result &= ~0x04;
            if (_pressed[(int)GameBoyButton.Down]) result &= ~0x08;
        }
        if ((_select & 0x20) == 0)
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
