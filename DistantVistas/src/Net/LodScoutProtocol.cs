using ProtoBuf;

namespace DistantVistas.Net;

/// <summary>
/// Login-bake scout anchors. Separate channel from the cache-assist handshake so
/// adding these messages does not break 1.0.29 assist peers.
///
/// Server WorldManager loads columns around each scout the same way it would if a
/// player stood there (KeepLoaded + ForceSend), then UnloadChunkColumn on despawn.
/// </summary>
public static class LodScoutNet
{
    public const string ChannelName = "distantvistas.scout";
}

[ProtoContract]
public class ScoutAnchorUp
{
    [ProtoMember(1)] public long Key;
    [ProtoMember(2)] public int Cx;
    [ProtoMember(3)] public int Cz;
    [ProtoMember(4)] public int Radius;
    [ProtoMember(5)] public int Dimension;
    [ProtoMember(6)] public double X;
    [ProtoMember(7)] public double Y;
    [ProtoMember(8)] public double Z;
    /// <summary>When the 16-scout cap is full, evict the farthest hold instead of queueing forever.</summary>
    [ProtoMember(9)] public bool Priority;
}

[ProtoContract]
public class ScoutAnchorDown
{
    [ProtoMember(1)] public long Key;
}

[ProtoContract]
public class ScoutAnchorsClear
{
    [ProtoMember(1)] public bool Unused;
}

[ProtoContract]
public class ScoutHostStatus
{
    [ProtoMember(1)] public long Sequence;
    [ProtoMember(2)] public bool Pressure;
    [ProtoMember(3)] public int PriorityPending;
    [ProtoMember(4)] public int PriorityInFlight;
    [ProtoMember(5)] public int ForceSendPending;
    [ProtoMember(6)] public long OldestInFlightMs;
    [ProtoMember(7)] public long PriorityCompleted;
    [ProtoMember(8)] public long ServerTimeMs;
}
