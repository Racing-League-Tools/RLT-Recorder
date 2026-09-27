namespace RltUdpClient.Core;

/// <summary>
/// Thread-safe snapshot of what the recorder is doing, shared between the
/// recording loop and whatever is displaying it — the HTTP status page, a
/// desktop window, or both at once.
/// </summary>
public sealed class RecorderState
{
    private readonly object _gate = new();
    private readonly List<SessionSummary> _completed = new();

    private RecorderStatus _status;
    private string? _currentFile;
    private DateTime? _startedAt;
    private DateTime _lastPacketAt;

    public void MarkStarted()
    {
        lock (_gate)
        {
            _startedAt = DateTime.UtcNow;
        }
    }

    public void MarkStopped()
    {
        lock (_gate)
        {
            _startedAt = null;
            _currentFile = null;
            _status = default;
        }
    }

    public void Update(RecorderStatus status)
    {
        lock (_gate)
        {
            if (status.PacketsReceived > _status.PacketsReceived)
                _lastPacketAt = DateTime.UtcNow;

            _status = status;
        }
    }

    public void SetCurrentFile(string? fileName)
    {
        lock (_gate)
        {
            _currentFile = fileName;
        }
    }

    public void AddCompleted(SessionSummary summary)
    {
        lock (_gate)
        {
            _completed.Add(summary);
            _currentFile = null;
        }
    }

    public RecorderSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new RecorderSnapshot(
                IsRunning: _startedAt.HasValue,
                RunningSince: _startedAt,
                CurrentFile: _currentFile,
                SecondsSinceLastPacket: _lastPacketAt == default
                    ? null
                    : (DateTime.UtcNow - _lastPacketAt).TotalSeconds,
                Status: _status,
                Completed: _completed.ToArray());
        }
    }
}

public sealed record RecorderSnapshot(
    bool IsRunning,
    DateTime? RunningSince,
    string? CurrentFile,
    double? SecondsSinceLastPacket,
    RecorderStatus Status,
    IReadOnlyList<SessionSummary> Completed)
{
    /// <summary>
    /// True once telemetry has actually been seen recently, as opposed to the
    /// recorder merely being up and listening.
    /// </summary>
    public bool IsReceiving => SecondsSinceLastPacket is < 5;
}
