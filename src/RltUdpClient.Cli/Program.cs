using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RltUdpClient.Core;

namespace RltUdpClient.Cli;

/// <summary>
/// Headless recorder. Meant for an always-on box on the same network as the
/// game - a Raspberry Pi, a NAS, a spare laptop - where a window would be in
/// the way. Files come back off it over the built-in HTTP server.
/// </summary>
internal static class Program
{
    private const string EndpointFileName = "endpoint.txt";

    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("-h") || args.Contains("--help"))
        {
            ShowHelp();
            return 0;
        }

        AppConfig config;
        try
        {
            config = LoadConfiguration(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Try --help.");
            return 2;
        }

        var options = config.ToRecorderOptions();
        var state = new RecorderState();

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine();
            Console.WriteLine("Stopping, finishing the current file...");
            stopping.Cancel();
        };

        await using var recorder = new UdpRecorder(options);
        recorder.Message += message => Console.WriteLine($"  {message}");
        recorder.StatusChanged += state.Update;
        recorder.FileOpened += path => state.SetCurrentFile(Path.GetFileName(path));
        recorder.SessionCompleted += summary =>
        {
            state.AddCompleted(summary);
            Report(summary);
        };

        Directory.CreateDirectory(options.OutputDirectory);

        await using var server = new StatusServer(config, state, options.OutputDirectory);
        var addresses = StartServer(config, server, options.OutputDirectory, stopping.Token);

        Console.WriteLine($"{AppVersion.Full}");
        Console.WriteLine($"telemetry on UDP {config.Port}  ->  {options.OutputDirectory}");
        foreach (var address in addresses)
            Console.WriteLine($"                files at {address}");
        Console.WriteLine("Press Ctrl+C to stop.");
        Console.WriteLine();

        state.MarkStarted();

        try
        {
            await recorder.RunAsync(stopping.Token);
        }
        catch (RecorderStartException ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"  {ex.Message}");
            return 1;
        }
        finally
        {
            state.MarkStopped();
        }

        return 0;
    }

    /// <summary>
    /// Brings up the file server and reports where it landed. A failure here is
    /// logged and then ignored: recording is the job, serving files is a convenience.
    /// </summary>
    private static List<string> StartServer(
        AppConfig config, StatusServer server, string outputDirectory, CancellationToken cancellationToken)
    {
        var addresses = new List<string>();

        if (!config.HttpEnabled)
            return addresses;

        if (!server.TryStart(cancellationToken, out var error))
        {
            Console.Error.WriteLine($"  ! File server did not start: {error}.");
            Console.Error.WriteLine("    Recording continues; edit http_port in the config to fix this.");
            return addresses;
        }

        if (config.MdnsEnabled)
            addresses.Add($"http://{LocalName(config)}.local:{server.Port}/");

        addresses.AddRange(LocalAddresses().Select(ip => $"http://{ip}:{server.Port}/"));

        // Leave the endpoint on disk too, for whoever pulls the SD card instead
        // of reaching the box over the network.
        WriteEndpointFile(outputDirectory, addresses);

        return addresses;
    }

    private static void WriteEndpointFile(string outputDirectory, IEnumerable<string> addresses)
    {
        try
        {
            File.WriteAllLines(Path.Combine(outputDirectory, EndpointFileName), addresses);
        }
        catch (IOException)
        {
            // Not worth failing a recording session over.
        }
    }

    /// <summary>
    /// The name the box answers to on the local network. Trailing ".local" is
    /// stripped so the address never comes out as "pi.local.local".
    /// </summary>
    private static string LocalName(AppConfig config)
    {
        var name = string.IsNullOrWhiteSpace(config.MdnsName) ? Dns.GetHostName() : config.MdnsName;

        return name.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            ? name[..^".local".Length]
            : name;
    }

    /// <summary>
    /// Addresses to show in the banner. Purely informational, so every failure
    /// here is swallowed: enumerating interfaces needs a netlink socket, which a
    /// sandboxed service or a locked-down container may refuse, and a recorder
    /// must not die over a line of console output.
    /// </summary>
    private static IEnumerable<string> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                            && !IPAddress.IsLoopback(a.Address))
                .Select(a => a.Address.ToString())
                .Distinct()
                .ToArray();
        }
        catch (Exception)
        {
            return ResolvedHostAddresses();
        }
    }

    /// <summary>Fallback that goes through the resolver instead of netlink.</summary>
    private static IEnumerable<string> ResolvedHostAddresses()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString())
                .Distinct()
                .ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static void Report(SessionSummary summary)
    {
        Console.WriteLine();
        Console.WriteLine($"  file      {summary.FileName}");
        Console.WriteLine($"  session   {summary.SessionId ?? "unknown"}");
        Console.WriteLine($"  packets   {summary.PacketsWritten}");
        Console.WriteLine($"  size      {summary.FileBytes / 1024.0:F0} KB "
                          + $"({summary.UncompressedBytes / 1024.0:F0} KB before compression)");
        Console.WriteLine($"  duration  {summary.Duration:hh\\:mm\\:ss}");
        Console.WriteLine($"  closed    {summary.Reason}");
        Console.WriteLine();
    }

    private static AppConfig LoadConfiguration(string[] args)
    {
        var configPath = AppConfig.ResolvePath(ValueOf(args, "--config", "-c"));
        var config = AppConfig.Load(configPath, message => Console.WriteLine($"  {message}"));

        // Command line wins over the file, so a one-off run needs no editing.
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-p" or "--port":
                    config.Port = ParseInt(Next(args, ref i, "--port"), "--port");
                    break;

                case "-o" or "--output":
                    // Typed in a shell, so relative to where it was typed.
                    config.OutputDirectory = Path.GetFullPath(Next(args, ref i, "--output"));
                    break;

                case "-t" or "--timeout":
                    config.SessionTimeoutSeconds = ParseInt(Next(args, ref i, "--timeout"), "--timeout");
                    break;

                case "--http-port":
                    config.HttpPort = ParseInt(Next(args, ref i, "--http-port"), "--http-port");
                    break;

                case "--no-http":
                    config.HttpEnabled = false;
                    break;

                case "-c" or "--config":
                    i++; // Already consumed above.
                    break;

                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        if (config.Port is < 1 or > 65535)
            throw new ArgumentException($"Port out of range: {config.Port}");

        if (config.HttpPort is < 1 or > 65535)
            throw new ArgumentException($"HTTP port out of range: {config.HttpPort}");

        return config;
    }

    private static string? ValueOf(string[] args, params string[] names)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (names.Contains(args[i]))
                return args[i + 1];
        }

        return null;
    }

    private static string Next(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{name} needs a value.");
        return args[++index];
    }

    private static int ParseInt(string value, string name) =>
        int.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException($"{name} expects a number, got '{value}'.");

    private static void ShowHelp()
    {
        Console.WriteLine("""
            rlt-udp-record - records F1 telemetry into Racing League Tools dump files

            Usage:
              rlt-udp-record [options]

            Options:
              -c, --config <path>     Config file (default: config.json next to the binary)
              -p, --port <port>       UDP port to listen on (default 20777)
              -o, --output <path>     Where to write .dat files (default ./dumps)
              -t, --timeout <sec>     Silence that ends a session (default 120)
                  --http-port <port>  Port for the file server (default 20780)
                  --no-http           Do not serve files over HTTP
              -h, --help              Show this help

            Point the game's telemetry settings at this machine's IP address and
            the chosen port. Recorded files are listed at http://<this machine>:20780/
            along with a live status view. Hand the .dat file to your league
            manager, who replays it in RLT.

            Settings given on the command line override the config file.
            """);
    }
}
