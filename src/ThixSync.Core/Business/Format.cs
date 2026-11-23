using System.Globalization;

namespace ThixSync.Core.Business;

/// <summary>
/// Numbers, sizes, speeds and durations the German way ("1.204", "3,2 GB", "48,2 MB/s", "2 Min. 10 Sek."), the same in the app,
/// the tray, the protocol and the CLI. Independent of the culture of the computer.
/// </summary>
public static class Format
{
    private static readonly NumberFormatInfo German = new()
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = ".",
        NumberGroupSizes = [3],
        NegativeSign = "-",
    };

    private static readonly string[] Units = ["KB", "MB", "GB", "TB", "PB", "EB"];

    /// <summary>A size: "0 Bytes", "1 Byte", "512 Bytes", "12,3 KB", "3,2 GB", "512 GB" (1 KB = 1024 Bytes).</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 0)
        {
            return "-" + Bytes(bytes == long.MinValue ? long.MaxValue : -bytes);
        }

        if (bytes < 1024)
        {
            return bytes == 1 ? "1 Byte" : $"{Count(bytes)} Bytes";
        }

        double value = bytes;
        var unit = -1;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // 1023,96 KB would round to "1024 KB": show the next unit instead
        if (value >= 1023.95 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value.ToString(value < 100 ? "0.0" : "0", German)} {Units[unit]}";
    }

    /// <summary>A speed: "48,2 MB/s", "512 Bytes/s".</summary>
    public static string Speed(double bytesPerSecond) =>
        double.IsFinite(bytesPerSecond) && bytesPerSecond > 0 ? $"{Bytes((long)Math.Round(bytesPerSecond))}/s" : "0 Bytes/s";

    /// <summary>A duration: "45 Sek.", "2 Min. 10 Sek.", "1 Std. 5 Min.", "2 Tage 3 Std.".</summary>
    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        var seconds = (long)Math.Round(duration.TotalSeconds);
        if (seconds < 60)
        {
            return $"{seconds} Sek.";
        }

        var minutes = seconds / 60;
        if (minutes < 60)
        {
            var restSeconds = seconds % 60;
            return restSeconds == 0 ? $"{minutes} Min." : $"{minutes} Min. {restSeconds} Sek.";
        }

        var hours = minutes / 60;
        if (hours < 24)
        {
            var restMinutes = minutes % 60;
            return restMinutes == 0 ? $"{hours} Std." : $"{hours} Std. {restMinutes} Min.";
        }

        var days = hours / 24;
        var restHours = hours % 24;
        var dayText = days == 1 ? "1 Tag" : $"{Count(days)} Tage";
        return restHours == 0 ? dayText : $"{dayText} {restHours} Std.";
    }

    /// <summary>A count with thousands separators: "1.204".</summary>
    public static string Count(long count) => count.ToString("#,0", German);

    /// <summary>A rate with one decimal below 10 ("2,5"), whole numbers above ("310").</summary>
    public static string Rate(double perSecond) =>
        !double.IsFinite(perSecond) || perSecond <= 0 ? "0" : perSecond < 10 ? perSecond.ToString("0.0", German) : Count((long)Math.Round(perSecond));

    /// <summary>"1 Datei" / "12 Dateien" (any noun with its plural).</summary>
    public static string Files(long count) => count == 1 ? "1 Datei" : $"{Count(count)} Dateien";
}
