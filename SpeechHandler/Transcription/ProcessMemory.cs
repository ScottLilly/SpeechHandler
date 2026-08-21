using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SpeechHandler.Transcription;

internal static class ProcessMemory
{
    private const long Megabyte = 1024L * 1024;
    private const long Gigabyte = 1024L * Megabyte;

    public static long PrivateBytes()
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.PrivateMemorySize64;
    }

    public static bool TryReadPhysicalMemory(out long totalBytes, out long availableBytes)
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            totalBytes = 0;
            availableBytes = 0;
            return false;
        }

        totalBytes = (long)status.ullTotalPhys;
        availableBytes = (long)status.ullAvailPhys;
        return true;
    }

    public static long TotalPhysicalBytes =>
        TryReadPhysicalMemory(out var total, out _) ? total : 0;

    public static long AvailablePhysicalBytes =>
        TryReadPhysicalMemory(out _, out var available) ? available : 0;

    public static long AutoBudgetBytes()
    {
        var physical = TotalPhysicalBytes;
        if (physical <= 0)
        {
            return 2 * Gigabyte;
        }

        if (physical <= 8 * Gigabyte)
        {
            return Math.Max(512 * Megabyte, physical * 25 / 100);
        }

        var fortyPercent = physical * 40 / 100;
        var afterReserve = physical - (2 * Gigabyte);
        return Math.Max(512 * Megabyte, Math.Min(fortyPercent, afterReserve));
    }

    public static string AutoBudgetReason()
    {
        var physical = TotalPhysicalBytes;
        if (physical <= 0)
        {
            return "a share of this computer's RAM, leaving room for Windows";
        }

        if (physical <= 8 * Gigabyte)
        {
            return "25% of RAM on this computer, leaving room for Windows and other apps";
        }

        return "40% of RAM, keeping at least 2 GB for Windows and other apps";
    }

    public static int MinBudgetGigabytes { get; } = 1;

    public static int MaxBudgetGigabytes()
    {
        var physical = TotalPhysicalBytes;
        if (physical <= 0)
        {
            return 8;
        }

        var physicalGb = Math.Max(1, (int)(physical / Gigabyte));
        var reserveGb = physical <= 8 * Gigabyte
            ? Math.Max(1, (int)Math.Ceiling(physicalGb * 0.25))
            : 2;
        return Math.Max(MinBudgetGigabytes, physicalGb - reserveGb);
    }

    public static int SuggestedBudgetGigabytes()
    {
        var suggested = (int)Math.Round(AutoBudgetBytes() / (double)Gigabyte);
        return ClampBudgetGigabytes(Math.Max(MinBudgetGigabytes, suggested));
    }

    public static int ClampBudgetGigabytes(int gigabytes) =>
        Math.Clamp(gigabytes, MinBudgetGigabytes, MaxBudgetGigabytes());

    public static long BytesFromGigabytes(int gigabytes) =>
        (long)ClampBudgetGigabytes(gigabytes) * Gigabyte;

    public static string FormatBytes(long bytes)
    {
        if (bytes >= Gigabyte)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:0.#} GB", bytes / (double)Gigabyte);
        }

        if (bytes >= Megabyte)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:0} MB", Math.Max(1, bytes / (double)Megabyte));
        }

        if (bytes >= 1024)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:0} KB", bytes / 1024d);
        }

        return $"{Math.Max(0, bytes)} B";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}
