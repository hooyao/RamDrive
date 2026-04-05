using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using RamDrive.Core.Configuration;
using WinFsp;

namespace RamDrive.Cli;

[SupportedOSPlatform("windows")]
internal sealed class WinFspHostedService : BackgroundService
{
    private readonly WinFspRamAdapter _adapter;
    private readonly RamDriveOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<WinFspHostedService> _logger;

    private FileSystemHost? _host;

    public WinFspHostedService(
        WinFspRamAdapter adapter,
        IOptions<RamDriveOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<WinFspHostedService> logger)
    {
        _adapter = adapter;
        _options = options.Value;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("RamDrive starting: mount={MountPoint} capacity={CapacityMb}MB pageSize={PageSizeKb}KB",
                _options.MountPoint, _options.CapacityMb, _options.PageSizeKb);

            _host = new FileSystemHost(_adapter);

            // Ensure WinFsp uses Mount Manager from kernel driver (no admin needed for mount).
            // WinFsp reads this once from HKLM\SOFTWARE\WOW6432Node\WinFsp on x64.
            EnsureMountMgrFromFSD();

            // WinFsp mount point format:
            //   "X:"     → DefineDosDevice (no admin required, invisible to disk benchmark tools)
            //   "\\.\X:" → Mount Manager (visible to all apps including ATTO, requires admin
            //               OR registry key MountUseMountmgrFromFSD=1)
            //
            // Strategy: try Mount Manager first, fallback to DefineDosDevice if it fails.
            string driveLetter = _options.MountPoint.TrimEnd('\\');
            string mountManagerPoint = @"\\.\" + driveLetter;

            int result = _host.Mount(mountManagerPoint);
            if (result >= 0)
            {
                _logger.LogInformation("Drive mounted at {MountPoint} via Mount Manager (visible to all apps).",
                    _options.MountPoint);
            }
            else
            {
                _logger.LogWarning(
                    "Mount Manager mount failed (0x{Status:X8}). Falling back to DefineDosDevice. " +
                    "The drive will work but may be invisible to disk benchmark tools (e.g. ATTO). " +
                    "To fix, run RamDrive.exe once as administrator — it will auto-configure and work without admin afterwards.",
                    result);

                // FileSystemHost is not reusable after a failed mount — create a new one
                _host.Dispose();
                _host = new FileSystemHost(_adapter);

                result = _host.Mount(driveLetter);
                if (result < 0)
                {
                    _logger.LogError("WinFsp mount failed: 0x{Status:X8}", result);
                    _lifetime.StopApplication();
                    return;
                }

                _logger.LogInformation("Drive mounted at {MountPoint} via DefineDosDevice.", _options.MountPoint);
            }

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (DllNotFoundException)
        {
            _logger.LogError("WinFsp is not installed. Install from: https://winfsp.dev/rel/");
            _lifetime.StopApplication();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Mount cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during mount");
            _lifetime.StopApplication();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Unmounting drive...");

        // Signal cancellation to ExecuteAsync first (BackgroundService contract)
        await base.StopAsync(cancellationToken);

        try
        {
            _host?.Dispose();
            _host = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during unmount");
        }

        _logger.LogInformation("Drive unmounted");
    }

    /// <summary>
    /// Ensures the WinFsp registry key MountUseMountmgrFromFSD=1 is set so that
    /// the WinFsp kernel driver handles Mount Manager registration (no admin needed for mount).
    /// On x64 systems, WinFsp reads from HKLM\SOFTWARE\WOW6432Node\WinFsp.
    /// Writing to HKLM requires elevation — if we don't have it, we silently skip.
    /// </summary>
    private void EnsureMountMgrFromFSD()
    {
        const string keyPath = @"SOFTWARE\WOW6432Node\WinFsp";
        const string valueName = "MountUseMountmgrFromFSD";

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: false);
            if (key?.GetValue(valueName) is int val && val != 0)
                return; // already set

            // Try to write it — requires admin
            using var writeKey = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
            if (writeKey != null)
            {
                writeKey.SetValue(valueName, 1, RegistryValueKind.DWord);
                _logger.LogInformation("Set WinFsp MountUseMountmgrFromFSD=1 in registry. " +
                    "Mount Manager will be available from kernel driver on next launch.");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // No admin — can't write. The fallback logic in ExecuteAsync will handle it.
        }
    }
}
