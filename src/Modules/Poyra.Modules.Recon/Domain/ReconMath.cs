namespace Poyra.Modules.Recon.Domain;

public static class ReconMath
{

    public static long ExpectedCommission(long grossMinor, int rateBps)
        => (long)Math.Round(grossMinor * (rateBps / 10_000m), 0, MidpointRounding.ToEven);
}
