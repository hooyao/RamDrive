using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using RamDrive.Core.Configuration;
using RamDrive.Core.FileSystem;
using WinFsp.Native;

namespace RamDrive.Cli;

[SupportedOSPlatform("windows")]
internal sealed class WinFspHostedService : BackgroundService
{
    private readonly WinFspRamAdapter _adapter;
    private readonly RamFileSystem _fs;
    private readonly RamDriveOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<WinFspHostedService> _logger;

    private FileSystemHost? _host;

    public WinFspHostedService(
        WinFspRamAdapter adapter,
        RamFileSystem fs,
        IOptions<RamDriveOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<WinFspHostedService> logger)
    {
        _adapter = adapter;
        _fs = fs;
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
                    Environment.ExitCode = 1;
                    _lifetime.StopApplication();
                    return;
                }

                _logger.LogInformation("Drive mounted at {MountPoint} via DefineDosDevice.", _options.MountPoint);
            }

            CreateInitialDirectories();

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (DllNotFoundException)
        {
            _logger.LogError("WinFsp is not installed. Install from: https://winfsp.dev/rel/");
            Environment.ExitCode = 1;
            _lifetime.StopApplication();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Mount cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during mount");
            Environment.ExitCode = 1;
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

    private void CreateInitialDirectories()
    {
        if (_options.InitialDirectories is not { Count: > 0 })
            return;

        var errors = new List<string>();
        ValidateDirectoryTree(_options.InitialDirectories, "", errors);
        if (errors.Count > 0)
        {
            _logger.LogError(
                "Invalid InitialDirectories configuration. Please fix appsettings.jsonc:{NewLine}{Errors}",
                Environment.NewLine,
                string.Join(Environment.NewLine, errors));
            return;
        }

        var count = CreateDirectoriesRecursive(@"\", _options.InitialDirectories);
        _logger.LogInformation("Created {Count} initial directories on RAM disk", count);
    }

    private static void ValidateDirectoryTree(DirectoryNode entries, string parentPath, List<string> errors)
    {
        foreach (var (name, children) in entries)
        {
            var displayPath = string.IsNullOrEmpty(parentPath) ? name : parentPath + @"\" + name;

            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add($"  - Empty directory name under \"{parentPath}\"");
                continue;
            }

            if (name.IndexOfAny(InvalidChars) >= 0)
            {
                errors.Add($"  - \"{displayPath}\": contains invalid character(s). Avoid < > : \" / \\ | ? *");
                continue;
            }

            if (IsReservedName(name))
            {
                errors.Add($"  - \"{displayPath}\": is a Windows reserved name (CON, PRN, NUL, etc.)");
                continue;
            }

            if (name.Length > 255)
            {
                errors.Add($"  - \"{displayPath}\": name exceeds 255 characters");
                continue;
            }

            if (children.Count > 0)
                ValidateDirectoryTree(children, displayPath, errors);
        }
    }

    private static readonly char[] InvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static bool IsReservedName(string name)
    {
        var upper = name.ToUpperInvariant();
        // Strip trailing dot/space — Windows treats "CON." and "CON " as reserved too
        upper = upper.TrimEnd('.', ' ');
        return upper is "CON" or "PRN" or "AUX" or "NUL"
            or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9"
            or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9";
    }

    private int CreateDirectoriesRecursive(string parentPath, DirectoryNode entries)
    {
        int count = 0;
        foreach (var (name, children) in entries)
        {
            var path = parentPath == @"\" ? @"\" + name : parentPath + @"\" + name;
            if (_fs.CreateDirectory(path) != null)
                count++;

            if (children.Count > 0)
                count += CreateDirectoriesRecursive(path, children);
        }

        return count;
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
