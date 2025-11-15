namespace D2NG.Core.D2GS.Enums;

public enum ButtonAction : byte
{
    APlayerWantsToTrade = 0x01,
    APlayerHasPressedAcceptButton = 0x05,
    NoAcceptButtonsCanBeClicked = 0x06,
    YouDontHaveRoomToAcceptTheItems = 0x09,
    APlayerAcceptedTrade = 0x0C,
    YouHaveTradedSomeItems = 0x0D,
    TheAcceptButtonHasBeenDisabled = 0x0E,
    TheAcceptButtonHasBeenReEnabled = 0x0F
}
