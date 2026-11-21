using D2NG.Core.D2GS.Packet;

namespace D2NG.Core.D2GS;

/// <summary>A position the server stated for this character.</summary>
/// <param name="Timestamp">When it arrived, on the game's clock.</param>
/// <param name="Believed">Where the client held itself just before; null when it held no position.</param>
internal sealed record PositionConfirmation(Point Location, long Timestamp, InComingPacket Packet, Point Believed);
