using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;
using WinFsp.Native;

namespace RamDrive.Cli.Diag;

[SupportedOSPlatform("windows")]
internal sealed class DiagHostedService : BackgroundService
{
    private readonly IFileSystem _fs;
    private readonly RamDriveOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<DiagHostedService> _logger;
    private FileSystemHost? _host;

    public DiagHostedService(IFileSystem fs, IOptions<RamDriveOptions> options,
        IHostApplicationLifetime lifetime, ILogger<DiagHostedService> logger)
    {
        _fs = fs; _options = options.Value; _lifetime = lifetime; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _host = new FileSystemHost(_fs);
            string drive = _options.MountPoint.TrimEnd('\\');
            int r = _host.Mount(@"\\.\" + drive);
            if (r < 0)
            {
                _host.Dispose();
                _host = new FileSystemHost(_fs);
                r = _host.Mount(drive);
            }
            if (r < 0)
            {
                _logger.LogError("Mount failed: 0x{Status:X8}", r);
                Environment.ExitCode = 1;
                _lifetime.StopApplication();
                return;
            }
            _logger.LogInformation("Diag drive mounted at {MountPoint} (Differential mode)", _options.MountPoint);
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mount error");
            Environment.ExitCode = 1;
            _lifetime.StopApplication();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try { _host?.Dispose(); _host = null; } catch { }
    }
}
