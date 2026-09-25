using RacingLeagueTools.UdpDumper.Models;
using Serilog;

namespace RacingLeagueTools.UdpDumper.Services;

/// <summary>
/// F1 25 (2026 Season Pack DLC) packet handler with filtering and session tracking.
/// Packet format identifier: 2026
/// </summary>
public class F12026SessionHandler : IGameSessionHandler
{
    private static readonly HashSet<F12026Udp> _allowedPacketTypes = new()
    {
        F12026Udp.Session,
        F12026Udp.LapData,
        F12026Udp.Event,
        F12026Udp.Participants,
        F12026Udp.FinalClassification,
        F12026Udp.SessionHistory,
        F12026Udp.LapPositions
    };

    private ulong? _currentSessionUID = null;
    private bool _isFirstPacket = true;

    public bool IsCanHandle(byte[] packet)
    {
        return F12026PacketHelper.IsValidF12026Packet(packet);
    }

    public GamePacketResult ProcessPacket(byte[] packet)
    {
        var result = new GamePacketResult();

        if (!IsCanHandle(packet))
            return result;

        try
        {
            var sessionUID = F12026PacketHelper.GetSessionUID(packet);
            var packetType = F12026PacketHelper.GetPacketType(packet);

            if (!sessionUID.HasValue || !packetType.HasValue)
            {
                Log.Debug("Failed to parse F1 2026 packet header");
                return result;
            }

            // Skip packets with Session UID = 0 (main menu packets)
            if (sessionUID.Value == 0)
            {
                result.IsShouldDump = false;
                Log.Debug("F1 2026 packet with Session UID = 0 (main menu) - skipping");
                return result;
            }

            // Check for session change
            if (_isFirstPacket)
            {
                _currentSessionUID = sessionUID.Value;
                _isFirstPacket = false;
                result.GameInfo = $"F1 25 (2026 Season Pack) session started (UID: {sessionUID.Value:X})";
                Log.Information("F1 2026 session initialized with UID: {SessionUID:X}", sessionUID.Value);
            }
            else if (_currentSessionUID != sessionUID.Value)
            {
                Log.Information("F1 2026 session change detected: {OldUID:X} -> {NewUID:X}", _currentSessionUID, sessionUID.Value);
                _currentSessionUID = sessionUID.Value;
                result.IsShouldSaveDump = true;
                result.GameInfo = $"F1 25 (2026 Season Pack) session changed (new UID: {sessionUID.Value:X})";
            }

            result.IsShouldDump = _allowedPacketTypes.Contains(packetType.Value);

            if (result.IsShouldDump is false)
            {
                Log.Debug("F1 2026 packet type {PacketType} filtered out", packetType.Value);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to process F1 2026 packet");
        }

        return result;
    }

    public void Reset()
    {
        _currentSessionUID = null;
        _isFirstPacket = true;
        Log.Debug("F1 2026 handler reset");
    }

    public string? CurrentSessionId => _currentSessionUID?.ToString("X");
}
