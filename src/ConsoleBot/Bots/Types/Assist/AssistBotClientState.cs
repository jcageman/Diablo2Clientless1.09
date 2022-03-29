using D2NG.Core.D2GS.Objects;

namespace ConsoleBot.Bots.Types.Assist
{
    public class AssistBotClientState
    {
        public bool ShouldHeal { get; set; } = false;
        public bool ShouldGoToTown { get; set; }  = false;
        public bool NextGame { get; set; } = false;
        public bool ShouldStop { get; set; } = false;
        public bool ShouldFollow { get; set; } = true;
        public bool GoNextLevel { get; set; } = false;
        public Waypoint? GoToWaypoint { get; set; }
    }
}
