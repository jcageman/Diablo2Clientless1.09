namespace D2NG.Core.D2GS.Objects;

public enum EntityState : byte
{
    Alive = 0x00,
    Activating = 0x01,
    Activated = 0x02,
    BeingHit = 0x06,
    Dieing = 0x08,
    Dead = 0x09,
}
