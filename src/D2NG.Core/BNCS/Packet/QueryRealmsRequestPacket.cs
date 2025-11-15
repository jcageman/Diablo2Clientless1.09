namespace D2NG.Core.BNCS.Packet;

public class QueryRealmsRequestPacket : BncsPacket
{
    public QueryRealmsRequestPacket() :
        base(BuildPacket(Sid.QUERYREALMS2))
    {
    }
}
