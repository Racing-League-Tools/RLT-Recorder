using System.Text.Json.Serialization;

namespace RacingLeagueTools.UdpDumper.Models;

public class UdpDumperConfig
{
    [JsonPropertyName("port")]
    public int Port { get; set; } = 20777;
    
    [JsonPropertyName("max_size_mb")]
    public int MaxSizeMB { get; set; } = 1024;
    
    [JsonPropertyName("udp_receive_timeout_ms")]
    public int UdpReceiveTimeoutMs { get; set; } = 1000;
    
    [JsonPropertyName("session_timeout_ms")]
    public int SessionTimeoutMs { get; set; } = 120000;
    
    [JsonPropertyName("output_directory")]
    public string OutputDirectory { get; set; } = "";
    
    [JsonPropertyName("file_prefix")]
    public string FilePrefix { get; set; } = "dump";
}