using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Objects;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace ConsoleBot.Helpers
{
    public static class WayPointHelpers
    {
        public static EntityCode MapTownWayPoint(this Act act)
        {
            switch (act)
            {
                case Act.Act1:
                    return EntityCode.WaypointAct1;
                case Act.Act2:
                    return EntityCode.WaypointAct2;
                case Act.Act3:
                    return EntityCode.WaypointAct3;
                case Act.Act4:
                    return EntityCode.WaypointAct4;
                case Act.Act5:
                    return EntityCode.WaypointAct5;
            }

            throw new InvalidOperationException($"specified invalid act {act}");
        }
    }
}
