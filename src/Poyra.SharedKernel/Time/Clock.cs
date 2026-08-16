namespace Poyra.SharedKernel.Time;

/// <summary>
/// Tek zaman kaynağı (İlke 2: sunucu otoritesi). Yasal zaman damgaları yalnız buradan;
/// istemci/cihaz saati ancak *_device beyan kolonlarına yazılabilir.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
