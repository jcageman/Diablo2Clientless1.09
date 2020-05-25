using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace D2NG.D2GS.Helpers
{
    public static class ByteHelpers
    {
        public static byte[] StringToByteArray(this string hex)
        {
            return Enumerable.Range(0, hex.Length)
                             .Where(x => x % 2 == 0)
                             .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                             .ToArray();
        }

        public static string ByteArrayToString(this byte[] ba)
        {
            return BitConverter.ToString(ba).Replace("-", "");
        }

        public static string HexStringToByteString(this string hex)
        {
            return Encoding.ASCII.GetString(StringToByteArray(hex));
        }

        public static string ToPrintString(this List<byte> bytes)
        {
            return bytes.ToArray().ToPrintString();
        }

        public static string ToPrintString(this byte[] bytes)
        {
            return "0x" + BitConverter.ToString(bytes).Replace("-", ", 0x");
        }
    }
}
