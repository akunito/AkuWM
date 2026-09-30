namespace AkuWM.Core.Nodes;

/// <summary>Colour levels shared by every panel: "ok", "warn", "err", or "" for unknown. Thresholds as sway-apps has them.</summary>
public static class Levels
{
    public const double Day = 86400.0;
    public const double Hour = 3600.0;
    public const double PctWarn = 60.0;
    public const double PctErr = 85.0;
    public const double LoadWarn = 70.0;
    public const double LoadErr = 100.0;
    public const double BackupWarnS = 3 * Day;
    public const double BackupErrS = 7 * Day;
    public const double HourlyWarnS = 3 * Hour;
    public const double HourlyErrS = 24 * Hour;
    public const double RttWarnMs = 80.0;
    public const double RttErrMs = 200.0;
    public const double HttpWarnS = 1.0;
    public const double HttpErrS = 3.0;

    private static string Band(double? v, double warn, double err) =>
        v is null ? string.Empty : v >= err ? "err" : v >= warn ? "warn" : "ok";

    public static string Pct(double? v) => Band(v, PctWarn, PctErr);

    public static string Load(double? v) => Band(v, LoadWarn, LoadErr);

    public static string Age(double? seconds, bool hourly = false) =>
        hourly ? Band(seconds, HourlyWarnS, HourlyErrS) : Band(seconds, BackupWarnS, BackupErrS);

    public static string Rtt(double? ms) => Band(ms, RttWarnMs, RttErrMs);

    public static string Http(double? seconds) => Band(seconds, HttpWarnS, HttpErrS);

    public static string Flag(bool? ok, string bad = "err") => ok is null ? string.Empty : ok.Value ? "ok" : bad;

    private static int Rank(string level) => level switch { "ok" => 1, "warn" => 2, "err" => 3, _ => 0 };

    public static string Worst(params string[] levels)
    {
        string worst = string.Empty;
        foreach (string level in levels)
        {
            if (Rank(level) > Rank(worst))
            {
                worst = level;
            }
        }

        return worst;
    }

    public static string PctText(double? v) => v is null ? "—" : $"{Math.Round(v.Value):0}%";

    public static double? ParsePct(string? text)
    {
        if (text is null)
        {
            return null;
        }

        return double.TryParse(text.Trim().TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null;
    }
}
