using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Helpers;
using System.Collections;
using System.Text;

namespace D2NG.Core.MCP
{
    public class Character
    {
        private readonly byte[] _stats;

        public string Name { get; }

        public Character(string name, byte[] stats)
        {
            Name = name;
            _stats = stats;
            Stats = _stats.ToPrintString();
            var flagsBits = new BitArray(new byte[] { _stats[26] });
            IsHardCore = flagsBits[2];
            IsExpansion = flagsBits[5];
        }

        public CharacterClass Class { get => (CharacterClass)((_stats[13] - 0x01) & 0xFF); }

        public uint Level { get => _stats[25]; }

        public string FlagsString { get => ToBitString(new BitArray(new byte[] { _stats[26] })); }

        public bool IsHardCore { get; private set; }
        public bool IsExpansion { get; private set; }
        public string Stats { get; set; }

        public static string ToBitString(BitArray bits)
        {
            var sb = new StringBuilder();

            for (int i = 0; i < bits.Count; i++)
            {
                char c = bits[i] ? '1' : '0';
                sb.Append(c);
            }

            return sb.ToString();
        }
    }
}