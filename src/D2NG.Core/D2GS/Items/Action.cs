namespace D2NG.Core.D2GS.Items
{
    public enum Action
    {
        AddToGround = 0,
        GroundToCursor = 1,
        DropToGround = 2,
        OnGround = 3,
        PutInContainer = 4,
        RemoveFromContainer = 5,
        Equip = 6,
        IndirectlySwapBodyItem = 7,
        Unequip = 8,
        SwapBodyItem = 9,
        AddQuantity = 0x0A,
        AddToShop = 0x0B,
        RemoveFromShop = 0x0C,
        SwapInContainer = 0x0D,
        PutInBelt = 0x0E,
        RemoveFromBelt = 0x0F,
        SwapInBelt = 0x10,
        AutoUnequip = 0x11,
        ToCursor = 0x12,
        ItemInSocket = 0x13,
        UpdateStats = 0x15,
        WeaponSwitch = 0x17
    }
}