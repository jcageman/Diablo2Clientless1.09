namespace D2NG.D2GS.Items.Containers
{
    public class Stash : Container
    {
        public Stash() : base(10, 10)
        {
        }

        protected override Point GetItemLocation(Item item)
        {
            if(item.Container == ContainerType.Stash)
            {
                return item.Location;
            }
            else
            { // stash2 starts at 8-9.
                return new Point(item.Location.X, (ushort)(item.Location.Y + 8));
            }
        }
    }
}