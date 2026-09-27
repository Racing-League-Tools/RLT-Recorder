using System.Text.Json.Serialization;

namespace RltUdpClient.Core;

/// <summary>
/// What the status endpoint returns. A concrete type rather than an anonymous
/// one because trimmed builds serialize through the source generator, which
/// needs something it can see at compile time.
/// </summary>
public sealed record StatusPayload(
    bool Running,
    bool Receiving,
    int UdpPort,
    string? CurrentFile,
    string? SessionId,
    string? GameInfo,
    int PacketsReceived,
    int PacketsWritten,
    int PacketsFiltered,
    long CurrentBytes,
    double? SecondsSinceLastPacket,
    string Version,
    IReadOnlyList<DumpFileEntry> Files);

public sealed record DumpFileEntry(string Name, long Bytes, DateTime Modified);

/// <summary>
/// Serialization contracts generated at compile time. Without this, a trimmed
/// single-file build throws "Reflection-based serialization has been disabled"
/// the first time it touches JSON.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(StatusPayload))]
internal partial class AppJsonContext : JsonSerializerContext;
