using D2NG.Core.D2GS.Objects;

namespace ConsoleBot.Bots.Types.Assist
{
    public class AssistBotClientState
    {
        public bool ShouldHeal { get; set; }
        public bool ShouldGoToTown { get; set; }
        public bool NextGame { get; set; }
        public bool ShouldStop { get; set; }
        public bool ShouldFollow { get; set; } = true;
        public bool GoNextLevel { get; set; }
        public Waypoint? GoToWaypoint { get; set; }
    }
}
