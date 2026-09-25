using System.Net;
using System.Net.Sockets;
using RacingLeagueTools.UdpDumper.Services;

namespace RltUdpClient.Core;

/// <summary>
/// Records F1 telemetry into Racing League Tools dump files.
///
/// Packet validation, filtering and session detection are reused verbatim from
/// RacingLeagueTools.UdpDumper (<see cref="GameSessionManager"/> and its game
/// handlers), so the output stays byte-compatible with what RLT expects. The
/// socket, file and lifecycle handling around it is ours, because that is the
/// part that has to work on phones and single-board computers too.
/// </summary>
public sealed class UdpRecorder : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly RecorderOptions _options;
    private readonly GameSessionManager _sessions;

    private UdpClient? _udp;
    private DumpWriter? _writer;
    private DateTime _sessionStartedAt;
    private DateTime _lastKeptPacketAt;
    private string? _currentGameInfo;
    private string _currentStem = "";
    private DateTime? _finalClassificationAt;
    private string? _finishedSessionId;

    private int _packetsReceived;
    private int _packetsWritten;
    private int _packetsFiltered;

    public UdpRecorder(RecorderOptions options)
    {
        _options = options;

        _sessions = new GameSessionManager();
        _sessions.AddHandler(new F12026SessionHandler());
        _sessions.AddHandler(new F12025SessionHandler());
        _sessions.AddHandler(new F12024SessionHandler());
    }

    /// <summary>Raised after every packet and on every idle poll.</summary>
    public event Action<RecorderStatus>? StatusChanged;

    /// <summary>Raised when a new dump file is opened, with its full path.</summary>
    public event Action<string>? FileOpened;

    /// <summary>Raised once a dump file has been closed and is safe to share.</summary>
    public event Action<SessionSummary>? SessionCompleted;

    /// <summary>Raised for anything the user should see in the log pane.</summary>
    public event Action<string>? Message;

    public bool IsRecording { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.OutputDirectory);

        try
        {
            _udp = CreateClient(_options);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse
                                             or SocketError.AccessDenied)
        {
            // By far the most common way this fails, and the operating system's
            // own wording explains none of it.
            throw new RecorderStartException(
                $"UDP port {_options.Port} is already being used by another program — "
                + "Racing League Tools itself, SimHub, or a second copy of this recorder. "
                + "Close that program or choose a different port.", ex);
        }

        IsRecording = true;
        Message?.Invoke($"Listening on UDP port {_options.Port}");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var packet = await ReceiveOrIdleAsync(cancellationToken);

                if (packet is null)
                {
                    await CheckDeadlinesAsync();
                    continue;
                }

                await ProcessPacketAsync(packet);
                await CheckDeadlinesAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping is the normal way out.
        }
        finally
        {
            await CloseSessionAsync(SessionEndReason.Stopped);
            IsRecording = false;
            Message?.Invoke("Recording stopped");
        }
    }

    private static UdpClient CreateClient(RecorderOptions options)
    {
        var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.ReceiveBufferSize = options.ReceiveBufferBytes;
        client.EnableBroadcast = true;
        client.Client.Bind(new IPEndPoint(IPAddress.Any, options.Port));
        return client;
    }

    private async Task<byte[]?> ReceiveOrIdleAsync(CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(PollInterval);

        try
        {
            var result = await _udp!.ReceiveAsync(idle.Token);
            return result.Buffer;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null; // Nothing arrived within the poll window.
        }
        catch (SocketException)
        {
            return null; // ICMP port-unreachable and friends; keep listening.
        }
    }

    private async Task ProcessPacketAsync(byte[] packet)
    {
        if (packet.Length == 0)
            return;

        _packetsReceived++;

        // Snapshot the session id *before* the handler advances it, so a file
        // closed by a session change is named after the session it contains.
        var sessionIdBeforePacket = _sessions.CurrentSessionId;

        var result = _sessions.ProcessPacket(packet);

        if (_finishedSessionId is not null)
        {
            if (_sessions.CurrentSessionId == _finishedSessionId)
                return;

            _finishedSessionId = null;
        }

        if (result.IsShouldSaveDump is true && _packetsWritten > 0)
            await CloseSessionAsync(SessionEndReason.SessionChanged, sessionIdBeforePacket);

        if (result.IsShouldDump is false)
        {
            _packetsFiltered++;
            RaiseStatus();
            return;
        }

        if (!string.IsNullOrEmpty(result.GameInfo))
        {
            _currentGameInfo = result.GameInfo;
            Message?.Invoke(result.GameInfo);
        }

        _writer ??= OpenWriter();
        _writer.Write(packet);

        _packetsWritten++;
        _lastKeptPacketAt = DateTime.UtcNow;

        // The game announces the end of a session with a final classification
        // packet. That is the moment to wrap the file up, rather than waiting
        // two minutes for the line to go quiet.
        if (_finalClassificationAt is null && IsFinalClassification(packet))
        {
            _finalClassificationAt = DateTime.UtcNow;
            Message?.Invoke("Final classification received, finishing the session");
        }

        RaiseStatus();
    }

    private DumpWriter OpenWriter()
    {
        _sessionStartedAt = DateTime.UtcNow;
        _currentStem = $"{_options.FilePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}";

        // The session id is only known for certain once the session ends, so the
        // final name is decided at close time and the file is written under a
        // .partial name until then.
        var partial = Path.Combine(
            _options.OutputDirectory, _currentStem + ".dat" + DumpWriter.PartialExtension);

        Message?.Invoke($"Recording to {_currentStem}.dat");
        FileOpened?.Invoke(Path.Combine(_options.OutputDirectory, _currentStem + ".dat"));
        return new DumpWriter(partial);
    }

    private string BuildFinalPath(string? sessionId)
    {
        var suffix = string.IsNullOrEmpty(sessionId) ? "" : $"_session_{sessionId}";
        return Path.Combine(_options.OutputDirectory, $"{_currentStem}{suffix}.dat");
    }

    private async Task CheckDeadlinesAsync()
    {
        if (_packetsWritten == 0)
        {
            RaiseStatus();
            return;
        }

        var now = DateTime.UtcNow;

        // Give trailing packets a moment to land - the game keeps sending
        // history and position updates on the results screen - then close.
        if (_finalClassificationAt is { } finishedAt
            && now - finishedAt >= _options.FinalClassificationGrace)
        {
            _finishedSessionId = _sessions.CurrentSessionId;
            await CloseSessionAsync(SessionEndReason.Finished);
        }
        else if (now - _lastKeptPacketAt >= _options.SessionTimeout)
        {
            Message?.Invoke($"No telemetry for {_options.SessionTimeout.TotalSeconds:F0} s, closing the session");
            await CloseSessionAsync(SessionEndReason.Timeout);
            _sessions.Reset();
        }

        RaiseStatus();
    }

    /// <summary>
    /// True for a final classification packet. The header layout is the same in
    /// the 2024, 2025 and 2026 formats - packet id at offset 6 - so one check
    /// covers every supported game.
    /// </summary>
    private static bool IsFinalClassification(byte[] packet)
    {
        const int headerSize = 29;
        const byte finalClassification = 8;

        if (packet.Length < headerSize)
            return false;

        var format = BitConverter.ToUInt16(packet, 0);
        return format is 2024 or 2025 or 2026 && packet[6] == finalClassification;
    }

    private async Task CloseSessionAsync(SessionEndReason reason, string? sessionIdOverride = null)
    {
        var writer = _writer;
        if (writer is null)
            return;

        _writer = null;

        var sessionId = sessionIdOverride ?? _sessions.CurrentSessionId;
        var packets = writer.PacketCount;
        var uncompressed = writer.UncompressedBytes;

        await writer.DisposeAsync();

        var path = writer.FinalizeAs(BuildFinalPath(sessionId));
        var fileBytes = new FileInfo(path).Length;
        var summary = new SessionSummary(
            path,
            sessionId,
            _currentGameInfo,
            packets,
            uncompressed,
            fileBytes,
            _sessionStartedAt,
            DateTime.UtcNow,
            reason);

        ResetCounters();
        _finalClassificationAt = null;
        SessionCompleted?.Invoke(summary);
        Message?.Invoke($"Saved {summary.FileName} - {packets} packets, {fileBytes / 1024.0:F0} KB");
    }

    private void ResetCounters()
    {
        _packetsReceived = 0;
        _packetsWritten = 0;
        _packetsFiltered = 0;
        _currentGameInfo = null;
    }

    private void RaiseStatus() => StatusChanged?.Invoke(new RecorderStatus(
        _packetsReceived,
        _packetsWritten,
        _packetsFiltered,
        _sessions.CurrentSessionId,
        _currentGameInfo,
        _writer?.UncompressedBytes ?? 0));

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null)
            await _writer.DisposeAsync();
        _udp?.Dispose();
    }
}
