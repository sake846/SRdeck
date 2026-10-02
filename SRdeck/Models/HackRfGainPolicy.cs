namespace SRdeck.Models;

internal readonly record struct HackRfGainStages(uint LnaGainDb, uint VgaGainDb);

internal static class HackRfGainPolicy
{
    public static HackRfGainStages Resolve(int masterGainPercent)
    {
        double ratio = Math.Clamp(masterGainPercent, 0, 100) / 100.0;
        int lnaGainDb = (int)Math.Round(ratio * 40.0 / 8.0, MidpointRounding.AwayFromZero) * 8;
        int vgaGainDb = (int)Math.Round(ratio * 62.0 / 2.0, MidpointRounding.AwayFromZero) * 2;
        return new((uint)Math.Clamp(lnaGainDb, 0, 40), (uint)Math.Clamp(vgaGainDb, 0, 62));
    }
}
