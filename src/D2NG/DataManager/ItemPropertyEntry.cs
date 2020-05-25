using D2NG.D2GS.Items;
using System;
using System.Collections.Generic;
using System.Text;

namespace D2NG.DataManager
{
    class ItemPropertyEntry
    {
        public string Name { get; }

        public StatType StatType { get; }
        public int SaveBits { get; }
        public int SaveParamBits { get; }
        public int SaveAdd { get; }

        public ItemPropertyEntry(string name, StatType statType, int saveBits, int saveParamBits, int saveAdd)
        {
            Name = name;
            StatType = statType;
            SaveBits = saveBits;
            SaveParamBits = saveParamBits;
            SaveAdd = saveAdd;
        }
    }
}
