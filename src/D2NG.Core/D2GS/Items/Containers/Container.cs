using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Core.D2GS.Items.Containers
{
    public class Container
    {
        public uint Width { get; }

        public uint Height { get; }

        private bool[,] Buffer { get; set; }

        protected ConcurrentDictionary<uint, Item> _items { get; set; } = new ConcurrentDictionary<uint, Item>();

        public List<Item> Items { get => _items.Values.ToList(); }

        public Container(uint width, uint height)
        {
            Width = width;
            Height = height;

            Buffer = new bool[height, width];
        }

        virtual protected Point GetItemLocation(Item item)
        {
            return item.Location;
        }

        private void SetBuffer(Item item, bool value)
        {
            var itemLocation = GetItemLocation(item);
            for (int y = 0; y < item.Height; y++)
            {
                for (int x = 0; x < item.Width; x++)
                {
                    Buffer[itemLocation.Y + y, itemLocation.X + x] = value;
                }
            }
        }

        public void Add(Item item)
        {
            _items[item.Id] = item;
            SetBuffer(item, true);
        }

        public Item FindItemByName(string name)
        {
            return _items.FirstOrDefault(i => i.Value.Name == name).Value;
        }

        public Item FindItemById(uint itemId)
        {
            return _items.GetValueOrDefault(itemId);
        }

        public bool UpdateItem(Item oldItem, Item newItem)
        {
            return _items.TryUpdate(oldItem.Id, newItem, oldItem);
        }

        public void Remove(Item item) => Remove(item.Id);

        public void Remove(uint id)
        {
            if (_items.Remove(id, out Item it))
            {
                SetBuffer(it, false);
            }
        }

        public Point FindFreeSpace(Item item)
        {
            for (ushort y = 0; y < Height; y++)
            {
                for (ushort x = 0; x < Width; x++)
                {
                    var point = new Point(x, y);
                    if (SpaceIsFree(point, item.Width, item.Height))
                    {
                        return point;
                    }
                }
            }
            return null;
        }

        private bool SpaceIsFree(Point point, ushort itemWidth, ushort itemHeight)
        {
            if ((point.X + itemWidth > Width) || (point.Y + itemHeight > Height))
            {
                return false;
            }
            for (int y = point.Y; y < point.Y + itemHeight; y++)
            {
                for (int x = point.X; x < point.X + itemWidth; x++)
                {
                    if (Buffer[y, x])
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        public override string ToString()
        {
            return string.Join("\n", Buffer.OfType<bool>()
                         .Select((value, index) => new { value, index })
                         .GroupBy(x => x.index / Buffer.GetLength(1))
                         .Select(x => string.Join("", x.Select(y => y.value ? "██" : "░░"))));
        }
    }
}
