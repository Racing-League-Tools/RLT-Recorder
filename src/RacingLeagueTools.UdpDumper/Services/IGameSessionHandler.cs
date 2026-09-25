namespace RacingLeagueTools.UdpDumper.Services;

/// <summary>
/// Result of packet processing for a specific game
/// </summary>
public struct GamePacketResult
{
    public GamePacketResult()
    {
    }

    public bool? IsShouldDump { get; set; } = null;
    public bool? IsShouldSaveDump { get; set; } = null;
    public string? GameInfo { get; set; } = null;
}

/// <summary>
/// Interface for game-specific packet handling
/// </summary>
public interface IGameSessionHandler
{
    /// <summary>
    /// Checks if this handler can process the packet
    /// </summary>
    bool IsCanHandle(byte[] packet);
    
    /// <summary>
    /// Processes packet and returns dumping/saving decisions
    /// </summary>
    GamePacketResult ProcessPacket(byte[] packet);
    
    /// <summary>
    /// Resets handler state for new session
    /// </summary>
    void Reset();
    
    /// <summary>
    /// Gets current session identifier (if available)
    /// </summary>
    string? CurrentSessionId { get; }
}