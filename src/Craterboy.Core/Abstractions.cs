namespace Craterboy;

public enum GameBoyFrameFormat
{
    MonochromeShade,
    Rgb15,
}

public interface ITimeProvider
{
    DateTimeOffset UtcNow { get; }
}

public interface IEntropyProvider
{
    void Fill(Span<byte> destination);
}

public interface ISerialEndpoint
{
    byte Exchange(byte outgoing);
}

public sealed class EmulatorOptions
{
    public ITimeProvider TimeProvider { get; init; } = SystemEmulationTime.Instance;
    public IEntropyProvider EntropyProvider { get; init; } = SystemEntropy.Instance;
    public ISerialEndpoint? SerialEndpoint { get; init; }
    public bool SkipBootRom { get; init; } = true;
    public bool EmulateJoypadBouncing { get; init; }
}

internal sealed class SystemEmulationTime : ITimeProvider
{
    public static SystemEmulationTime Instance { get; } = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

internal sealed class SystemEntropy : IEntropyProvider
{
    public static SystemEntropy Instance { get; } = new();
    public void Fill(Span<byte> destination) => System.Security.Cryptography.RandomNumberGenerator.Fill(destination);
}
