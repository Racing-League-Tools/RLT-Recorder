using RacingLeagueTools.UdpDumper.Models;
using Serilog;

namespace RacingLeagueTools.UdpDumper.Services;

/// <summary>
/// F1 2025 packet handler with filtering and session tracking
/// </summary>
public class F12025SessionHandler : IGameSessionHandler
{
    private static readonly HashSet<F12025Udp> _allowedPacketTypes = new()
    {
        F12025Udp.Session,
        F12025Udp.LapData,
        F12025Udp.Event,
        F12025Udp.Participants,
        F12025Udp.FinalClassification,
        F12025Udp.SessionHistory,
        F12025Udp.LapPositions
    };

    private ulong? _currentSessionUID = null;
    private bool _isFirstPacket = true;

    public bool IsCanHandle(byte[] packet)
    {
        return F12025PacketHelper.IsValidF12025Packet(packet);
    }

    public GamePacketResult ProcessPacket(byte[] packet)
    {
        var result = new GamePacketResult();

        if (!IsCanHandle(packet))
            return result;

        try
        {
            var sessionUID = F12025PacketHelper.GetSessionUID(packet);
            var packetType = F12025PacketHelper.GetPacketType(packet);

            if (!sessionUID.HasValue || !packetType.HasValue)
            {
                Log.Debug("Failed to parse F1 2025 packet header");
                return result;
            }

            // Skip packets with Session UID = 0 (main menu packets)
            if (sessionUID.Value == 0)
            {
                result.IsShouldDump = false;
                Log.Debug("F1 2025 packet with Session UID = 0 (main menu) - skipping");
                return result;
            }

            // Check for session change
            if (_isFirstPacket)
            {
                _currentSessionUID = sessionUID.Value;
                _isFirstPacket = false;
                result.GameInfo = $"F1 2025 session started (UID: {sessionUID.Value:X})";
                Log.Information("F1 2025 session initialized with UID: {SessionUID:X}", sessionUID.Value);
            }
            else if (_currentSessionUID != sessionUID.Value)
            {
                Log.Information("F1 2025 session change detected: {OldUID:X} -> {NewUID:X}", _currentSessionUID, sessionUID.Value);
                _currentSessionUID = sessionUID.Value;
                result.IsShouldSaveDump = true;
                result.GameInfo = $"F1 2025 session changed (new UID: {sessionUID.Value:X})";
            }

            result.IsShouldDump = _allowedPacketTypes.Contains(packetType.Value);

            if (result.IsShouldDump is false)
            {
                Log.Debug("F1 2025 packet type {PacketType} filtered out", packetType.Value);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to process F1 2025 packet");
        }

        return result;
    }

    public void Reset()
    {
        _currentSessionUID = null;
        _isFirstPacket = true;
        Log.Debug("F1 2025 handler reset");
    }

    public string? CurrentSessionId => _currentSessionUID?.ToString("X");
}