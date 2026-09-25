using System.Reflection;

namespace RltUdpClient.Core;

/// <summary>
/// The running application's version, presented the way Racing League Tools
/// shows its own: product name followed by the number.
/// </summary>
public static class AppVersion
{
    /// <summary>Just the number, for example <c>0.1.0</c>.</summary>
    public static string Number { get; } = Read();

    /// <summary>Product and number, for example <c>RLT Recorder 0.1.0</c>.</summary>
    public static string Full => $"RLT Recorder {Number}";

    private static string Read()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
            return assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        // The SDK appends "+<commit>" when source link is configured; that is
        // build provenance, not something to show in a title bar.
        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }
}
