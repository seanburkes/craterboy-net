namespace Craterboy;

internal sealed class ApuDevice : ICycleParticipant
{
    private static readonly byte[] DutyPatterns = [0b00000001, 0b10000001, 0b10000111, 0b01111110];
    private readonly byte[] _io;
    private bool _powered;
    private int _frameCycles;
    private int _frameStep;
    private int _channel1Length;
    private bool _channel1Enabled;
    private int _channel1Volume;
    private int _envelopeTimer;
    private int _sweepTimer;
    private int _channel1Frequency;
    private int _channel1SweepFrequency;
    private bool _sweepEnabled;
    private int _wave1Phase;
    private int _channel2Length;
    private bool _channel2Enabled;
    private int _channel2Volume;
    private int _channel2EnvelopeTimer;
    private int _channel2Frequency;
    private int _wave2Phase;
    private int _channel3Length;
    private bool _channel3Enabled;
    private int _wave3Phase;
    private int _channel3Frequency;
    private int _channel4Length;
    private bool _channel4Enabled;
    private int _channel4Volume;
    private int _channel4EnvelopeTimer;
    private ushort _noiseLfsr;
    private int _noiseTimer;
    private bool _mixerConfigured;
    private int _sampleCycles;
    private readonly short[] _samples = new short[4096];
    private int _sampleRead;
    private int _sampleWrite;
    private int _sampleCount;

    public ApuDevice(byte[] io) => _io = io;

    public void Reset()
    {
        _powered = false;
        _frameCycles = 0;
        _frameStep = 0;
        _channel1Length = 0;
        _channel1Enabled = false;
        _channel1Volume = 0;
        _envelopeTimer = 0;
        _sweepTimer = 0;
        _channel1Frequency = 0;
        _channel1SweepFrequency = 0;
        _sweepEnabled = false;
        _wave1Phase = 0;
        _channel2Length = 0;
        _channel2Enabled = false;
        _channel2Volume = 0;
        _channel2EnvelopeTimer = 0;
        _channel2Frequency = 0;
        _wave2Phase = 0;
        _channel3Length = 0;
        _channel3Enabled = false;
        _wave3Phase = 0;
        _channel3Frequency = 0;
        _channel4Length = 0;
        _channel4Enabled = false;
        _channel4Volume = 0;
        _channel4EnvelopeTimer = 0;
        _noiseLfsr = 0x7FFF;
        _noiseTimer = 0;
        _mixerConfigured = false;
        _sampleCycles = 0;
        _sampleRead = 0;
        _sampleWrite = 0;
        _sampleCount = 0;
        Array.Clear(_io, 0x10, 0x16);
        Array.Clear(_io, 0x30, 0x10);
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
            if (!powered && _powered)
            {
                Array.Clear(_io, 0x10, 0x16);
                _channel1Length = 0;
                _channel1Enabled = false;
                _channel1Volume = 0;
                _envelopeTimer = 0;
                _sweepTimer = 0;
                _channel1Frequency = 0;
                _channel1SweepFrequency = 0;
                _sweepEnabled = false;
                _wave1Phase = 0;
                _channel2Length = 0;
                _channel2Enabled = false;
                _channel2Volume = 0;
                _channel2EnvelopeTimer = 0;
                _channel2Frequency = 0;
                _wave2Phase = 0;
                _channel3Length = 0;
                _channel3Enabled = false;
                _wave3Phase = 0;
                _channel3Frequency = 0;
                _channel4Length = 0;
                _channel4Enabled = false;
                _channel4Volume = 0;
                _channel4EnvelopeTimer = 0;
                _noiseLfsr = 0x7FFF;
                _noiseTimer = 0;
                _mixerConfigured = false;
                Array.Clear(_io, 0x30, 0x10);
            }
            _powered = powered;
            _io[0x26] = (byte)(powered ? 0x80 : 0);
            return;
        }

        if (!_powered || address is < 0xFF10 or > 0xFF3F) return;
        if (address >= 0xFF30)
        {
            _io[address - 0xFF00] = value;
            return;
        }
        var previousValue = _io[address - 0xFF00];
        _io[address - 0xFF00] = value;
        if (address is 0xFF24 or 0xFF25) _mixerConfigured = true;
        switch (address)
        {
            case 0xFF10:
                _sweepEnabled = (value & 0x70) != 0;
                if (_channel1Enabled)
                {
                    _sweepTimer = SweepPeriodTicks();
                    var negateTransition = (previousValue & 0x08) != 0 && (value & 0x08) == 0;
                    if (_sweepEnabled && ((value & 0x07) != 0 || negateTransition) && SweepFrequency() > 2047)
                    {
                        _channel1Enabled = false;
                        UpdateStatus();
                    }
                }
                break;
            case 0xFF11:
                _channel1Length = 64 - (value & 0x3F);
                break;
            case 0xFF13:
                _channel1Frequency = (_channel1Frequency & 0x700) | value;
                break;
            case 0xFF18:
                _channel2Frequency = (_channel2Frequency & 0x700) | value;
                break;
            case 0xFF16:
                _channel2Length = 64 - (value & 0x3F);
                break;
            case 0xFF19:
                _channel2Frequency = (_channel2Frequency & 0x0FF) | ((value & 0x07) << 8);
                if ((value & 0x80) != 0)
                {
                    if (_channel2Length == 0) _channel2Length = 64;
                    _channel2Volume = _io[0x17] >> 4;
                    _channel2EnvelopeTimer = (_io[0x17] & 0x07) == 0 ? 8 : (_io[0x17] & 0x07);
                    _channel2Enabled = (_io[0x17] & 0xF8) != 0;
                    _wave2Phase = 0;
                    UpdateStatus();
                }
                break;
            case 0xFF1B:
                _channel3Length = 256 - value;
                break;
            case 0xFF1D:
                _channel3Frequency = (_channel3Frequency & 0x700) | value;
                break;
            case 0xFF1E when (value & 0x80) != 0:
                if (_channel3Length == 0) _channel3Length = 256;
                _channel3Frequency = (_channel3Frequency & 0x0FF) | ((value & 0x07) << 8);
                _channel3Enabled = (_io[0x1A] & 0x80) != 0;
                _wave3Phase = 0;
                UpdateStatus();
                break;
            case 0xFF20:
                _channel4Length = 64 - (value & 0x3F);
                break;
            case 0xFF23 when (value & 0x80) != 0:
                if (_channel4Length == 0) _channel4Length = 64;
                _channel4Volume = _io[0x21] >> 4;
                _channel4EnvelopeTimer = (_io[0x21] & 0x07) == 0 ? 8 : (_io[0x21] & 0x07);
                _channel4Enabled = (_io[0x21] & 0xF8) != 0;
                _noiseLfsr = 0x7FFF;
                _noiseTimer = 0;
                UpdateStatus();
                break;
            case 0xFF14:
                _channel1Frequency = (_channel1Frequency & 0x0FF) | ((value & 0x07) << 8);
                if ((value & 0x80) != 0)
                {
                    if (_channel1Length == 0) _channel1Length = 64;
                    _channel1Enabled = (_io[0x12] & 0xF8) != 0;
                    _channel1Volume = _io[0x12] >> 4;
                    _envelopeTimer = (_io[0x12] & 0x07) == 0 ? 8 : (_io[0x12] & 0x07);
                    _sweepEnabled = (_io[0x10] & 0x70) != 0;
                    _channel1SweepFrequency = _channel1Frequency;
                    _sweepTimer = SweepPeriodTicks();
                    if ((_io[0x10] & 0x07) != 0 && SweepFrequency() > 2047)
                    {
                        _channel1Enabled = false;
                    }
                    _wave1Phase = 0;
                    UpdateStatus();
                }
                break;
        }
    }

    public void AdvanceTCycle()
    {
        if (!_powered) return;
        if (++_sampleCycles >= 95)
        {
            _sampleCycles = 0;
            EmitSample();
        }
        if (++_frameCycles < 8192) return;
        _frameCycles = 0;
        _frameStep = (_frameStep + 1) & 7;
        if ((_io[0x14] & 0x40) != 0 && _channel1Enabled && _channel1Length > 0 && --_channel1Length == 0)
        {
            _channel1Enabled = false;
            UpdateStatus();
        }
        if ((_io[0x19] & 0x40) != 0 && _channel2Enabled && _channel2Length > 0 && --_channel2Length == 0)
        {
            _channel2Enabled = false;
            UpdateStatus();
        }
        if ((_io[0x1E] & 0x40) != 0 && _channel3Enabled && _channel3Length > 0 && --_channel3Length == 0)
        {
            _channel3Enabled = false;
            UpdateStatus();
        }
        if ((_io[0x23] & 0x40) != 0 && _channel4Enabled && _channel4Length > 0 && --_channel4Length == 0)
        {
            _channel4Enabled = false;
            UpdateStatus();
        }
        if (_frameStep == 7 && _channel1Enabled && (_io[0x12] & 0x07) != 0 && --_envelopeTimer == 0)
        {
            if ((_io[0x12] & 0x08) != 0 && _channel1Volume < 15) _channel1Volume++;
            else if ((_io[0x12] & 0x08) == 0 && _channel1Volume > 0) _channel1Volume--;
            _io[0x12] = (byte)((_io[0x12] & 0x0F) | (_channel1Volume << 4));
            _envelopeTimer = _io[0x12] & 0x07;
        }
        if (_frameStep == 7 && _channel2Enabled && (_io[0x17] & 0x07) != 0 && --_channel2EnvelopeTimer == 0)
        {
            if ((_io[0x17] & 0x08) != 0 && _channel2Volume < 15) _channel2Volume++;
            else if ((_io[0x17] & 0x08) == 0 && _channel2Volume > 0) _channel2Volume--;
            _io[0x17] = (byte)((_io[0x17] & 0x0F) | (_channel2Volume << 4));
            _channel2EnvelopeTimer = _io[0x17] & 0x07;
        }
        if (_frameStep == 7 && _channel4Enabled && (_io[0x21] & 0x07) != 0 && --_channel4EnvelopeTimer == 0)
        {
            if ((_io[0x21] & 0x08) != 0 && _channel4Volume < 15) _channel4Volume++;
            else if ((_io[0x21] & 0x08) == 0 && _channel4Volume > 0) _channel4Volume--;
            _io[0x21] = (byte)((_io[0x21] & 0x0F) | (_channel4Volume << 4));
            _channel4EnvelopeTimer = _io[0x21] & 0x07;
        }
        if (_frameStep is 2 or 6 && _channel1Enabled && _sweepEnabled && --_sweepTimer <= 0)
        {
            _sweepTimer = SweepPeriodTicks();
            var next = SweepFrequency();
            if (next > 2047 || next < 0)
            {
                _channel1Enabled = false;
                UpdateStatus();
            }
            else if ((_io[0x10] & 0x07) != 0)
            {
                _channel1Frequency = next;
                _channel1SweepFrequency = next;
                _io[0x13] = (byte)next;
                _io[0x14] = (byte)((_io[0x14] & 0xF8) | (next >> 8));
            }
        }
    }

    private int SweepPeriodTicks() => Math.Max(1, (_io[0x10] >> 4) & 0x07);

    private int SweepFrequency()
    {
        var delta = _channel1SweepFrequency >> (_io[0x10] & 0x07);
        return (_io[0x10] & 0x08) != 0
            ? _channel1SweepFrequency - delta
            : _channel1SweepFrequency + delta;
    }

    public int CopySamples(Span<short> destination)
    {
        var copied = 0;
        while (copied < destination.Length && _sampleCount > 0)
        {
            destination[copied++] = _samples[_sampleRead];
            _sampleRead = (_sampleRead + 1) % _samples.Length;
            _sampleCount--;
        }
        return copied;
    }

    private void EmitSample()
    {
        Span<int> channels = stackalloc int[4];
        if (_channel1Enabled)
        {
            var duty = (_io[0x11] >> 6) & 0x03;
            var high = ((DutyPatterns[duty] >> (7 - _wave1Phase)) & 1) != 0;
            channels[0] = high ? _channel1Volume * 2048 : -_channel1Volume * 2048;
            _wave1Phase = (_wave1Phase + Math.Max(1, _channel1Frequency >> 8)) & 7;
        }
        if (_channel2Enabled)
        {
            var duty = (_io[0x16] >> 6) & 0x03;
            var high = ((DutyPatterns[duty] >> (7 - _wave2Phase)) & 1) != 0;
            channels[1] = high ? _channel2Volume * 2048 : -_channel2Volume * 2048;
            _wave2Phase = (_wave2Phase + Math.Max(1, _channel2Frequency >> 8)) & 7;
        }
        if (_channel3Enabled)
        {
            var packed = _io[0x30 + (_wave3Phase >> 1)];
            var nibble = (_wave3Phase & 1) == 0 ? packed >> 4 : packed & 0x0F;
            var volumeCode = (_io[0x1C] >> 5) & 0x03;
            var waveSample = volumeCode switch { 0 => 0, 1 => nibble, 2 => nibble >> 1, _ => nibble >> 2 };
            channels[2] = volumeCode == 0 ? 0 : (waveSample - 4) * 2048;
            _wave3Phase = (_wave3Phase + Math.Max(1, _channel3Frequency >> 8)) & 31;
        }
        if (_channel4Enabled)
        {
            var high = (_noiseLfsr & 1) == 0;
            channels[3] = high ? _channel4Volume * 2048 : -_channel4Volume * 2048;
            if (_noiseTimer-- <= 0)
            {
                var feedback = (_noiseLfsr & 1) ^ ((_noiseLfsr >> 1) & 1);
                _noiseLfsr = (ushort)((_noiseLfsr >> 1) | (feedback << 14));
                if ((_io[0x22] & 0x08) != 0) _noiseLfsr = (ushort)((_noiseLfsr & ~0x40) | (feedback << 6));
                var divisor = (_io[0x22] & 0x07) switch { 0 => 8, 1 => 16, 2 => 32, 3 => 48, 4 => 64, 5 => 80, 6 => 96, _ => 112 };
                _noiseTimer = divisor << ((_io[0x22] >> 4) & 0x0F);
            }
        }
        var routing = _mixerConfigured ? _io[0x25] : 0xFF;
        var left = 0;
        var right = 0;
        for (var channel = 0; channel < channels.Length; channel++)
        {
            if ((routing & (1 << channel)) != 0) right += channels[channel];
            if ((routing & (1 << (channel + 4))) != 0) left += channels[channel];
        }
        var rightVolume = _mixerConfigured ? (_io[0x24] & 0x07) + 1 : 8;
        var leftVolume = _mixerConfigured ? ((_io[0x24] >> 4) & 0x07) + 1 : 8;
        var sample = (left * leftVolume + right * rightVolume) / 16;
        sample = Math.Clamp(sample, short.MinValue, short.MaxValue);
        if (_sampleCount == _samples.Length)
        {
            _sampleRead = (_sampleRead + 1) % _samples.Length;
            _sampleCount--;
        }
        _samples[_sampleWrite] = (short)sample;
        _sampleWrite = (_sampleWrite + 1) % _samples.Length;
        _sampleCount++;
    }

    private void UpdateStatus() => _io[0x26] = (byte)(0x80 | (_channel1Enabled ? 0x01 : 0) | (_channel2Enabled ? 0x02 : 0) | (_channel3Enabled ? 0x04 : 0) | (_channel4Enabled ? 0x08 : 0));
}
