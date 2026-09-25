using System.IO.Compression;
using RacingLeagueTools.UdpDumper.Models;
using Serilog;

namespace RacingLeagueTools.UdpDumper.Services;

public class UdpPacketProcessor : IDisposable
{
    private readonly string _outputDirectory;
    private readonly string _filePrefix;
    private readonly int _udpReceiveTimeoutMs;
    private readonly int _sessionTimeoutMs;
    private readonly GameSessionManager _gameSessionManager;

    private UdpClient? _udpClient;
    private MemoryStream? _dumpMemoryStream;
    private DeflateStream? _deflateStream;
    private BinaryWriter? _dumpWriter;
    private int _totalPacketsReceived = 0;
    private int _totalPacketsFiltered = 0;
    private int _totalPacketsDumped = 0;
    private int _currentDumpFileSize = 0;
    private bool _isRunning = true;
    private bool _recordingStarted = false;
    private DateTime _lastValidPacketTime = DateTime.Now;
    private DateTime _currentSessionStartTime = DateTime.Now;

    public UdpPacketProcessor(UdpDumperConfig config)
    {
        _outputDirectory = config.OutputDirectory;
        _filePrefix = config.FilePrefix;
        _udpReceiveTimeoutMs = config.UdpReceiveTimeoutMs;
        _sessionTimeoutMs = config.SessionTimeoutMs;

        // Initialize game session manager with F1 2024, F1 2025 and F1 2026 handlers
        _gameSessionManager = new GameSessionManager();
        _gameSessionManager.AddHandler(new F12026SessionHandler());
        _gameSessionManager.AddHandler(new F12025SessionHandler());
        _gameSessionManager.AddHandler(new F12024SessionHandler());

        EnsureDirectoryExists(_outputDirectory);
    }

    public void Initialize(int port, int udpReceiveTimeout)
    {
        var udpClient = new UdpClient(port)
        {
            Client =
            {
                ReceiveTimeout = udpReceiveTimeout,
                EnableBroadcast = true
            }
        };
        udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udpClient = udpClient;

        InitializeDumpFile();
    }

    public async Task ProcessUdpPackets(int maxFileSize)
    {
        var udpClient = _udpClient ?? throw new InvalidOperationException("UDP client is not initialized.");

        while (_isRunning)
        {
            try
            {
                var receiveTask = udpClient.ReceiveAsync();
                var timeoutTask = Task.Delay(_udpReceiveTimeoutMs);

                if (await Task.WhenAny(receiveTask, timeoutTask) == receiveTask)
                {
                    var result = await receiveTask;
                    await ProcessPacket(result.Buffer, maxFileSize);
                }
                else
                {
                    await CheckSessionTimeout();
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                await CheckSessionTimeout();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error processing UDP packet");
            }
        }
    }

    private async Task ProcessPacket(byte[]? packet, int maxFileSize)
    {
        if (packet == null || packet.Length == 0)
            return;

        _totalPacketsReceived++;

        // Show status every 10,000 received packets (regardless of filtering)
        if (_totalPacketsReceived % 10000 == 0)
        {
            var compressedSizeMB = _dumpMemoryStream?.Length / (1024.0 * 1024.0) ?? 0;
            var filteringEfficiency = _totalPacketsReceived > 0
                ? (double)_totalPacketsFiltered / _totalPacketsReceived * 100
                : 0;
            
            Log.Information("Status: Received {Received} packets, dumped {Dumped}, filtered {Filtered} ({FilteringEfficiency:F1}%), compressed size: {Size:F2} MB",
                _totalPacketsReceived, _totalPacketsDumped, _totalPacketsFiltered, filteringEfficiency, compressedSizeMB);
        }

        var gameResult = _gameSessionManager.ProcessPacket(packet);

        if (gameResult.IsShouldSaveDump is true && _totalPacketsDumped > 0)
        {
            Log.Information("Session change detected. Auto-saving current dump file.");
            await CreateNewDumpFile(isSessionChange: true);
        }

        if (gameResult.IsShouldDump is false)
        {
            _totalPacketsFiltered++;
            return;
        }

        _lastValidPacketTime = DateTime.Now;

        if (!_recordingStarted)
        {
            LogFirstPacket(gameResult);
            _recordingStarted = true;
        }

        WritePacketToFile(packet);
        _totalPacketsDumped++;

        if (_currentDumpFileSize >= maxFileSize)
        {
            await CreateNewDumpFile();
        }
    }

    private async Task CheckSessionTimeout()
    {
        if (_totalPacketsDumped > 0)
        {
            var timeSinceLastValidPacket = DateTime.Now - _lastValidPacketTime;

            if (timeSinceLastValidPacket.TotalMilliseconds >= _sessionTimeoutMs)
            {
                Log.Information("Session timeout reached ({SessionTimeoutMs} ms since last valid packet). Auto-saving current dump file.", _sessionTimeoutMs);
                await CreateNewDumpFile(isTimeoutSave: true);
            }
        }
    }

    private void LogFirstPacket(GamePacketResult gameResult)
    {
        Log.Information("First UDP packet received. Recording started.");

        if (!string.IsNullOrEmpty(gameResult.GameInfo))
        {
            Log.Information("{GameInfo}", gameResult.GameInfo);
        }

        if (!string.IsNullOrEmpty(_gameSessionManager.CurrentSessionId))
        {
            Log.Information("Session ID: {SessionId}", _gameSessionManager.CurrentSessionId);
        }
    }

    private void WritePacketToFile(byte[] packet)
    {
        var writer = _dumpWriter ?? throw new InvalidOperationException("Dump writer is not initialized.");
        writer.Write(packet.Length);
        writer.Write(packet);
        _currentDumpFileSize += sizeof(int) + packet.Length;
    }
    
    private async Task CreateNewDumpFile(bool isTimeoutSave = false, bool isSessionChange = false)
    {
        if (_totalPacketsDumped > 0)
        {
            await CloseDumpFile();

            if (isSessionChange)
            {
                Log.Information("Dump file auto-saved due to session change");
            }
            else if (isTimeoutSave)
            {
                Log.Information("Dump file auto-saved due to session timeout");
            }
            else
            {
                Log.Information("Created new dump file due to size limit");
            }
        }

        if (_isRunning && !isTimeoutSave && !isSessionChange)
        {
            InitializeDumpFile();
        }
        else if (isTimeoutSave || isSessionChange)
        {
            ResetSessionState(isSessionChange);
        }
    }

    private void ResetSessionState(bool isSessionChange = false)
    {
        _totalPacketsReceived = 0;
        _totalPacketsFiltered = 0;
        _totalPacketsDumped = 0;
        _recordingStarted = false;
        _lastValidPacketTime = DateTime.Now;
        _currentSessionStartTime = DateTime.Now;

        if (!isSessionChange)
        {
            _gameSessionManager.Reset();
        }

        InitializeDumpFile();

        var reason = isSessionChange ? "session change" : "timeout";
        Log.Information("Session reset due to {Reason}. Ready for new session.", reason);
    }

    private void InitializeDumpFile()
    {
        CloseDumpStreams();

        _dumpMemoryStream = new MemoryStream();
        _deflateStream = new DeflateStream(_dumpMemoryStream, CompressionLevel.SmallestSize);
        _dumpWriter = new BinaryWriter(_deflateStream);
        _currentDumpFileSize = 0;
    }

    private string GenerateFileName()
    {
        var now = DateTime.Now;
        var sessionSuffix = !string.IsNullOrEmpty(_gameSessionManager.CurrentSessionId)
            ? $"_session_{_gameSessionManager.CurrentSessionId}"
            : "";
        return $"{_filePrefix}_{now:yyyyMMdd_HHh_mmm_sss}{sessionSuffix}.dat";
    }

    public void Stop() => _isRunning = false;

    public async Task CloseDumpFile()
    {
        var memoryStream = _dumpMemoryStream;
        var deflateStream = _deflateStream;
        if (_dumpWriter == null || memoryStream == null || deflateStream == null)
            return;

        try
        {
            await deflateStream.FlushAsync();
            await SaveDumpToFile(memoryStream);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error closing dump file");
        }
        finally
        {
            CloseDumpStreams();
        }
    }

    private async Task SaveDumpToFile(MemoryStream memoryStream)
    {
        var fileName = GenerateFileName();
        var filePath = Path.Combine(_outputDirectory, fileName);

        using var fs = new FileStream(filePath, FileMode.Create);
        var data = memoryStream.ToArray();
        await fs.WriteAsync(data);

        LogDumpFileInfo(fileName, data.Length);
    }

    private void LogDumpFileInfo(string fileName, int fileSize)
    {
        var elapsedTime = DateTime.Now - _currentSessionStartTime;
        var fileSizeMB = fileSize / (1024.0 * 1024.0);
        var filteringEfficiency = _totalPacketsReceived > 0
            ? (double)_totalPacketsFiltered / _totalPacketsReceived * 100
            : 0;

        Log.Information("Dump file saved: {FileName}", fileName);
        Log.Information("File size: {Size:F2} MB", fileSizeMB);
        Log.Information("Total packets received: {Received}", _totalPacketsReceived);
        Log.Information("Packets dumped: {Dumped}", _totalPacketsDumped);
        Log.Information("Packets filtered: {Filtered} ({FilteringEfficiency:F1}%)", _totalPacketsFiltered, filteringEfficiency);
        Log.Information("Session recording duration: {Duration}", FormatDuration(elapsedTime));

        if (!string.IsNullOrEmpty(_gameSessionManager.CurrentSessionId))
        {
            Log.Information("Session ID: {SessionId}", _gameSessionManager.CurrentSessionId);
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours:D2}h {duration.Minutes:D2}m {duration.Seconds:D2}s";
        }
        else if (duration.TotalMinutes >= 1)
        {
            return $"{duration.Minutes:D2}m {duration.Seconds:D2}s";
        }
        else
        {
            return $"{duration.Seconds:D2}s";
        }
    }

    private void CloseDumpStreams()
    {
        _dumpWriter?.Close();
        _deflateStream?.Close();
        _dumpMemoryStream?.Close();
        _dumpWriter = null;
        _deflateStream = null;
        _dumpMemoryStream = null;
    }

    private static void EnsureDirectoryExists(string directory)
    {
        if (Directory.Exists(directory))
            return;

        try
        {
            Directory.CreateDirectory(directory);
            Log.Information("Created output directory: {Directory}", directory);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create output directory: {Directory}", directory);
        }
    }

    public void Dispose()
    {
        CloseDumpStreams();
        _udpClient?.Close();
    }

    public int GetTotalPacketsReceived() => _totalPacketsReceived;
}