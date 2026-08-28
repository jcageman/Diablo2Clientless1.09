using D2NG.Core.D2GS.Exceptions;
using System.IO;
using System.Text;

namespace D2NG.Core.D2GS.Packet.Incoming;

/// <summary>
/// Tells the client which party a player belongs to. A party id of <see cref="NoParty"/> means the
/// player is on their own, which is what every player starts as.
/// </summary>
/// <remarks>
/// Needed because quest credit for a rush depends on actually being partied, and sending an invite is
/// no guarantee that it was accepted: a run that assumed it was produced a kill nobody got credit for.
/// </remarks>
internal class AssignPlayerToPartyPacket : D2gsPacket
{
    public const ushort NoParty = 0xFFFF;

    public AssignPlayerToPartyPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new BinaryReader(new MemoryStream(Raw), Encoding.ASCII);
        var id = reader.ReadByte();
        if ((InComingPacket)id != InComingPacket.AssignPlayerToParty)
        {
            throw new D2GSPacketException($"Invalid Packet Id {id}");
        }
        PlayerId = reader.ReadUInt32();
        PartyId = reader.ReadUInt16();
    }

    public uint PlayerId { get; }

    public ushort PartyId { get; }

    public bool IsInParty => PartyId != NoParty;
}
