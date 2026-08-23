using System.Security.Cryptography;
using Craterboy;

namespace Craterboy.Tester;

public sealed record QualificationCheckpoint(long Cycle, string StateSha256);

public sealed record RetailQualificationReport(
    string RomSha256,
    string Title,
    byte CartridgeType,
    int RomSize,
    int RamSize,
    bool SupportsColor,
    string Model,
    long RequestedCycles,
    long CompletedCycles,
    string Outcome,
    string? ErrorType,
    string? ErrorMessage,
    int FrameChanges,
    long AudioFrames,
    bool AudioNonSilent,
    bool BatteryDirtyObserved,
    int BatteryBytes,
    bool BatteryRoundTrip,
    int InputEvents,
    IReadOnlyList<QualificationCheckpoint> Checkpoints);

public static class RetailQualification
{
    public static RetailQualificationReport Run(
        ReadOnlyMemory<byte> rom, long cycles, int checkpointCycles,
        GameBoyModel? requestedModel = null, InputRecording? recording = null)
    {
        if (cycles < 0) throw new ArgumentOutOfRangeException(nameof(cycles));
        if (checkpointCycles <= 0) throw new ArgumentOutOfRangeException(nameof(checkpointCycles));

        var header = RomHeader.Parse(rom.Span);
        var model = requestedModel ?? (header.SupportsColor ? GameBoyModel.CgbE : GameBoyModel.DmgB);
        var checkpoints = new List<QualificationCheckpoint>();
        var frameChanges = 0;
        long audioFrames = 0;
        var audioNonSilent = false;
        var batteryDirty = false;
        var previousFrame = new ushort[160 * 144];
        var audio = new short[8192];
        Emulator? emulator = null;
        string outcome = "completed";
        string? errorType = null;
        string? errorMessage = null;
        var batteryBytes = 0;
        var batteryRoundTrip = false;

        try
        {
            var options = DeterministicOptions();
            emulator = new Emulator(model, options);
            emulator.LoadRom(rom);
            emulator.RawFrame.CopyTo(previousFrame);
            var events = recording?.Events ?? [];
            var eventIndex = 0;
            long nextCheckpoint = Math.Min(cycles, checkpointCycles);

            while (emulator.CycleCount < cycles)
            {
                var target = nextCheckpoint;
                while (eventIndex < events.Count && events[eventIndex].Cycle <= target)
                {
                    RunTo(emulator, events[eventIndex].Cycle);
                    var input = events[eventIndex++];
                    emulator.SetButtonState(input.Button, input.Pressed, input.Player);
                }
                RunTo(emulator, target);
                batteryDirty |= emulator.BatteryDirty;
                checkpoints.Add(new(emulator.CycleCount, Convert.ToHexString(emulator.ComputeStateHash())));
                if (!emulator.RawFrame.SequenceEqual(previousFrame))
                {
                    frameChanges++;
                    emulator.RawFrame.CopyTo(previousFrame);
                }
                var copied = emulator.CopyAudioFrames(audio);
                audioFrames += copied;
                audioNonSilent |= audio.AsSpan(0, copied * 2).ContainsAnyExcept((short)0);
                if (target == cycles) break;
                nextCheckpoint = Math.Min(cycles, checked(nextCheckpoint + checkpointCycles));
            }

            var battery = emulator.SaveBattery();
            batteryBytes = battery.Length;
            var restored = new Emulator(model, options);
            restored.LoadRom(rom);
            restored.LoadBattery(battery);
            batteryRoundTrip = restored.SaveBattery().AsSpan().SequenceEqual(battery);
        }
        catch (Exception exception)
        {
            outcome = "failed";
            errorType = exception.GetType().Name;
            errorMessage = exception.Message;
        }

        return new(
            Convert.ToHexString(SHA256.HashData(rom.Span)), header.Title, header.CartridgeType,
            header.RomSize, header.RamSize, header.SupportsColor, model.ToString(), cycles,
            emulator?.CycleCount ?? 0, outcome, errorType, errorMessage, frameChanges,
            audioFrames, audioNonSilent, batteryDirty, batteryBytes, batteryRoundTrip,
            recording?.Events.Count ?? 0, checkpoints);
    }

    private static EmulatorOptions DeterministicOptions() => new()
    {
        TimeProvider = new FixedTimeProvider(),
        EntropyProvider = new ZeroEntropyProvider(),
    };

    private static void RunTo(Emulator emulator, long target)
    {
        while (emulator.CycleCount < target)
            emulator.RunCycles((int)Math.Min(target - emulator.CycleCount, int.MaxValue));
    }

    private sealed class FixedTimeProvider : ITimeProvider
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }

    private sealed class ZeroEntropyProvider : IEntropyProvider
    {
        public void Fill(Span<byte> destination) => destination.Clear();
    }
}
