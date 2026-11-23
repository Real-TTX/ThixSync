using System.Globalization;
using ThixSync.Core.Infrastructure;

namespace ThixSync.Controls;

/// <summary>
/// Numbers, sizes, speeds and times the German way (de-DE) for every page: "3,2 GB", "48,2 MB/s", "1.204 Dateien",
/// "Heute, 10:24", "Gestern, 20:00". Times are local and measured against <see cref="AppClock"/> (pinned in the snapshots).
/// </summary>
public static class Formats
{
    /// <summary>The culture of every text of ThixSync.</summary>
    public static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>"0 B", "512 B", "12,3 KB", "4,6 MB", "123 GB", "1,2 TB" (1 KB = 1024 B, as Explorer and Finder count).</summary>
    public static string Bytes(long bytes)
    {
        double value = Math.Abs(bytes);
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var sign = bytes < 0 ? "−" : "";
        if (unit == 0)
        {
            return $"{sign}{value:0} B";
        }

        return sign + value.ToString(value >= 100 ? "0" : "0.0", German) + " " + Units[unit];
    }

    /// <summary>"48,2 MB/s".</summary>
    public static string Speed(double bytesPerSecond) => Bytes((long)Math.Max(0, bytesPerSecond)) + "/s";

    /// <summary>"37 Dateien/s", "2,6 Dateien/s".</summary>
    public static string FileSpeed(double filesPerSecond) =>
        (filesPerSecond >= 10 ? filesPerSecond.ToString("0", German) : Math.Max(0, filesPerSecond).ToString("0.0", German)) + " Dateien/s";

    /// <summary>"1.204" (German thousands).</summary>
    public static string Count(long count) => count.ToString("N0", German);

    /// <summary>"1 Datei", "1.204 Dateien".</summary>
    public static string Files(long count) => count == 1 ? "1 Datei" : $"{Count(count)} Dateien";

    /// <summary>"41 %".</summary>
    public static string Percent(double fraction) => Math.Clamp(fraction * 100, 0, 100).ToString("0", German) + " %";

    /// <summary>"45 s", "3 Min. 12 s", "1 Std. 4 Min.".</summary>
    public static string Duration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 60)
        {
            return $"{Math.Max(0, Math.Round(duration.TotalSeconds)):0} s";
        }

        return duration.TotalMinutes < 60
            ? $"{(int)duration.TotalMinutes} Min. {duration.Seconds} s"
            : $"{(int)duration.TotalHours} Std. {duration.Minutes} Min.";
    }

    /// <summary>"noch 2 Min." / "noch 40 s" / "wird berechnet …".</summary>
    public static string Remaining(TimeSpan? remaining) => remaining is { } left
        ? left.TotalSeconds < 60 ? $"noch {Math.Max(1, Math.Round(left.TotalSeconds)):0} s" : left.TotalMinutes < 60 ? $"noch {Math.Ceiling(left.TotalMinutes):0} Min." : $"noch {(int)left.TotalHours} Std. {left.Minutes} Min."
        : "wird berechnet …";

    /// <summary>"Heute, 10:24", "Gestern, 20:00", "Morgen, 20:00", "Mo., 16.11., 20:00" (this week), "12.11.2025, 09:14".</summary>
    public static string When(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        var today = AppClock.Today;
        var time = local.ToString("HH:mm", German);
        var days = (local.Date - today).Days;
        return days switch
        {
            0 => $"Heute, {time}",
            -1 => $"Gestern, {time}",
            1 => $"Morgen, {time}",
            > 1 and < 7 => $"{German.DateTimeFormat.GetAbbreviatedDayName(local.DayOfWeek).TrimEnd('.')}., {local.ToString("dd.MM.", German)}, {time}",
            _ => local.Year == today.Year ? $"{local.ToString("dd.MM.", German)}, {time}" : $"{local.ToString("dd.MM.yyyy", German)}, {time}",
        };
    }

    /// <summary><see cref="When"/> or "–".</summary>
    public static string When(DateTimeOffset? value) => value is { } moment ? When(moment) : "–";

    /// <summary>"vor 3 Min.", "vor 2 Std.", or <see cref="When"/> for anything older than a day.</summary>
    public static string Ago(DateTimeOffset value)
    {
        var age = AppClock.UtcNow - value.ToUniversalTime();
        if (age < TimeSpan.FromMinutes(1))
        {
            return "gerade eben";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"vor {(int)age.TotalMinutes} Min.";
        }

        return age < TimeSpan.FromHours(12) ? $"vor {(int)age.TotalHours} Std." : When(value);
    }
}
