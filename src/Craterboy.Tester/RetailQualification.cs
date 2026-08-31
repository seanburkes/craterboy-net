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
    bool TenMinuteDurationCompleted,
    string Outcome,
    string? ErrorType,
    string? ErrorMessage,
    int FrameChanges,
    long AudioFrames,
    bool AudioNonSilent,
    bool BatteryDirtyObserved,
    int BatteryBytes,
    bool BatteryRoundTrip,
    bool RepeatedLoadStable,
    bool ResetStable,
    int InputEvents,
    int AppliedInputEvents,
    int FrameChangesAfterInput,
    bool InputChangedFinalFrame,
    long? FirstInputFrameDivergenceCycle,
    IReadOnlyList<QualificationCheckpoint> Checkpoints);

public static class RetailQualification
{
    public const long HardwareCyclesPerSecond = 4_194_304;
    public const long TenMinuteCycles = HardwareCyclesPerSecond * 60 * 10;

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
        var repeatedLoadStable = false;
        var resetStable = false;
        var appliedInputEvents = 0;
        var frameChangesAfterInput = 0;
        var inputChangedFinalFrame = false;
        long? firstInputFrameDivergenceCycle = null;
        long completedCycles = 0;

        try
        {
            var options = DeterministicOptions();
            emulator = new Emulator(model, options);
            emulator.LoadRom(rom);
            var initialHash = Convert.ToHexString(emulator.ComputeStateHash());
            emulator.RawFrame.CopyTo(previousFrame);
            var events = recording?.Events ?? [];
            var eventIndex = 0;
            Emulator? noInput = null;
            if (events.Count != 0 && events[0].Cycle <= cycles)
            {
                noInput = new Emulator(model, options);
                noInput.LoadRom(rom);
            }
            long nextCheckpoint = Math.Min(cycles, checkpointCycles);

            while (emulator.CycleCount < cycles)
            {
                var target = nextCheckpoint;
                while (eventIndex < events.Count && events[eventIndex].Cycle <= target)
                {
                    RunTo(emulator, events[eventIndex].Cycle);
                    var input = events[eventIndex++];
                    emulator.SetButtonState(input.Button, input.Pressed, input.Player);
                    appliedInputEvents++;
                }
                RunTo(emulator, target);
                if (noInput is not null)
                {
                    RunTo(noInput, target);
                    if (appliedInputEvents != 0 &&
                        !emulator.RawFrame.SequenceEqual(noInput.RawFrame))
                        firstInputFrameDivergenceCycle ??= target;
                }
                batteryDirty |= emulator.BatteryDirty;
                checkpoints.Add(new(emulator.CycleCount, Convert.ToHexString(emulator.ComputeStateHash())));
                if (!emulator.RawFrame.SequenceEqual(previousFrame))
                {
                    frameChanges++;
                    if (appliedInputEvents != 0) frameChangesAfterInput++;
                    emulator.RawFrame.CopyTo(previousFrame);
                }
                var copied = emulator.CopyAudioFrames(audio);
                audioFrames += copied;
                audioNonSilent |= audio.AsSpan(0, copied * 2).ContainsAnyExcept((short)0);
                if (target == cycles) break;
                nextCheckpoint = Math.Min(cycles, checked(nextCheckpoint + checkpointCycles));
            }
            completedCycles = emulator.CycleCount;

            if (appliedInputEvents != 0 && noInput is not null)
                inputChangedFinalFrame = !emulator.RawFrame.SequenceEqual(noInput.RawFrame);

            var battery = emulator.SaveBattery();
            batteryBytes = battery.Length;
            var restored = new Emulator(model, options);
            restored.LoadRom(rom);
            restored.LoadBattery(battery);
            batteryRoundTrip = restored.SaveBattery().AsSpan().SequenceEqual(battery);

            var stabilityCycle = checkpoints.Count == 0 ? 0 : checkpoints[0].Cycle;
            var expectedHash = checkpoints.Count == 0 ? initialHash : checkpoints[0].StateSha256;
            emulator.LoadRom(rom);
            ReplayTo(emulator, events, stabilityCycle);
            repeatedLoadStable = Convert.ToHexString(emulator.ComputeStateHash()) == expectedHash;
            var resetBattery = emulator.SaveBattery();
            emulator.Reset();
            ReplayTo(emulator, events, stabilityCycle);
            var resetReference = new Emulator(model, options);
            resetReference.LoadRom(rom);
            resetReference.LoadBattery(resetBattery);
            ReplayTo(resetReference, events, stabilityCycle);
            resetStable = emulator.ComputeStateHash().AsSpan().SequenceEqual(resetReference.ComputeStateHash());
        }
        catch (Exception exception)
        {
            if (completedCycles == 0) completedCycles = emulator?.CycleCount ?? 0;
            outcome = "failed";
            errorType = exception.GetType().Name;
            errorMessage = exception.Message;
        }

        return new(
            Convert.ToHexString(SHA256.HashData(rom.Span)), header.Title, header.CartridgeType,
            header.RomSize, header.RamSize, header.SupportsColor, model.ToString(), cycles,
            completedCycles, completedCycles >= TenMinuteCycles, outcome, errorType, errorMessage, frameChanges,
            audioFrames, audioNonSilent, batteryDirty, batteryBytes, batteryRoundTrip,
            repeatedLoadStable, resetStable,
            recording?.Events.Count ?? 0, appliedInputEvents, frameChangesAfterInput,
            inputChangedFinalFrame, firstInputFrameDivergenceCycle, checkpoints);
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

    private static void ReplayTo(Emulator emulator, IReadOnlyList<InputEvent> events, long target)
    {
        foreach (var input in events)
        {
            if (input.Cycle > target) break;
            RunTo(emulator, input.Cycle);
            emulator.SetButtonState(input.Button, input.Pressed, input.Player);
        }
        RunTo(emulator, target);
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
