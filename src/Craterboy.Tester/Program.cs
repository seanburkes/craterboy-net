using System.Text.Json;
using Craterboy;
using Craterboy.Tester;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Usage:\n  craterboy-tester <rom> [--cycles <count>]\n  craterboy-tester qualify <rom> [--cycles <count>] [--checkpoint-cycles <count>] [--recording <path>] [--output <path>]");
    return 0;
}

if (args[0] == "qualify")
    return Qualify(args[1..]);

return RunLegacy(args);

static int Qualify(string[] arguments)
{
    if (arguments.Length == 0) return UsageError("A ROM path is required.");
    var romPath = arguments[0];
    long cycles = 70224 * 60L;
    var checkpointCycles = 70224;
    string? recordingPath = null;
    string? outputPath = null;
    for (var index = 1; index < arguments.Length; index += 2)
    {
        if (index + 1 >= arguments.Length) return UsageError($"Missing value for {arguments[index]}.");
        var value = arguments[index + 1];
        switch (arguments[index])
        {
            case "--cycles" when long.TryParse(value, out var parsedCycles) && parsedCycles >= 0:
                cycles = parsedCycles;
                break;
            case "--checkpoint-cycles" when int.TryParse(value, out var parsedCheckpoint) && parsedCheckpoint > 0:
                checkpointCycles = parsedCheckpoint;
                break;
            case "--recording": recordingPath = value; break;
            case "--output": outputPath = value; break;
            default: return UsageError($"Invalid option or value: {arguments[index]} {value}");
        }
    }

    try
    {
        var rom = File.ReadAllBytes(romPath);
        InputRecording? recording = null;
        if (recordingPath is not null)
        {
            using var source = File.OpenRead(recordingPath);
            recording = InputRecording.Read(source);
        }
        var report = RetailQualification.Run(rom, cycles, checkpointCycles, recording: recording);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (outputPath is null) Console.WriteLine(json);
        else File.WriteAllText(outputPath, json + Environment.NewLine);
        return report.Outcome == "completed" ? 0 : 1;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}

static int RunLegacy(string[] arguments)
{
    var cycles = 0;
    if (arguments.Length == 3 && arguments[1] == "--cycles" &&
        (!int.TryParse(arguments[2], out cycles) || cycles < 0))
        return UsageError("Cycle count must be a non-negative integer.");
    try
    {
        var emulator = new Emulator(GameBoyModel.DmgB);
        using var rom = File.OpenRead(arguments[0]);
        emulator.LoadRom(rom);
        Console.WriteLine($"{emulator.RomHeader!.Title} | type 0x{emulator.RomHeader.CartridgeType:X2} | {emulator.RomHeader.RomSize} bytes");
        if (cycles != 0)
        {
            emulator.RunCycles(cycles);
            var registers = emulator.Registers;
            Console.WriteLine($"cycles={emulator.CycleCount} PC={registers.ProgramCounter:X4} AF={registers.A:X2}{registers.F:X2} BC={registers.B:X2}{registers.C:X2}");
        }
        return 0;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}

static int UsageError(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}
