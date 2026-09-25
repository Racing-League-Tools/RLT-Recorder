namespace RacingLeagueTools.UdpDumper.Models;

public static class F12025UdpProtocol
{
    public const ushort PacketFormat = 2025;
    public const int HeaderSize = 29;
}

public struct F12025PacketHeader
{
    public ushort PacketFormat;
    public byte GameYear;
    public byte GameMajorVersion;
    public byte GameMinorVersion;
    public byte PacketVersion;
    public byte PacketId;
    public ulong SessionUID;
    public float SessionTime;
    public uint FrameIdentifier;
    public uint OverallFrameIdentifier;
    public byte PlayerCarIndex;
    public byte SecondaryPlayerCarIndex;
}

public enum F12025Udp : byte
{
    Motion = 0,
    Session = 1,
    LapData = 2,
    Event = 3,
    Participants = 4,
    CarSetups = 5,
    CarTelemetry = 6,
    CarStatus = 7,
    FinalClassification = 8,
    LobbyInfo = 9,
    CarDamage = 10,
    SessionHistory = 11,
    TyreSets = 12,
    MotionEx = 13,
    TimeTrial = 14,
    LapPositions = 15
}

public static class F12025PacketHelper
{
    public static bool IsValidF12025Packet(byte[]? packet)
    {
        if (packet == null || packet.Length < F12025UdpProtocol.HeaderSize)
            return false;

        var packetFormat = BitConverter.ToUInt16(packet, 0);
        return packetFormat == F12025UdpProtocol.PacketFormat;
    }

    public static F12025PacketHeader ParseHeader(byte[] packet)
    {
        if (!IsValidF12025Packet(packet))
            throw new ArgumentException("Not a valid F1 2025 UDP packet");

        return new F12025PacketHeader
        {
            PacketFormat = BitConverter.ToUInt16(packet, 0),
            GameYear = packet[2],
            GameMajorVersion = packet[3],
            GameMinorVersion = packet[4],
            PacketVersion = packet[5],
            PacketId = packet[6],
            SessionUID = BitConverter.ToUInt64(packet, 7),
            SessionTime = BitConverter.ToSingle(packet, 15),
            FrameIdentifier = BitConverter.ToUInt32(packet, 19),
            OverallFrameIdentifier = BitConverter.ToUInt32(packet, 23),
            PlayerCarIndex = packet[27],
            SecondaryPlayerCarIndex = packet[28]
        };
    }

    public static F12025Udp? GetPacketType(byte[] packet)
    {
        if (!IsValidF12025Packet(packet))
            return null;

        return (F12025Udp)packet[6];
    }

    public static ulong? GetSessionUID(byte[] packet)
    {
        if (!IsValidF12025Packet(packet))
            return null;

        return BitConverter.ToUInt64(packet, 7);
    }
}