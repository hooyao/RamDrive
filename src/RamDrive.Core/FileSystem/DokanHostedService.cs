using System.Runtime.Versioning;
using DokanNet;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;

namespace RamDrive.Core.FileSystem;

[SupportedOSPlatform("windows")]
public sealed class DokanHostedService : BackgroundService
{
    private readonly DokanRamAdapter _adapter;
    private readonly RamDriveOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<DokanHostedService> _logger;

    private Dokan? _dokan;
    private DokanInstance? _dokanInstance;

    public DokanHostedService(
        DokanRamAdapter adapter,
        IOptions<RamDriveOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<DokanHostedService> logger)
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

            _dokan = new Dokan(new DokanNetLogger(_logger));

            var dokanBuilder = new DokanInstanceBuilder(_dokan)
                .ConfigureOptions(options =>
                {
                    options.Options = DokanOptions.FixedDrive | DokanOptions.MountManager;
                    options.MountPoint = _options.MountPoint;
                });

            _dokanInstance = dokanBuilder.Build(_adapter);

            _logger.LogInformation("Drive mounted at {MountPoint}. Press Ctrl+C to unmount.", _options.MountPoint);

            await _dokanInstance.WaitForFileSystemClosedAsync(uint.MaxValue);
        }
        catch (DllNotFoundException)
        {
            _logger.LogError("Dokany driver is not installed. Install from: https://github.com/dokan-dev/dokany/releases");
            _lifetime.StopApplication();
        }
        catch (DokanException ex)
        {
            _logger.LogError(ex, "Dokan mount failed");
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

        try
        {
            _dokan?.RemoveMountPoint(_options.MountPoint);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error removing mount point");
        }

        _dokanInstance?.Dispose();
        _dokan?.Dispose();

        _logger.LogInformation("Drive unmounted");
        await base.StopAsync(cancellationToken);
    }

    private sealed class DokanNetLogger : DokanNet.Logging.ILogger
    {
        private readonly ILogger _logger;
        public DokanNetLogger(ILogger logger) => _logger = logger;

        public void Debug(string message, params object[] args) => _logger.LogDebug(message, args);
        public void Info(string message, params object[] args) => _logger.LogInformation(message, args);
        public void Warn(string message, params object[] args) => _logger.LogWarning(message, args);
        public void Error(string message, params object[] args) => _logger.LogError(message, args);
        public void Fatal(string message, params object[] args) => _logger.LogCritical(message, args);
        public bool DebugEnabled => _logger.IsEnabled(LogLevel.Debug);
    }
}
