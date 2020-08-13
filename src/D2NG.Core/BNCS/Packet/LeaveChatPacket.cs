namespace D2NG.Core.BNCS.Packet
{
    public class LeaveChatPacket : BncsPacket
    {
        public LeaveChatPacket() : base(BuildPacket(Sid.LEAVECHAT))
        {
        }
    }
}