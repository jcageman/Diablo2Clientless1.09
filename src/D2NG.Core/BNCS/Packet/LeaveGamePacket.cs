namespace D2NG.Core.BNCS.Packet
{
    internal class LeaveGamePacket : BncsPacket
    {
        public LeaveGamePacket() :
            base(
                BuildPacket(
                    Sid.LEAVEGAME
                )
            )
        {
        }
    }
}