using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RltUdpClient.Core;

/// <summary>A newer release than the one running.</summary>
public sealed record UpdateInfo(string Version, string Url);

/// <summary>
/// Asks once at start-up and then daily whether a newer release exists. Only
/// ever tells; downloading and installing stay with the user.
///
/// The question goes to the project's own endpoint, which also counts it:
/// version, operating system, architecture, window or command line, and a
/// random install id — nothing else, and no address is kept. When that endpoint
/// cannot be reached, GitHub's release list answers instead, so a dead counter
/// never hides a new version. <see cref="AppConfig.UpdateCheck"/> turns both off.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    private const string Endpoint = "https://rlt-recorder.kaac.uk/v1/check";
    private const string ReleasesApi = "https://api.github.com/repos/Racing-League-Tools/RLT-Recorder/releases?per_page=10";

    /// <summary>Points the check somewhere else, for testing against a local server.</summary>
    private const string EndpointOverride = "RLT_RECORDER_UPDATE_URL";

    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly HttpClient _http;
    private readonly string _query;

    /// <param name="flavor"><c>gui</c> or <c>cli</c>.</param>
    public UpdateChecker(string flavor, string installId)
    {
        _query = $"v={Uri.EscapeDataString(AppVersion.Number)}&os={OsName()}&arch={ArchName()}"
                 + $"&flavor={flavor}&id={installId}";

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub rejects API calls without one.
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RLT-Recorder", AppVersion.Number));
    }

    /// <summary>Raised, on a background thread, each time a check finds a newer release.</summary>
    public event Action<UpdateInfo>? UpdateAvailable;

    /// <summary>Checks now and then every day until cancelled. Never throws.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var update = await CheckAsync(cancellationToken);
            if (update is not null)
                UpdateAvailable?.Invoke(update);

            try
            {
                await Task.Delay(Interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        var endpoint = Environment.GetEnvironmentVariable(EndpointOverride) is { Length: > 0 } custom
            ? custom
            : Endpoint;

        var latest = await AskAsync($"{endpoint}?{_query}", ReadEndpoint, cancellationToken)
                     ?? await AskAsync(ReleasesApi, ReadGitHub, cancellationToken);

        return latest is not null && IsNewer(latest.Version, AppVersion.Number) ? latest : null;
    }

    private async Task<UpdateInfo?> AskAsync(string url, Func<JsonElement, UpdateInfo?> read,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            return read(json.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or UriFormatException or InvalidOperationException or ObjectDisposedException)
        {
            // Offline, blocked, timed out or answered with something odd: none of
            // it is worth a word to someone who is recording a race.
            return null;
        }
    }

    /// <summary><c>{ "latest": "0.3.0", "url": "…" }</c>; an empty object when the server does not know.</summary>
    private static UpdateInfo? ReadEndpoint(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("latest", out var latest) && latest.GetString() is { } version
        && root.TryGetProperty("url", out var url) && url.GetString() is { } link
            ? new UpdateInfo(version, link)
            : null;

    /// <summary>The newest published entry of GitHub's release list, pre-releases included.</summary>
    private static UpdateInfo? ReadGitHub(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var release in root.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                continue;

            var tag = release.GetProperty("tag_name").GetString();
            var link = release.GetProperty("html_url").GetString();
            return tag is null || link is null ? null : new UpdateInfo(tag.TrimStart('v'), link);
        }

        return null;
    }

    /// <summary>Compares plain <c>x.y.z</c> numbers; anything unparsable never counts as newer.</summary>
    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(candidate.TrimStart('v'), out var a)
        && Version.TryParse(current, out var b)
        && a > b;

    private static string OsName() =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "macos"
        : "linux";

    /// <summary>The build that is running, not the machine: an Intel build under Rosetta reports x64.</summary>
    private static string ArchName() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        Architecture.X86 => "x86",
        var other => other.ToString().ToLowerInvariant(),
    };

    public void Dispose() => _http.Dispose();
}
