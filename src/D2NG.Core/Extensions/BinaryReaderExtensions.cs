using System.IO;
using System.Text;

namespace D2NG.Core.Extensions
{
    public static class BinaryReaderExtensions
    {
        public static string ReadNullTerminatedString(this BinaryReader reader)
        {
            var text = new StringBuilder();
            while (reader.PeekChar() != 0)
            {
                text.Append(reader.ReadChar());
            }
            reader.ReadChar();
            return text.ToString();
        }
    }
}
