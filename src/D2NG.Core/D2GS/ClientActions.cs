using D2NG.Core.Tracing;
using System;

namespace D2NG.Core.D2GS;

/// <summary>
/// The client state changes a <see cref="Game"/> method makes without the server's say, applied the
/// same way live and in replay.
/// </summary>
internal static class ClientActions
{
    private const uint NoLocation = uint.MaxValue;

    public static uint Pack(Point location)
        => location == null ? NoLocation : location.X | ((uint)location.Y << 16);

    public static void Apply(GameData data, ClientAction action, uint argument)
    {
        switch (action)
        {
            case ClientAction.Lift:
                data.Lifted(argument);
                data.SellRequested.TryRemove(argument, out _);
                // A use the server ignored - an unreadable scroll - leaves its mark behind, and the lift's removal then
                // read as the item used up: the client believed its cursor empty while the server held the scroll there,
                // and ignored every interaction after it.
                data.UseRequested.TryRemove(argument, out _);
                break;
            case ClientAction.Use:
                data.UseRequested[argument] = 0;
                break;
            case ClientAction.Sell:
                data.SellRequested[argument] = 0;
                break;
            case ClientAction.ForgetWaypointSelection:
                data.Me.LastSelectedWaypointId = 0;
                break;
            case ClientAction.OpenShop:
                data.ShopOpen = true;
                data.ShopTracked = true;
                break;
            case ClientAction.RemoveMerchantItems:
                data.ShopOpen = false;
                foreach (var item in data.Items)
                {
                    if (item.Value.IsInMerchantTab())
                    {
                        data.Items.TryRemove(item.Key, out _);
                    }
                }
                break;
            case ClientAction.Move:
                data.Me.Location = argument == NoLocation ? null : new Point((ushort)argument, (ushort)(argument >> 16));
                break;
            case ClientAction.ClearCursor:
                data.CursorCleared();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown client action");
        }
    }
}
