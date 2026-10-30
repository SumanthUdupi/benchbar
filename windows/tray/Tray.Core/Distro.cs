using System.Text;
using Microsoft.Win32;

namespace BenchBar.Tray.Core;

/// <summary>Whether a WSL distro is running now. It must never start one.</summary>
public interface IDistroProbe
{
    Task<bool> IsRunningAsync(string distro, CancellationToken ct);
}

/// <summary>
/// <c>wsl.exe --list --running --quiet</c>: lists the running distros without booting any.
/// Its output is UTF-16LE; names are compared without regard to case.
/// </summary>
public sealed class WslDistroProbe(IProcessRunner? runner = null) : IDistroProbe
{
    private readonly IProcessRunner _runner = runner ?? new SystemProcessRunner();

    public async Task<bool> IsRunningAsync(string distro, CancellationToken ct)
    {
        // a missing wsl.exe throws; the poller then asks the shim, which says so with its own fix line
        var result = await _runner.RunAsync("wsl.exe", ["--list", "--running", "--quiet"], ct, TimeSpan.FromSeconds(10), Encoding.Unicode)
            .ConfigureAwait(false);
        return ParseRunning(result.Output).Contains(distro.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The names in the output, with the BOM, NULs and carriage returns removed and blank lines dropped.</summary>
    public static IReadOnlyList<string> ParseRunning(string output)
    {
        var text = output.Replace("\0", "").Replace("﻿", "").Replace("\r", "");
        return text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }
}

/// <summary>
/// The check before any shim call that is not a poll: a snapshot can be minutes old and WSL idles a distro out, so the
/// answer is asked fresh, and a call into the shim is never made for a distro that is not running (that would boot it).
/// </summary>
public static class DistroGuard
{
    public const string StoppedMessage = "The WSL distro is stopped. Start it from the BenchBar flyout first.";

    /// <summary>
    /// Null when the shim may be called: the distro runs, or there is no probe or no distro to ask about. Otherwise the line to show.
    /// A probe that fails answers with the poller's own line. A cancelled <paramref name="ct"/> throws.
    /// </summary>
    public static async Task<string?> CheckAsync(IDistroProbe? probe, string? distro, CancellationToken ct)
    {
        if (probe is null || string.IsNullOrWhiteSpace(distro)) return null;
        try
        {
            return await probe.IsRunningAsync(distro.Trim(), ct).ConfigureAwait(false) ? null : StoppedMessage;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ex is System.ComponentModel.Win32Exception ? BenchPoller.WslMissingMessage : BenchPoller.WslSilentMessage;
        }
    }
}

/// <summary>
/// The distro the tray watches: <c>distro</c> in config.json (the shim's key), else the WSL default distro.
/// </summary>
public static class DistroName
{
    private const string LxssKey = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    public static string? Resolve(string? configured, Func<string?>? defaultDistro = null)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
        return (defaultDistro ?? FromRegistry)();
    }

    /// <summary>HKCU Lxss: <c>DefaultDistribution</c> is a GUID; its subkey holds <c>DistributionName</c>.</summary>
    public static string? FromRegistry()
    {
        try
        {
            using var lxss = Registry.CurrentUser.OpenSubKey(LxssKey);
            if (lxss?.GetValue("DefaultDistribution") is not string guid || guid.Length == 0) return null;
            using var distro = lxss.OpenSubKey(guid);
            return distro?.GetValue("DistributionName") as string;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
