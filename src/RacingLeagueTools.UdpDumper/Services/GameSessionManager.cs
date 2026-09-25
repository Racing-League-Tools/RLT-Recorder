using Serilog;

namespace RacingLeagueTools.UdpDumper.Services;

/// <summary>
/// Manages multiple game session handlers
/// </summary>
public class GameSessionManager
{
    private readonly List<IGameSessionHandler> _handlers;
    private IGameSessionHandler? _activeHandler;

    public GameSessionManager()
    {
        _handlers = new List<IGameSessionHandler>();
    }

    public void AddHandler(IGameSessionHandler handler)
    {
        _handlers.Add(handler);
    }
    
    public GamePacketResult ProcessPacket(byte[] packet)
    {
        if (_activeHandler?.IsCanHandle(packet) == true)
            return _activeHandler.ProcessPacket(packet);

        foreach (var handler in _handlers)
        {
            if (handler.IsCanHandle(packet))
            {
                _activeHandler = handler;
                return handler.ProcessPacket(packet);
            }
        }

        // Unknown game: keep the packet.
        return new GamePacketResult { IsShouldDump = true };
    }
   
    public void Reset()
    {
        foreach (var handler in _handlers)
        {
            handler.Reset();
        }
        _activeHandler = null;
        Log.Debug("All game handlers reset");
    }

    public string? CurrentSessionId => _activeHandler?.CurrentSessionId;
}