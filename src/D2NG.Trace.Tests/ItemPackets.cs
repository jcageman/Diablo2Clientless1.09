using D2NG.Core.D2GS.Items;
using D2NG.Core.Tracing;
using System;
using System.Collections.Generic;
using System.Text;
using Action = D2NG.Core.D2GS.Items.Action;

namespace D2NG.Trace.Tests;

/// <summary>
/// Item packets (0x9C, and 0x9D with an owner) for simple items, written bit by bit in the layout ParseItemPacket reads,
/// and the client's item requests and the server's answers around them.
/// </summary>
internal static class ItemPackets
{
    private const int Identified = 4;
    private const int Simple = 21;

    public static byte[] Item(Action action, uint id, string code, ContainerType container = ContainerType.Inventory, int x = 0, int y = 0,
        uint? owner = null, DirectoryType slot = DirectoryType.NotApplicable, bool identified = true, (ushort X, ushort Y)? ground = null, uint gold = 0,
        uint? quantity = null)
    {
        var bits = new BitWriter();
        bits.Write(owner == null ? 0x9Cu : 0x9Du, 8);
        bits.Write((uint)action, 8);
        bits.Write(0x10, 8);
        bits.Write(0, 8);
        bits.Write(id, 32);
        if (owner is { } unit)
        {
            bits.Write(0, 8);
            bits.Write(unit, 32);
        }

        var status = (quantity == null ? 1u << Simple : 0) | (identified ? 1u << Identified : 0) | (slot != DirectoryType.NotApplicable ? 1u : 0);
        bits.Write(status, 32);
        bits.Write(0, 8);
        bits.Write(0, 2);
        if (ground is { } at)
        {
            bits.Write(3, 3);
            bits.Write(at.X, 16);
            bits.Write(at.Y, 16);
        }
        else
        {
            bits.Write(0, 3);
            bits.Write((uint)slot, 4);
            bits.Write((uint)x, 4);
            bits.Write((uint)y, 3);
            bits.Write((uint)container, 4);
        }

        foreach (var c in Encoding.ASCII.GetBytes(code + " "))
        {
            bits.Write(c, 8);
        }

        if (code == "gld")
        {
            bits.Write(0, 1);
            bits.Write(gold, 12);
        }

        bits.Write(0, 3);
        if (quantity is { } amount)
        {
            bits.Write(1, 7);
            bits.Write((uint)QualityType.Normal, 4);
            bits.Write(0, 2);
            bits.Write(amount, 9);
            if (identified)
            {
                bits.Write(0x1FF, 9);
            }
        }

        return bits.ToArray();
    }

    /// <summary>
    /// A normal stackable item that is neither armor nor a weapon (a quiver) in the full layout, whose quantity rides in the
    /// packet; a simple item's does not.
    /// </summary>
    public static byte[] Stack(Action action, uint id, string code, uint quantity, uint owner, DirectoryType slot = DirectoryType.NotApplicable,
        ContainerType container = ContainerType.Unspecified, int x = 0, int y = 0)
        => Item(action, id, code, container, x, y, owner, slot, quantity: quantity);

    /// <summary>The server's quantity update for an item (0x3E, type 0x46), as it counts a worn quiver or javelins down.</summary>
    public static byte[] Quantity(uint id, ushort amount) => StatUpdated(id, 0x46, amount);

    /// <summary>A belt potion: the packet numbers belt cells 0-15, four to a row.</summary>
    public static byte[] Belt(Action action, uint id, int column, int row, string code = "hp5")
        => Item(action, id, code, ContainerType.Unspecified, row * 4 + column, 0);

    public static byte[] Owned(Action action, uint id, string code, uint owner, ContainerType container = ContainerType.Inventory, int x = 0, int y = 0, DirectoryType slot = DirectoryType.NotApplicable, bool identified = true)
        => Item(action, id, code, container, x, y, owner, slot, identified);

    public static byte[] OnGround(uint id, string code, ushort x = 5060, ushort y = 1905, uint gold = 0)
        => Item(Action.AddToGround, id, code, ground: (x, y), gold: gold);

    /// <summary>An unidentified ring of <paramref name="quality"/> on the ground in the full layout - level and quality, nothing after - as a fresh drop (AddToGround) or a player's (DropToGround) reaches every client.</summary>
    public static byte[] Loot(uint id, QualityType quality, ushort x, ushort y, Action action = Action.AddToGround)
    {
        var bits = new BitWriter();
        bits.Write(0x9C, 8);
        bits.Write((uint)action, 8);
        bits.Write(0x10, 8);
        bits.Write(0, 8);
        bits.Write(id, 32);
        bits.Write(0, 32);
        bits.Write(0, 8);
        bits.Write(0, 2);
        bits.Write(3, 3);
        bits.Write(x, 16);
        bits.Write(y, 16);
        foreach (var c in Encoding.ASCII.GetBytes("rin "))
        {
            bits.Write(c, 8);
        }

        bits.Write(0, 3);
        bits.Write(60, 7);
        bits.Write((uint)quality, 4);
        bits.Write(0, 2);
        if (quality is QualityType.Rare or QualityType.Crafted)
        {
            bits.Write(0, 6);
        }

        return bits.ToArray();
    }

    public static byte[] Lift(uint id) => [0x19, .. BitConverter.GetBytes(id)];

    /// <summary>A client action the bot records beside its request, as the replay reads it.</summary>
    public static byte[] ClientDid(ClientAction action, uint argument) => [(byte)action, .. BitConverter.GetBytes(argument)];

    public static byte[] Put(uint id, uint x, uint y, uint buffer) => [0x18, .. BitConverter.GetBytes(id), .. BitConverter.GetBytes(x), .. BitConverter.GetBytes(y), .. BitConverter.GetBytes(buffer)];

    public static byte[] Equip(uint id, DirectoryType slot) => [0x1A, .. BitConverter.GetBytes(id), .. BitConverter.GetBytes((uint)slot)];

    public static byte[] TakeOff(DirectoryType slot) => [0x1C, .. BitConverter.GetBytes((ushort)slot)];

    public static byte[] UseBelt(uint id) => [0x26, .. BitConverter.GetBytes(id), 0, 0, 0, 0, 0, 0, 0, 0];

    public static byte[] Sell(uint npc, uint id) => [0x33, .. BitConverter.GetBytes(npc), .. BitConverter.GetBytes(id), 0, 0, 0, 0, 0, 0, 0, 0];

    public static byte[] Buy(uint npc, uint listing, bool gamble) => [0x32, .. BitConverter.GetBytes(npc), .. BitConverter.GetBytes(listing), (byte)(gamble ? 0x02 : 0x00), 0, 0, 0, 0, 0, 0, 0];

    public static byte[] IdentifyGambled(uint id) => [0x37, .. BitConverter.GetBytes(id)];

    public static byte[] ToCube(uint id, uint cube) => [0x2A, .. BitConverter.GetBytes(id), .. BitConverter.GetBytes(cube)];

    public static byte[] Transmute(uint cube) => [0x4C, .. BitConverter.GetBytes(cube)];

    public static byte[] AcceptTrade() => [0x4F, 0x04, 0, 0, 0, 0, 0];

    public static byte[] Vendor(byte transaction, byte result, uint id, int goldAfter) => [0x2A, transaction, result, 0, 0, 0, 0, .. BitConverter.GetBytes(id), .. BitConverter.GetBytes(goldAfter)];

    public static byte[] Used(uint id) => [0x3F, 0x00, .. BitConverter.GetBytes(id), 0, 0];

    public static byte[] Removed(uint id) => [0x0A, 0x04, .. BitConverter.GetBytes(id)];

    public static byte[] Gold(byte attribute, uint value) => [0x1F, attribute, .. BitConverter.GetBytes(value)];

    /// <summary>A small amount of gold added to the person (0x19), as a party member's share of a pile arrives.</summary>
    public static byte[] GoldAdded(byte amount) => [0x19, amount];

    /// <summary>The sound the realm plays for a player picking an item up (0x2C, sound 1), sent only to that player.</summary>
    public static byte[] PickUpSound(uint player) => [0x2C, 0x00, .. BitConverter.GetBytes(player), 0x01, 0x00];

    public static byte[] Use(uint id) => [0x20, .. BitConverter.GetBytes(id), 0, 0, 0, 0, 0, 0, 0, 0];

    public static byte[] ToBelt(uint id) => [0x63, .. BitConverter.GetBytes(id)];

    /// <summary>The client dropping gold from the person (0x50): unit type 1 (the player) and the amount.</summary>
    public static byte[] DropGold(uint amount) => [0x50, 0x01, 0x00, 0x00, 0x00, .. BitConverter.GetBytes(amount)];

    /// <summary>Identifying every item at an NPC (0x34), as Cain does it.</summary>
    public static byte[] IdentifyAll(uint npc) => [0x34, .. BitConverter.GetBytes(npc)];

    public static byte[] SwapBody(uint id, DirectoryType slot) => [0x1D, .. BitConverter.GetBytes(id), .. BitConverter.GetBytes((uint)slot)];

    public static byte[] RepairAll(uint npc) => [0x35, .. BitConverter.GetBytes(npc), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x80];

    /// <summary>An item's stat the server updates (0x3E), as a repair restores durability.</summary>
    public static byte[] StatUpdated(uint id, byte stat, ushort amount) => [0x3E, .. BitConverter.GetBytes(id), 0, stat, .. BitConverter.GetBytes(amount), 0, 0];

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bit;

        public void Write(uint value, int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (_bit % 8 == 0)
                {
                    _bytes.Add(0);
                }

                if (((value >> i) & 1) != 0)
                {
                    _bytes[^1] |= (byte)(1 << (_bit % 8));
                }

                _bit++;
            }
        }

        public byte[] ToArray() => [.. _bytes];
    }
}
