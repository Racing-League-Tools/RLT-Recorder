namespace RltUdpClient.Core;

/// <summary>
/// Picks the car telemetry, car status and car damage packets worth keeping.
///
/// The upstream filter drops all three, but RLT reads saved results from them:
/// top speed from telemetry, tyre compound and age from status, tyre wear for
/// stints and race battles from damage, and current speed for the gap and
/// battle analysis. Keeping every one would multiply the file size, so only
/// the packets that change something RLT computes are kept. RLT turns its own
/// throttling off when it imports a dump, so a lone packet is never skipped,
/// and its processors compare each packet with the previous one of the same
/// type, so they need not be consecutive frames.
///
/// What is kept, per file:
/// <list type="bullet">
/// <item>The first packet of each type, and the next one after the first
/// participants packet or a change in the number of cars, since RLT ignores
/// cars it does not know yet.</item>
/// <item>Telemetry every 0.3 s of session time - the interval RLT's gap analysis
/// runs at - and whenever a car beats its top speed, DRS open and closed
/// counted apart as RLT does.</item>
/// <item>Status when a car's visual tyre compound or tyre age changes.</item>
/// <item>Damage when a car's tyre wear changes as RLT rounds it: the average of
/// the two most worn tyres.</item>
/// </list>
/// </summary>
internal sealed class ResultPacketSampler
{
    internal const float TelemetryInterval = 0.3f;

    private const int HeaderSize = 29;
    private const int MaxCars = 24;

    private const byte Participants = 4;
    private const byte CarTelemetry = 6;
    private const byte CarStatus = 7;
    private const byte CarDamage = 10;

    // Per car, offsets inside one car's block.
    private const int SpeedOffset = 0;     // uint16, km/h
    private const int DrsOffset = 18;      // uint8, 1 = open
    private const int CompoundOffset = 26; // uint8, visual tyre compound
    private const int TyreAgeOffset = 27;  // uint8, laps

    // Session time is a float that drifts; without some slack a frame landing
    // a hair under the interval would push the sample out by one frame.
    private const float IntervalSlack = 0.005f;

    private sealed record Layout(int Cars, int TelemetryStride, int StatusStride, int DamageStride);

    private static Layout? LayoutFor(ushort packetFormat) => packetFormat switch
    {
        2024 => new Layout(22, 60, 55, 42),
        2025 => new Layout(22, 60, 55, 46),
        2026 => new Layout(24, 59, 59, 46),
        _ => null,
    };

    private ulong _sessionUid;
    private int _activeCars = -1;

    private bool _telemetryDue;
    private bool _statusDue;
    private bool _damageDue;

    private float? _lastTelemetryTime;
    private readonly int[] _topSpeedDrs = new int[MaxCars];
    private readonly int[] _topSpeedNoDrs = new int[MaxCars];
    private readonly byte[] _compound = new byte[MaxCars];
    private readonly byte[] _tyreAge = new byte[MaxCars];
    private readonly int?[] _tyreWear = new int?[MaxCars];

    public ResultPacketSampler() => Reset();

    /// <summary>Starts over, for a new file.</summary>
    public void Reset()
    {
        _sessionUid = 0;
        _activeCars = -1;
        _lastTelemetryTime = null;
        Array.Clear(_compound);
        Array.Clear(_tyreAge);
        Array.Clear(_tyreWear);
        ResetTopSpeeds();
        ScheduleSnapshots();
    }

    /// <summary>
    /// Looks at every packet that passed the game handler, so it can follow
    /// participants, and says whether a packet the upstream filter dropped
    /// should be written after all.
    /// </summary>
    public bool ShouldKeep(byte[] packet, bool keptByFilter)
    {
        if (packet.Length < HeaderSize || LayoutFor(BitConverter.ToUInt16(packet, 0)) is not { } layout)
            return false;

        var sessionUid = BitConverter.ToUInt64(packet, 7);
        if (sessionUid == 0)
            return false; // main menu

        if (sessionUid != _sessionUid)
        {
            Reset();
            _sessionUid = sessionUid;
        }

        var packetId = packet[6];

        if (packetId == Participants)
        {
            ObserveParticipants(packet);
            return false;
        }

        if (keptByFilter)
            return false;

        return packetId switch
        {
            CarTelemetry => KeepTelemetry(packet, layout),
            CarStatus => KeepStatus(packet, layout),
            CarDamage => KeepDamage(packet, layout),
            _ => false,
        };
    }

    private void ObserveParticipants(byte[] packet)
    {
        if (packet.Length <= HeaderSize)
            return;

        int activeCars = packet[HeaderSize];
        if (activeCars == _activeCars)
            return;

        _activeCars = activeCars;

        // The cars RLT now knows about need their values again; top speeds
        // start over so the snapshot re-establishes them on RLT's side too.
        ResetTopSpeeds();
        ScheduleSnapshots();
    }

    private bool KeepTelemetry(byte[] packet, Layout layout)
    {
        if (packet.Length < HeaderSize + layout.Cars * layout.TelemetryStride)
            return false;

        var sessionTime = BitConverter.ToSingle(packet, 15);

        var keep = _telemetryDue
            || _lastTelemetryTime is not { } last
            || Math.Abs(sessionTime - last) >= TelemetryInterval - IntervalSlack;

        for (var car = 0; car < layout.Cars && !keep; car++)
        {
            var (speed, drsOpen) = ReadSpeed(packet, layout, car);
            keep = speed > (drsOpen ? _topSpeedDrs[car] : _topSpeedNoDrs[car]);
        }

        if (!keep)
            return false;

        for (var car = 0; car < layout.Cars; car++)
        {
            var (speed, drsOpen) = ReadSpeed(packet, layout, car);
            var top = drsOpen ? _topSpeedDrs : _topSpeedNoDrs;
            if (speed > top[car])
                top[car] = speed;
        }

        _lastTelemetryTime = sessionTime;
        _telemetryDue = false;
        return true;
    }

    private static (int Speed, bool DrsOpen) ReadSpeed(byte[] packet, Layout layout, int car)
    {
        var at = HeaderSize + car * layout.TelemetryStride;
        return (BitConverter.ToUInt16(packet, at + SpeedOffset), packet[at + DrsOffset] == 1);
    }

    private bool KeepStatus(byte[] packet, Layout layout)
    {
        if (packet.Length < HeaderSize + layout.Cars * layout.StatusStride)
            return false;

        var keep = _statusDue;

        for (var car = 0; car < layout.Cars; car++)
        {
            var at = HeaderSize + car * layout.StatusStride;
            var compound = packet[at + CompoundOffset];
            var age = packet[at + TyreAgeOffset];

            if (compound != _compound[car] || age != _tyreAge[car])
            {
                keep = true;
                _compound[car] = compound;
                _tyreAge[car] = age;
            }
        }

        _statusDue = false;
        return keep;
    }

    private bool KeepDamage(byte[] packet, Layout layout)
    {
        if (packet.Length < HeaderSize + layout.Cars * layout.DamageStride)
            return false;

        var keep = _damageDue;

        for (var car = 0; car < layout.Cars; car++)
        {
            var wear = TyreWear(packet, HeaderSize + car * layout.DamageStride);
            if (wear != _tyreWear[car])
            {
                keep = true;
                _tyreWear[car] = wear;
            }
        }

        _damageDue = false;
        return keep;
    }

    /// <summary>
    /// Tyre wear the way RLT reduces it: none while all four read zero,
    /// otherwise the two most worn tyres averaged and rounded, half to even.
    /// </summary>
    internal static int? TyreWear(byte[] packet, int at)
    {
        Span<float> wear = stackalloc float[4];
        for (var i = 0; i < 4; i++)
            wear[i] = BitConverter.ToSingle(packet, at + i * 4);

        var allZero = true;
        foreach (var w in wear)
            allZero &= Math.Abs(w) < 1e-6f;
        if (allZero)
            return null;

        wear.Sort();
        return Convert.ToInt32(Math.Round((wear[3] + wear[2]) / 2f));
    }

    private void ResetTopSpeeds()
    {
        Array.Clear(_topSpeedDrs);
        Array.Clear(_topSpeedNoDrs);
    }

    private void ScheduleSnapshots()
    {
        _telemetryDue = true;
        _statusDue = true;
        _damageDue = true;
    }
}
