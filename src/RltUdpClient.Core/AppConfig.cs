using System.Text.Json;
using System.Text.Json.Serialization;

namespace RltUdpClient.Core;

/// <summary>
/// On-disk settings. Lives next to the executable as <c>config.json</c> unless a
/// path is given explicitly, matching how the upstream dumper behaves — except
/// for the window on macOS and Linux, see <see cref="UsesUserFolders"/>.
/// </summary>
public sealed class AppConfig
{
    public const string DefaultFileName = "config.json";

    /// <summary>Recordings folder in the home directory, and the settings folder on macOS.</summary>
    private const string UserFolderName = "RLT Recorder";

    /// <summary>Settings folder under <c>~/.config</c> on Linux, lower-case as is the custom there.</summary>
    private const string XdgFolderName = "rlt-recorder";

    /// <summary>
    /// True when running from <c>*.app/Contents/MacOS</c>. Nothing may be written
    /// there: it breaks the bundle's signature, a downloaded app runs from a
    /// read-only translocated copy anyway, and Finder starts it with <c>/</c> as
    /// the working directory, so <c>./dumps</c> would point at the disk root.
    /// </summary>
    public static bool IsMacAppBundle { get; } = OperatingSystem.IsMacOS()
        && AppContext.BaseDirectory.Contains(".app/Contents/MacOS", StringComparison.Ordinal);

    /// <summary>
    /// Whether settings and recordings go to the user's own folders rather than
    /// beside the executable. Always inside a macOS bundle. For the window on
    /// Linux too: it gets unpacked or installed wherever — <c>/opt</c> is not
    /// writable — and a menu launcher picks the working directory, so neither
    /// the binary's folder nor <c>./dumps</c> can be relied on. The command-line
    /// recorder keeps its config beside itself or wherever <c>--config</c> says,
    /// which is what the systemd install depends on.
    /// </summary>
    private static bool UsesUserFolders(bool desktop) => IsMacAppBundle || (desktop && OperatingSystem.IsLinux());

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [JsonPropertyName("port")]
    public int Port { get; set; } = 20777;

    /// <summary>A relative path is taken from the folder holding the config file, see <see cref="ResolvedOutputDirectory"/>.</summary>
    [JsonPropertyName("output_directory")]
    public string OutputDirectory { get; set; } = "./dumps";

    [JsonPropertyName("file_prefix")]
    public string FilePrefix { get; set; } = "dump";

    [JsonPropertyName("session_timeout_seconds")]
    public int SessionTimeoutSeconds { get; set; } = 120;

    /// <summary>Seconds to keep recording after the game reports final classification.</summary>
    [JsonPropertyName("final_classification_grace_seconds")]
    public int FinalClassificationGraceSeconds { get; set; } = 8;

    [JsonPropertyName("receive_buffer_bytes")]
    public int ReceiveBufferBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Serve recorded files over HTTP. The only way to reach them on a headless box.</summary>
    [JsonPropertyName("http_enabled")]
    public bool HttpEnabled { get; set; } = true;

    /// <summary>Deliberately next to the telemetry port, so it is easy to remember.</summary>
    [JsonPropertyName("http_port")]
    public int HttpPort { get; set; } = 20780;

    /// <summary>
    /// When the HTTP port is taken, move to the next free one. Off by default: a
    /// server that silently lands somewhere else is worse than one that says it
    /// could not start, because nobody can find it afterwards.
    /// </summary>
    [JsonPropertyName("http_port_fallback")]
    public bool HttpPortFallback { get; set; }

    /// <summary>
    /// Show a &lt;name&gt;.local address alongside the raw IPs, so nobody has to chase
    /// DHCP leases. The name is resolved by the operating system's mDNS responder
    /// (avahi on Linux, Bonjour on macOS, built in on Windows 10+) rather than by
    /// us — reimplementing one would only fight with the responder already running.
    /// </summary>
    [JsonPropertyName("mdns_enabled")]
    public bool MdnsEnabled { get; set; } = true;

    /// <summary>Overrides the machine host name in that address. Empty means use the host name.</summary>
    [JsonPropertyName("mdns_name")]
    public string MdnsName { get; set; } = "";

    /// <summary>
    /// Start recording as soon as the window opens. On by default: someone who
    /// opens the recorder and forgets to press a button has lost the race, and
    /// nothing brings it back. The command-line recorder always starts.
    /// </summary>
    [JsonPropertyName("auto_start")]
    public bool AutoStart { get; set; } = true;

    /// <summary>
    /// Ask once a day whether a newer release exists, which also counts this
    /// install by version and platform; see <see cref="UpdateChecker"/> for
    /// exactly what is sent. Off stops both.
    /// </summary>
    [JsonPropertyName("update_check")]
    public bool UpdateCheck { get; set; } = true;

    /// <summary>
    /// The folder a relative <see cref="OutputDirectory"/> is measured from: the
    /// one holding the config file, as the upstream dumper does. Not the working
    /// directory — the main RLT application starts the recorder without setting
    /// one, which would scatter dumps into whatever folder RLT itself runs in.
    /// </summary>
    [JsonIgnore]
    public string BaseDirectory { get; private set; } = AppContext.BaseDirectory;

    [JsonIgnore]
    public string ResolvedOutputDirectory => Path.GetFullPath(OutputDirectory, BaseDirectory);

    /// <summary>
    /// Loads the configuration, writing a default file when none exists yet so
    /// that a headless install has something to edit.
    /// </summary>
    public static AppConfig Load(string path, Action<string>? log = null, bool desktop = false)
    {
        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? AppContext.BaseDirectory;

        if (!File.Exists(path))
        {
            var created = new AppConfig();

            // Home rather than Documents: Documents is behind a macOS privacy
            // prompt, and a member who clicks "Don't Allow" gets no recordings.
            if (UsesUserFolders(desktop))
                created.OutputDirectory = Path.Combine(Home, UserFolderName);

            created.BaseDirectory = baseDirectory;
            created.Save(path);
            log?.Invoke($"Created {path}");
            return created;
        }

        var config = JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonContext.Default.AppConfig)
                     ?? new AppConfig();
        config.BaseDirectory = baseDirectory;
        log?.Invoke($"Loaded {path}");
        return config;
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(this, AppJsonContext.Default.AppConfig));
    }

    /// <summary>
    /// Resolves the config path: the one given, or <c>config.json</c> beside the
    /// executable — in the user's settings folder when <see cref="UsesUserFolders"/>.
    /// </summary>
    public static string ResolvePath(string? explicitPath, bool desktop = false)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return Path.GetFullPath(explicitPath);

        if (!UsesUserFolders(desktop))
            return Path.Combine(AppContext.BaseDirectory, DefaultFileName);

        if (OperatingSystem.IsMacOS())
            return Path.Combine(Home, "Library", "Application Support", UserFolderName, DefaultFileName);

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configRoot = string.IsNullOrWhiteSpace(xdg) ? Path.Combine(Home, ".config") : xdg;
        return Path.Combine(configRoot, XdgFolderName, DefaultFileName);
    }

    public RecorderOptions ToRecorderOptions() => new()
    {
        Port = Port,
        OutputDirectory = ResolvedOutputDirectory,
        FilePrefix = FilePrefix,
        SessionTimeout = TimeSpan.FromSeconds(SessionTimeoutSeconds),
        FinalClassificationGrace = TimeSpan.FromSeconds(FinalClassificationGraceSeconds),
        ReceiveBufferBytes = ReceiveBufferBytes,
    };
}
