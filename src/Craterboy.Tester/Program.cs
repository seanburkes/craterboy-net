using Craterboy;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Usage: craterboy-tester <rom> [--cycles <count>]");
    return 0;
}

var cycles = 0;
if (args.Length == 3 && args[1] == "--cycles" &&
    (!int.TryParse(args[2], out cycles) || cycles < 0))
{
    Console.Error.WriteLine("Cycle count must be a non-negative integer.");
    return 2;
}

try
{
    var emulator = new Emulator(GameBoyModel.DmgB);
    using var rom = File.OpenRead(args[0]);
    emulator.LoadRom(rom);
    Console.WriteLine($"{emulator.RomHeader!.Title} | type 0x{emulator.RomHeader.CartridgeType:X2} | {emulator.RomHeader.RomSize} bytes");
    if (cycles != 0)
    {
        emulator.RunCycles(cycles);
        var r = emulator.Registers;
        Console.WriteLine($"cycles={emulator.CycleCount} PC={r.ProgramCounter:X4} AF={r.A:X2}{r.F:X2} BC={r.B:X2}{r.C:X2}");
    }
    return 0;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
