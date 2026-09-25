using RacingLeagueTools.UdpDumper.Models;
using Serilog;
using System.Text.Json;

namespace RacingLeagueTools.UdpDumper.Services;

public class ConfigManager
{
    private const string ConfigFileName = "udp_dumper_config.json";
    private const string DumpsFolderName = "dumps";
    private readonly string _configFilePath;
    private readonly string _appDirectory;

    public ConfigManager()
    {
        // Get the directory where the executable is located (works with single-file deployment)
        _appDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppDomain.CurrentDomain.BaseDirectory;
        _configFilePath = Path.Combine(_appDirectory, ConfigFileName);
    }

    public UdpDumperConfig LoadConfig()
    {
        try
        {
            UdpDumperConfig config;
            if (File.Exists(_configFilePath))
            {
                var json = File.ReadAllText(_configFilePath);
                config = JsonSerializer.Deserialize<UdpDumperConfig>(json) ?? new UdpDumperConfig();
                Log.Debug("Configuration loaded from {FilePath}", _configFilePath);
            }
            else
            {
                config = new UdpDumperConfig();
                SaveConfig(config);
                Log.Information("Created new configuration file at {FilePath}", _configFilePath);
            }

            if (string.IsNullOrEmpty(config.OutputDirectory))
                config.OutputDirectory = Path.Combine(_appDirectory, DumpsFolderName);

            EnsureOutputDirectoryExists(config.OutputDirectory);
            return config;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load configuration from {FilePath}", _configFilePath);
            
            // Use default output directory in case of error
            var defaultOutputDir = Path.Combine(_appDirectory, DumpsFolderName);
            EnsureOutputDirectoryExists(defaultOutputDir);
            
            return new UdpDumperConfig
            {
                OutputDirectory = defaultOutputDir
            };
        }
    }

    public void SaveConfig(UdpDumperConfig config)
    {
        try
        {
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configFilePath, json);
            Log.Debug("Configuration saved to {FilePath}", _configFilePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save configuration to {FilePath}", _configFilePath);
        }
    }
    
    private void EnsureOutputDirectoryExists(string directory)
    {
        if (!Directory.Exists(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
                Log.Debug("Created output directory: {Directory}", directory);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to create output directory: {Directory}", directory);
            }
        }
    }
}