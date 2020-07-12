namespace D2NG.BNCS
{
    public class BncsContext
    {
        public uint ClientToken { get; set; }
        public uint ServerToken { get; set; }
        public string Username { get; internal set; }
        public string KeyOwner { get; internal set; }
        public string Gamefolder { get; internal set; }
    }
}