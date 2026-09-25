using System.IO.Compression;

namespace RltUdpClient.Core;

/// <summary>
/// Writes a Racing League Tools UDP dump.
///
/// Container format, matching RacingLeagueTools.UdpDumper:
///   file = raw DEFLATE stream (RFC 1951, no gzip/zlib wrapper, no magic)
///   body = repeated [int32 little-endian packet length][raw UDP packet]
/// No timestamps are stored; RLT reconstructs timing from the packets themselves.
///
/// Unlike the original, this writes straight through to the file instead of
/// buffering the whole session in memory, and always closes the deflate stream
/// before the file is finished so the stream is properly terminated.
///
/// Because it streams, the file on disk is incomplete for as long as recording
/// runs — the compressor holds data back, so it can even be zero bytes. It is
/// therefore written under a <c>.partial</c> name and moved into place by
/// <see cref="FinalizeAs"/> once closed, so nothing ever offers a half-written
/// dump as if it were finished.
/// </summary>
public sealed class DumpWriter : IAsyncDisposable
{
    private readonly FileStream _file;
    private readonly DeflateStream _deflate;
    private readonly byte[] _lengthBuffer = new byte[sizeof(int)];

    public const string PartialExtension = ".partial";

    public DumpWriter(string partialPath)
    {
        Path = partialPath;
        _file = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        _deflate = new DeflateStream(_file, CompressionLevel.SmallestSize, leaveOpen: false);
    }

    /// <summary>The <c>.partial</c> file being written.</summary>
    public string Path { get; }

    /// <summary>Number of packets written.</summary>
    public int PacketCount { get; private set; }

    /// <summary>Uncompressed bytes handed to the compressor, including length prefixes.</summary>
    public long UncompressedBytes { get; private set; }

    public void Write(ReadOnlySpan<byte> packet)
    {
        BitConverter.TryWriteBytes(_lengthBuffer, packet.Length);
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(_lengthBuffer);

        _deflate.Write(_lengthBuffer);
        _deflate.Write(packet);

        PacketCount++;
        UncompressedBytes += _lengthBuffer.Length + packet.Length;
    }

    public async ValueTask DisposeAsync()
    {
        // Order matters: the deflate stream must be disposed before the file is
        // closed, otherwise its final block never reaches the disk.
        await _deflate.DisposeAsync();
        await _file.DisposeAsync();
    }

    /// <summary>
    /// Moves the finished dump to its final name. Must be called after
    /// <see cref="DisposeAsync"/>, and returns the path actually used — the
    /// <c>.partial</c> one if the move could not be made, so a recording is
    /// never thrown away just because it could not be renamed.
    /// </summary>
    public string FinalizeAs(string finalPath)
    {
        try
        {
            File.Move(Path, finalPath, overwrite: true);
            return finalPath;
        }
        catch (IOException)
        {
            return Path;
        }
        catch (UnauthorizedAccessException)
        {
            return Path;
        }
    }
}
