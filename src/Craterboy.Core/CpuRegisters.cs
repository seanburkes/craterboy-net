namespace Craterboy;

[Flags]
public enum CpuFlags : byte
{
    Carry = 0x10,
    HalfCarry = 0x20,
    Subtract = 0x40,
    Zero = 0x80,
}

public readonly record struct CpuRegisterSnapshot(
    byte A, byte F, byte B, byte C, byte D, byte E, byte H, byte L,
    ushort StackPointer, ushort ProgramCounter, bool InterruptMasterEnable,
    bool Halted);

internal sealed class CpuState
{
    public byte A, F, B, C, D, E, H, L;
    public ushort SP, PC;
    public bool Ime, Halted;

    public CpuRegisterSnapshot Snapshot => new(
        A, F, B, C, D, E, H, L, SP, PC, Ime, Halted);

    public ushort BC { get => (ushort)((B << 8) | C); set { B = (byte)(value >> 8); C = (byte)value; } }
    public ushort DE { get => (ushort)((D << 8) | E); set { D = (byte)(value >> 8); E = (byte)value; } }
    public ushort HL { get => (ushort)((H << 8) | L); set { H = (byte)(value >> 8); L = (byte)value; } }
}
