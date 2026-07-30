using System.Runtime.InteropServices;

namespace Craterboy.Core.Tests;

internal sealed class SameBoyOracle : IDisposable
{
    private const string Library = "craterboy-oracle";
    private IntPtr _instance;

    public SameBoyOracle(GameBoyModel model, byte[] rom)
    {
        _instance = Native.Create((int)model, rom, (nuint)rom.Length);
        if (_instance == IntPtr.Zero)
            throw new InvalidOperationException("The SameBoy oracle could not be created.");
    }

    public static string Baseline => Marshal.PtrToStringUTF8(Native.Baseline())!;
    public int NativeModel => Native.Model(Instance);
    public byte Read(ushort address) => Native.Read(Instance, address);
    public void Write(ushort address, byte value) => Native.Write(Instance, address, value);
    public uint StepInstruction() => Native.Step(Instance);

    public OracleRegisters Registers
    {
        get
        {
            Native.GetRegisters(Instance, out var registers);
            return registers;
        }
    }

    public void Dispose()
    {
        if (_instance == IntPtr.Zero) return;
        Native.Destroy(_instance);
        _instance = IntPtr.Zero;
    }

    private IntPtr Instance => _instance != IntPtr.Zero
        ? _instance
        : throw new ObjectDisposedException(nameof(SameBoyOracle));

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct OracleRegisters
    {
        public readonly byte A, F, B, C, D, E, H, L;
        public readonly ushort StackPointer, ProgramCounter;
    }

    private static class Native
    {
        [DllImport(Library, EntryPoint = "cb_oracle_baseline")]
        internal static extern IntPtr Baseline();

        [DllImport(Library, EntryPoint = "cb_oracle_create")]
        internal static extern IntPtr Create(int model, byte[] rom, nuint romSize);

        [DllImport(Library, EntryPoint = "cb_oracle_destroy")]
        internal static extern void Destroy(IntPtr instance);

        [DllImport(Library, EntryPoint = "cb_oracle_model")]
        internal static extern int Model(IntPtr instance);

        [DllImport(Library, EntryPoint = "cb_oracle_get_registers")]
        internal static extern void GetRegisters(IntPtr instance, out OracleRegisters registers);

        [DllImport(Library, EntryPoint = "cb_oracle_read")]
        internal static extern byte Read(IntPtr instance, ushort address);

        [DllImport(Library, EntryPoint = "cb_oracle_write")]
        internal static extern void Write(IntPtr instance, ushort address, byte value);

        [DllImport(Library, EntryPoint = "cb_oracle_step")]
        internal static extern uint Step(IntPtr instance);
    }
}
