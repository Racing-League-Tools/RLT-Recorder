using RacingLeagueTools.UdpDumper.Models;
using Serilog;

namespace RacingLeagueTools.UdpDumper.Services;

public class UdpDumperService
{
    private readonly ConfigManager _configManager;
    private readonly UdpDumperConfig _config;
    private UdpPacketProcessor? _processor;

    public UdpDumperService()
    {
        _configManager = new ConfigManager();
        _config = _configManager.LoadConfig();
    }

    public async Task StartAsync(int port, int maxSizeMB, int timeout, string outputDir, string prefix)
    {
        ApplyCommandLineOverrides(port, maxSizeMB, timeout, outputDir, prefix);
        _configManager.SaveConfig(_config);

        _processor = new UdpPacketProcessor(_config);
        ConfigureLogging();
        DisplayStartupInfo();
        Console.CancelKeyPress += OnCancelKeyPress;

        try
        {
            _processor.Initialize(_config.Port, _config.UdpReceiveTimeoutMs);
            await _processor.ProcessUdpPackets(_config.MaxSizeMB * 1024 * 1024);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during UDP packet processing");
        }
        finally
        {
            await CleanupAsync();
        }
    }

    private void ApplyCommandLineOverrides(int port, int maxSizeMB, int timeout, string outputDir, string prefix)
    {
        if (port > 0)
            _config.Port = port;

        if (maxSizeMB > 0)
            _config.MaxSizeMB = maxSizeMB;

        if (timeout > 0)
            _config.SessionTimeoutMs = timeout;

        if (!string.IsNullOrWhiteSpace(outputDir))
            _config.OutputDirectory = outputDir;

        if (!string.IsNullOrWhiteSpace(prefix))
            _config.FilePrefix = prefix;
    }

    private void DisplayStartupInfo()
    {
        Console.WriteLine("=================================================");
        Console.WriteLine("  Racing League Tools UDP Dumper");
        Console.WriteLine("=================================================");
        Console.WriteLine($"  Listening on port: {_config.Port}");
        Console.WriteLine($"  Output directory: {_config.OutputDirectory}");
        Console.WriteLine($"  Max file size: {_config.MaxSizeMB} MB");
        Console.WriteLine("  Waiting for UDP packets...");
        Console.WriteLine("  Press Ctrl+C to stop recording");
        Console.WriteLine("=================================================");
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        _processor?.Stop();
        Console.WriteLine("\nStopping recording, please wait...");
    }

    private void ConfigureLogging()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(outputTemplate: "{Message:lj}{NewLine}{Exception}")
            .WriteTo.File(Path.Combine(_config.OutputDirectory, "udp_dumper_log_.txt"),
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private async Task CleanupAsync()
    {
        try
        {
            if (_processor != null)
            {
                await _processor.CloseDumpFile();
                _processor.Dispose();
            }

            Console.WriteLine("\n=================================================");
            Console.WriteLine("  Recording stopped");
            Console.WriteLine($"  Total packets captured: {_processor?.GetTotalPacketsReceived() ?? 0}");
            Console.WriteLine($"  Dumps saved to: {_config.OutputDirectory}");
            Console.WriteLine("=================================================");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
