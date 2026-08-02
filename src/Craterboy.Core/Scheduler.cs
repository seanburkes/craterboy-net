namespace Craterboy;

internal interface ICycleParticipant
{
    void AdvanceTCycle();
}

internal sealed class Scheduler
{
    private readonly List<ICycleParticipant> _participants = new();

    public long CycleCount { get; private set; }

    public void Register(ICycleParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        _participants.Add(participant);
    }

    public void Reset() => CycleCount = 0;

    public void Advance(int cycles)
    {
        if (cycles < 0) throw new ArgumentOutOfRangeException(nameof(cycles));
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            foreach (var participant in _participants)
                participant.AdvanceTCycle();
            CycleCount = checked(CycleCount + 1);
        }
    }
}

internal sealed class TimerDevice : ICycleParticipant
{
    private readonly byte[] _io;
    private ushort _divider;

    public TimerDevice(byte[] io) => _io = io;

    public void Reset()
    {
        _divider = 0;
        _io[0x04] = 0;
        _io[0x05] = 0;
        _io[0x06] = 0;
        _io[0x07] = 0;
    }

    public byte Read(ushort address) => address switch
    {
        0xFF04 => (byte)(_divider >> 8),
        0xFF05 => _io[0x05],
        0xFF06 => _io[0x06],
        0xFF07 => (byte)(_io[0x07] | 0xF8),
        _ => 0xFF,
    };

    public void Write(ushort address, byte value)
    {
        switch (address)
        {
            case 0xFF04:
                _divider = 0;
                break;
            case 0xFF05:
                _io[0x05] = value;
                break;
            case 0xFF06:
                _io[0x06] = value;
                break;
            case 0xFF07:
                var control = (byte)(value & 0x07);
                if (TimerSignal(_divider, _io[0x07]) && !TimerSignal(_divider, control))
                    IncrementTima();
                _io[0x07] = control;
                break;
        }
    }

    public void AdvanceTCycle()
    {
        var oldSignal = TimerSignal(_divider, _io[0x07]);
        _divider++;
        _io[0x04] = (byte)(_divider >> 8);
        var newSignal = TimerSignal(_divider, _io[0x07]);
        if (oldSignal && !newSignal)
            IncrementTima();
    }

    private void IncrementTima()
    {
        if (_io[0x05] == 0xFF)
        {
            _io[0x05] = _io[0x06];
            _io[0x0F] |= 0x04;
        }
        else
        {
            _io[0x05]++;
        }
    }

    private static bool TimerSignal(ushort divider, byte control)
    {
        if ((control & 0x04) == 0) return false;
        var bit = (control & 0x03) switch
        {
            0 => 9,
            1 => 3,
            2 => 5,
            _ => 7,
        };
        return (divider & (1 << bit)) != 0;
    }
}

internal sealed class EmulatorState
{
    public CpuState Cpu { get; } = new();
    public Scheduler Scheduler { get; } = new();
}
