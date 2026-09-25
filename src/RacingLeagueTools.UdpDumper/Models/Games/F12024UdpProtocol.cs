namespace RacingLeagueTools.UdpDumper.Models;

public static class F12024UdpProtocol
{
    public const ushort PacketFormat = 2024;
    public const int HeaderSize = 29;
}

public struct F12024PacketHeader
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

public enum F12024Udp : byte
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
    TimeTrial = 14
}

public static class F12024PacketHelper
{
    public static bool IsValidF12024Packet(byte[]? packet)
    {
        if (packet == null || packet.Length < F12024UdpProtocol.HeaderSize)
            return false;

        var packetFormat = BitConverter.ToUInt16(packet, 0);
        return packetFormat == F12024UdpProtocol.PacketFormat;
    }

    public static F12024PacketHeader ParseHeader(byte[] packet)
    {
        if (!IsValidF12024Packet(packet))
            throw new ArgumentException("Not a valid F1 2024 UDP packet");

        return new F12024PacketHeader
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

    public static F12024Udp? GetPacketType(byte[] packet)
    {
        if (!IsValidF12024Packet(packet))
            return null;

        return (F12024Udp)packet[6];
    }

    public static ulong? GetSessionUID(byte[] packet)
    {
        if (!IsValidF12024Packet(packet))
            return null;

        return BitConverter.ToUInt64(packet, 7);
    }
}