namespace D2NG.Core.D2GS.Enums
{
    public enum EventType : byte
    {
        DroppedDueToTimeOut,
        DroppedDueToErrors,
        JoinedTheGame,
        QuiteTheGame,
        IsNotInGame,
        IsNotLoggedIn,
        WasKilledBy,
        RelationChange,
        IsBusy,
        WaitAShortTimeForTrade,
        ItemIsNotAccessible,
        YouHaveBeenSquelched,
        NotEnoughMana,
        RealmIsGoingDown,
        YouMustWaitBeforeHostile,
    }
}
