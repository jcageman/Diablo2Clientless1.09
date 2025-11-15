namespace D2NG.Core.D2GS.Enums;

public enum PlayerRelationType : byte
{
    AllowLoot = 0x01,
    AllowLoot2 = 0x02,
    GoneHostile = 0x03,
    NoLongerHostile = 0x04,
    InvitesYouToParty = 0x05,
    CancelsPartyInvite = 0x06,
    IsNowInYourParty = 0x07,
    YouAreNowInIdsParty = 0x08,
    LeftParty = 0x09,
    HasRemovedLoot = 0x0B,
}
