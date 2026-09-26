using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using RltUdpClient.Core;

namespace RltUdpClient.Desktop.ViewModels;

/// <summary>
/// The whole application state. Everything the recorder reports arrives on a
/// background thread, so every update is marshalled onto the UI thread here
/// rather than in the view.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 200;

    private readonly AppConfig _config;
    private readonly string _configPath;

    /// <summary>
    /// False when the command line supplied the port or folder. Those belong to
    /// that one run — typically the main RLT application starting us on its
    /// forwarding port — and must not become the settings of the next manual start.
    /// </summary>
    private readonly bool _persistSettings;
    private readonly DispatcherTimer _tick;

    private readonly RecorderState _state = new();

    private UdpRecorder? _recorder;
    private CancellationTokenSource? _stopping;
    private StatusServer? _server;
    private CancellationTokenSource? _serverStopping;
    private string _serverAddress = "";
    private DateTime _lastPacketAt;

    private int _port;
    private string _outputDirectory;
    private bool _isRecording;
    private bool _isReceiving;
    private string _statusHeadline = "Not recording";
    private string _currentFile = "";
    private string? _sessionId;
    private int _packetsWritten;
    private int _packetsFiltered;
    private long _currentBytes;

    public MainViewModel(string[] args)
    {
        _configPath = AppConfig.ResolvePath(null, desktop: true);
        _config = AppConfig.Load(_configPath, desktop: true);

        _port = _config.Port;
        _outputDirectory = _config.ResolvedOutputDirectory;

        var overridden = ApplyArguments(args);
        _persistSettings = !overridden;

        ToggleRecordingCommand = new RelayCommand(ToggleRecording);
        OpenFolderCommand = new RelayCommand(OpenOutputFolder);
        BrowseFolderCommand = new RelayCommand(BrowseForFolder);

        StartServer();

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _tick.Tick += (_, _) => RefreshLiveState();
        _tick.Start();

        RefreshFiles();

        // Started with arguments means started by something that expects a
        // recorder, not a window waiting for a click.
        if (overridden || _config.AutoStart)
            StartRecording();
    }

    /// <summary>
    /// Reads the same options as the upstream dumper, <c>--port</c> and
    /// <c>--output</c>, so the main application can start either one. A GUI has
    /// no console to fail into, so anything unusable is logged and skipped.
    /// Returns whether any setting came from the command line.
    /// </summary>
    private bool ApplyArguments(string[] args)
    {
        var overridden = false;

        for (var i = 0; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : null;

            switch (args[i].ToLowerInvariant())
            {
                case "-p" or "--port":
                    if (int.TryParse(value, out var port) && port is > 0 and <= 65535)
                    {
                        _port = port;
                        overridden = true;
                    }
                    else
                    {
                        Append($"Ignoring {args[i]} {value}: not a port number");
                    }

                    i++;
                    break;

                case "-o" or "--output":
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        _outputDirectory = Path.GetFullPath(value);
                        overridden = true;
                    }

                    i++;
                    break;

                default:
                    Append($"Ignoring unknown argument {args[i]}");
                    break;
            }
        }

        if (overridden)
            Append("Using settings from the command line for this run; they are not saved");

        return overridden;
    }

    /// <summary>
    /// Window title, in the same shape the main application uses: what is going
    /// on, then the product and version. Readable from the taskbar without
    /// bringing the window up.
    /// </summary>
    public string WindowTitle => IsRecording
        ? $"Recording on UDP {Port} - {AppVersion.Full}"
        : AppVersion.Full;

    public ObservableCollection<DumpFileItem> Files { get; } = new();

    public ObservableCollection<string> Log { get; } = new();

    public RelayCommand ToggleRecordingCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public RelayCommand BrowseFolderCommand { get; }

    /// <summary>
    /// Shows the system folder picker and returns the chosen path, or null if
    /// the user backed out. Supplied by the window, which is the only part that
    /// can reach the platform's storage provider.
    /// </summary>
    public Func<string, Task<string?>>? PickFolder { get; set; }

    public int Port
    {
        get => _port;
        set => Set(ref _port, value);
    }

    public string OutputDirectory
    {
        get => _outputDirectory;
        set
        {
            if (!Set(ref _outputDirectory, value))
                return;

            RefreshFiles();

            // The server serves one folder, chosen when it starts, so point it
            // at the new one.
            StartServer();
        }
    }

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (!Set(ref _isRecording, value))
                return;

            Raise(nameof(IsIdle));
            Raise(nameof(ActionLabel));
            Raise(nameof(WindowTitle));
            ToggleRecordingCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Settings are only editable while stopped.</summary>
    public bool IsIdle => !IsRecording;

    public string ActionLabel => IsRecording ? "Stop recording" : "Start recording";

    public bool IsReceiving
    {
        get => _isReceiving;
        private set => Set(ref _isReceiving, value);
    }

    public string StatusHeadline
    {
        get => _statusHeadline;
        private set => Set(ref _statusHeadline, value);
    }

    public string CurrentFile
    {
        get => _currentFile;
        private set
        {
            if (Set(ref _currentFile, value))
                Raise(nameof(SessionLabel));
        }
    }

    /// <summary>Where the recordings can be fetched from, empty when not served.</summary>
    public string ServerAddress
    {
        get => _serverAddress;
        private set
        {
            if (Set(ref _serverAddress, value))
                Raise(nameof(IsServed));
        }
    }

    public bool IsServed => !string.IsNullOrEmpty(ServerAddress);

    // The recorder keeps reporting the last session's UID after closing it, so
    // it is only shown while a file for it is actually open.
    public string SessionLabel => string.IsNullOrEmpty(_sessionId) || string.IsNullOrEmpty(_currentFile) ? "—" : _sessionId;

    public string PacketsWrittenLabel => _packetsWritten.ToString("N0");

    public string PacketsFilteredLabel => _packetsFiltered.ToString("N0");

    public string CurrentSizeLabel => FormatBytes(_currentBytes);

    private void ToggleRecording()
    {
        if (IsRecording)
            StopRecording();
        else
            StartRecording();
    }

    private void StartRecording()
    {
        _config.Port = Port;
        _config.OutputDirectory = OutputDirectory;

        if (_persistSettings)
            TrySaveConfig();

        var recorder = new UdpRecorder(_config.ToRecorderOptions());
        recorder.Message += message => OnUi(() => Append(message));
        recorder.StatusChanged += status =>
        {
            _state.Update(status);
            OnUi(() => ApplyStatus(status));
        };
        recorder.FileOpened += path =>
        {
            _state.SetCurrentFile(Path.GetFileName(path));
            OnUi(() => CurrentFile = Path.GetFileName(path));
        };
        recorder.SessionCompleted += summary =>
        {
            _state.AddCompleted(summary);
            OnUi(() => OnSessionCompleted(summary));
        };

        _recorder = recorder;
        _stopping = new CancellationTokenSource();

        _state.MarkStarted();
        IsRecording = true;
        StatusHeadline = "Waiting for telemetry";

        _ = RunAsync(recorder, _stopping.Token);
    }

    private async Task RunAsync(UdpRecorder recorder, CancellationToken cancellationToken)
    {
        try
        {
            await recorder.RunAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            OnUi(() => Append($"Recording failed: {ex.Message}"));
        }
        finally
        {
            await recorder.DisposeAsync();
            _state.MarkStopped();
            OnUi(() =>
            {
                IsRecording = false;
                IsReceiving = false;
                StatusHeadline = "Not recording";
                CurrentFile = "";
                RefreshFiles();
            });
        }
    }

    private void StopRecording()
    {
        StatusHeadline = "Finishing the current file…";
        _stopping?.Cancel();
    }

    private void ApplyStatus(RecorderStatus status)
    {
        if (status.PacketsReceived > 0)
            _lastPacketAt = DateTime.UtcNow;

        _packetsWritten = status.PacketsWritten;
        _packetsFiltered = status.PacketsFiltered;
        _currentBytes = status.UncompressedBytes;
        _sessionId = status.SessionId;

        Raise(nameof(PacketsWrittenLabel));
        Raise(nameof(PacketsFilteredLabel));
        Raise(nameof(CurrentSizeLabel));
        Raise(nameof(SessionLabel));
    }

    private void OnSessionCompleted(SessionSummary summary)
    {
        CurrentFile = "";
        RefreshFiles();
    }

    /// <summary>
    /// Telemetry arriving is not an event the recorder raises, so it is inferred
    /// from how long it has been since the counters last moved.
    /// </summary>
    private void RefreshLiveState()
    {
        if (!IsRecording)
            return;

        var receiving = _lastPacketAt != default && DateTime.UtcNow - _lastPacketAt < TimeSpan.FromSeconds(3);

        if (receiving == IsReceiving)
            return;

        IsReceiving = receiving;
        StatusHeadline = receiving ? "Receiving telemetry" : "Waiting for telemetry";
    }

    private void RefreshFiles()
    {
        Files.Clear();

        if (!Directory.Exists(OutputDirectory))
            return;

        var found = new DirectoryInfo(OutputDirectory)
            .EnumerateFiles("*.dat", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTime);

        foreach (var file in found)
            Files.Add(new DumpFileItem(file.Name, FormatBytes(file.Length), file.LastWriteTime.ToString("g")));
    }

    private void BrowseForFolder()
    {
        if (PickFolder is null)
            return;

        _ = BrowseAsync();
    }

    private async Task BrowseAsync()
    {
        try
        {
            var chosen = await PickFolder!(OutputDirectory);

            if (!string.IsNullOrWhiteSpace(chosen))
                OutputDirectory = chosen;
        }
        catch (Exception ex)
        {
            Append($"Could not open the folder picker: {ex.Message}");
        }
    }

    /// <summary>
    /// Brings up the file server on the folder currently selected. Failing to
    /// bind is reported and then ignored — recording is the job, serving files
    /// over the network is a convenience.
    /// </summary>
    private void StartServer()
    {
        StopServer();

        if (!_config.HttpEnabled)
            return;

        _config.OutputDirectory = OutputDirectory;
        _serverStopping = new CancellationTokenSource();

        var server = new StatusServer(_config, _state, OutputDirectory);

        if (!server.TryStart(_serverStopping.Token, out var error))
        {
            Append($"File server did not start: {error}");
            return;
        }

        _server = server;
        ServerAddress = $"http://{Environment.MachineName.ToLowerInvariant()}:{server.Port}/";
    }

    private void StopServer()
    {
        _serverStopping?.Cancel();
        _server?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _server = null;
        ServerAddress = "";
    }

    private void OpenOutputFolder()
    {
        try
        {
            Directory.CreateDirectory(OutputDirectory);
            Process.Start(new ProcessStartInfo(OutputDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Append($"Could not open the folder: {ex.Message}");
        }
    }

    private void TrySaveConfig()
    {
        try
        {
            _config.Save(_configPath);
        }
        catch (Exception ex)
        {
            Append($"Settings not saved: {ex.Message}");
        }
    }

    private void Append(string message)
    {
        Log.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");

        while (Log.Count > MaxLogLines)
            Log.RemoveAt(Log.Count - 1);
    }

    public void Shutdown()
    {
        _tick.Stop();
        _stopping?.Cancel();
        StopServer();
    }

    private static void OnUi(Action action) => Dispatcher.UIThread.Post(action);

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB",
    };
}

public sealed record DumpFileItem(string Name, string Size, string Recorded);
