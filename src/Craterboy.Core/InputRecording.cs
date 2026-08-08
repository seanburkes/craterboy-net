namespace Craterboy;

public readonly record struct InputEvent(long Cycle, GameBoyButton Button, bool Pressed, int Player = 0);

public sealed class InputRecording
{
    private const int MaxEvents = 1_000_000;
    private readonly List<InputEvent> _events = new();
    private readonly IReadOnlyList<InputEvent> _readOnlyEvents;

    public InputRecording() => _readOnlyEvents = _events.AsReadOnly();

    public IReadOnlyList<InputEvent> Events => _readOnlyEvents;

    public void Add(InputEvent inputEvent)
    {
        if (inputEvent.Cycle < 0) throw new ArgumentOutOfRangeException(nameof(inputEvent));
        if (!Enum.IsDefined(inputEvent.Button)) throw new ArgumentOutOfRangeException(nameof(inputEvent));
        if (inputEvent.Player != 0) throw new ArgumentOutOfRangeException(nameof(inputEvent), "Only the primary player is supported until SGB multiplayer input is implemented.");
        if (_events.Count > 0 && inputEvent.Cycle < _events[^1].Cycle)
            throw new ArgumentException("Input events must be ordered by emulated cycle.", nameof(inputEvent));
        if (_events.Count == MaxEvents) throw new InvalidOperationException("Input recording event limit reached.");
        _events.Add(inputEvent);
    }

    public void Write(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var writer = new BinaryWriter(destination, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write("CBIN"u8.ToArray());
        writer.Write((ushort)1);
        writer.Write(_events.Count);
        foreach (var inputEvent in _events)
        {
            writer.Write(inputEvent.Cycle);
            writer.Write((byte)inputEvent.Button);
            writer.Write(inputEvent.Pressed);
            writer.Write((byte)inputEvent.Player);
        }
    }

    public static InputRecording Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var reader = new BinaryReader(source, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadBytes(4) is not { Length: 4 } magic || !magic.AsSpan().SequenceEqual("CBIN"u8))
            throw new InvalidDataException("Input recording magic is invalid.");
        if (reader.ReadUInt16() != 1) throw new InvalidDataException("Input recording version is unsupported.");
        var count = reader.ReadInt32();
        if (count < 0 || count > MaxEvents) throw new InvalidDataException("Input recording event count is invalid.");
        var recording = new InputRecording();
        for (var index = 0; index < count; index++)
        {
            var cycle = reader.ReadInt64();
            var button = (GameBoyButton)reader.ReadByte();
            var pressed = reader.ReadBoolean();
            var player = reader.ReadByte();
            recording.Add(new InputEvent(cycle, button, pressed, player));
        }
        if (reader.BaseStream.ReadByte() != -1) throw new InvalidDataException("Input recording has trailing data.");
        return recording;
    }
}
