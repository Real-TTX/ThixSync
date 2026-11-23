using System.Diagnostics;
using System.Runtime.InteropServices;
using ThixSync.Core.Infrastructure;

namespace ThixSync.Core.Business.Engine;

/// <summary>Whether the computer runs on battery (best effort: Windows GetSystemPowerStatus, macOS <c>pmset -g batt</c>; cached for a minute).</summary>
internal static class PowerStatus
{
    private static readonly Lock Gate = new();
    private static long _checkedAt = long.MinValue;
    private static bool _onBattery;

    /// <summary>Tests: the answer instead of the system's.</summary>
    internal static Func<bool>? OnBatteryForTests { get; set; }

    public static bool IsOnBattery()
    {
        if (OnBatteryForTests is { } forTests)
        {
            return forTests();
        }

        lock (Gate)
        {
            var now = Environment.TickCount64;
            if (_checkedAt != long.MinValue && now - _checkedAt < 60_000)
            {
                return _onBattery;
            }

            _onBattery = Read();
            _checkedAt = now;
            return _onBattery;
        }
    }

    private static bool Read()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;
            }

            if (OperatingSystem.IsMacOS())
            {
                var start = new ProcessStartInfo("/usr/bin/pmset") { RedirectStandardOutput = true, UseShellExecute = false };
                start.ArgumentList.Add("-g");
                start.ArgumentList.Add("batt");
                using var process = Process.Start(start);
                if (process is null)
                {
                    return false;
                }

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                return output.Contains("'Battery Power'", StringComparison.Ordinal);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or DllNotFoundException
                                           or EntryPointNotFoundException)
        {
            Log.Warning($"The power status could not be read: {exception.Message}");
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
