namespace D2NG.D2GS
{
    public enum ClickType
    {
        CancelTrade = 0x02,
        AcceptTradeRequest = 0x03,
        PressAcceptButton = 0x04,
        UnclickTheAcceptButton = 0x07,
        RefreshTradeScreen = 0x08,
        CloseStash = 0x12,
        MoveGoldFromStashToInventory = 0x13,
        MoveGoldFromInventoryToStash = 0x14,
        CloseHoradricCube = 0x17,
        TransmuteItems = 0x18
    }
}
