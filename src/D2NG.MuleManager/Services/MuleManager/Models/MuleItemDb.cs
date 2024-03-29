﻿using D2NG.Core.D2GS.Items;
using System.Collections.Generic;

namespace D2NG.MuleManager.Services.MuleManager.Models
{
    public class MuleItemDb
    {
        public string Id { get; set; }
        public string AccountName { get; set; }
        public string CharacterName { get; set; }
        public string ItemName { get; set; }
        public string QualityType { get; set; }
        public string ClassificationType { get; set; }
        public bool Ethereal { get; set; }
        public uint Level { get; set; }
        public uint Sockets { get; set; }
        public List<StatDb> Stats { get; set; }
        public List<string> StatTypes { get; set; }
    }
}
