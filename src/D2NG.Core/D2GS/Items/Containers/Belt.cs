using System.Collections.Generic;
using System.Linq;

namespace D2NG.Core.D2GS.Items.Containers
{
    public class Belt : Container
    {
        public Belt() : base(4, 4)
        {
        }

        private bool IsHealthPotion(Item item)
        {
            return item.Classification == ClassificationType.HealthPotion;
        }

        private bool IsManaPotion(Item item)
        {
            return item.Classification == ClassificationType.ManaPotion;
        }

        public int NumOfHealthPotions()
        {
            return _items.Values.Where(i => IsHealthPotion(i)).Count();
        }

        public Item FirstOrDefaultHealthPotion()
        {
            return _items.Values.FirstOrDefault(i => IsHealthPotion(i));
        }

        public int NumOfManaPotions()
        {
            return _items.Values.Where(i => IsManaPotion(i)).Count();
        }

        public Item FirstOrDefaultManaPotion()
        {
            return _items.Values.FirstOrDefault(i => IsManaPotion(i));
        }

        public List<Item> GetHealthPotionsInSlots(List<int> slots)
        {
            return _items.Values.Where(i => IsHealthPotion(i) && slots.Contains(i.Location.X)).ToList();
        }

        public List<Item> GetManaPotionsInSlots(List<int> slots)
        {
            return _items.Values.Where(i => IsManaPotion(i) && slots.Contains(i.Location.X)).ToList();
        }

        public List<Item> GetHealthPotionsInSlot(int slot)
        {
            return _items.Values.Where(i => IsHealthPotion(i) && i.Location.X == slot).ToList();
        }

        public List<Item> GetManaPotionsInSlot(int slot)
        {
            return _items.Values.Where(i => IsManaPotion(i) && i.Location.X == slot).ToList();
        }
    }
}
