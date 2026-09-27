namespace RltUdpClient.Core;

public sealed class RecorderOptions
{
    /// <summary>UDP port to listen on. 20777 is the F1 default.</summary>
    public int Port { get; set; } = 20777;

    /// <summary>Directory the .dat files are written to.</summary>
    public string OutputDirectory { get; set; } = "";

    /// <summary>Leading part of the generated file name.</summary>
    public string FilePrefix { get; set; } = "dump";

    /// <summary>Silence after which the current session is considered finished.</summary>
    public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// How long to keep recording after the final classification packet before
    /// closing the file. The game keeps sending history and position updates on
    /// the results screen, so cutting instantly risks losing the tail of them.
    /// </summary>
    public TimeSpan FinalClassificationGrace { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Socket receive buffer. The default (~64 KB) holds only about 50 telemetry
    /// packets, so a brief stall can drop data; 4 MB gives comfortable headroom
    /// on slower machines.
    /// </summary>
    public int ReceiveBufferBytes { get; set; } = 4 * 1024 * 1024;
}

/// <summary>Live counters, pushed to the UI while recording.</summary>
public readonly record struct RecorderStatus(
    int PacketsReceived,
    int PacketsWritten,
    int PacketsFiltered,
    string? SessionId,
    string? GameInfo,
    long UncompressedBytes)
{
    public double FilteredPercent =>
        PacketsReceived > 0 ? PacketsFiltered * 100.0 / PacketsReceived : 0;
}

/// <summary>A finished dump file.</summary>
public sealed record SessionSummary(
    string Path,
    string? SessionId,
    string? GameInfo,
    int PacketsWritten,
    long UncompressedBytes,
    long FileBytes,
    DateTime StartedAt,
    DateTime FinishedAt,
    SessionEndReason Reason)
{
    public TimeSpan Duration => FinishedAt - StartedAt;

    public string FileName => System.IO.Path.GetFileName(Path);
}

public enum SessionEndReason
{
    SessionChanged,
    Finished,
    Timeout,
    Stopped,
}

/// <summary>
/// The recorder could not start for a reason worth showing the user verbatim.
/// </summary>
public sealed class RecorderStartException(string message, Exception? inner = null)
    : Exception(message, inner);
