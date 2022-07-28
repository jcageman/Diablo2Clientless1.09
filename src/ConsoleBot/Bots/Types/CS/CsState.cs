using D2NG.Core.D2GS;

namespace ConsoleBot.Bots.Types.CS
{
    internal class CsState
    {
        public uint? TeleportId { get; set; }

        public Point KillLocation { get; set; }

        public bool TeleportHasChanged(CsState otherState)
        {
            return TeleportId != otherState.TeleportId;
        }
    }
}
