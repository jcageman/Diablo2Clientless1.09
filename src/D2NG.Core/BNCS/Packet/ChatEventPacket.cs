using D2NG.Core.BNCS.Exceptions;
using D2NG.Core.Extensions;
using System.IO;
using System.Text;

namespace D2NG.Core.BNCS.Packet
{
    public class ChatEventPacket : BncsPacket
    {
        public Eid Eid { get; }
        public uint UserFlags { get; }
        public uint Ping { get; }
        public string Username { get; }
        public string Text { get; }
        public ChatEventPacket(byte[] packet) : base(packet)
        {
            BinaryReader reader = new BinaryReader(new MemoryStream(packet), Encoding.ASCII);
            if (PrefixByte != reader.ReadByte())
            {
                throw new BncsPacketException("Not a valid BNCS Packet");
            }
            if ((byte)Sid.CHATEVENT != reader.ReadByte())
            {
                throw new BncsPacketException("Expected type was not found");
            }
            if (packet.Length != reader.ReadUInt16())
            {
                throw new BncsPacketException("Packet length does not match");
            }

            Eid = (Eid)reader.ReadUInt32();
            UserFlags = reader.ReadUInt32();
            Ping = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();

            Username = reader.ReadNullTerminatedString();
            Text = reader.ReadNullTerminatedString();

            reader.Close();
        }

        public string RenderText()
        {
            switch (Eid)
            {
                case Eid.JOIN:
                    return $"{Username} has joined the channel";
                case Eid.LEAVE:
                    return $"{Username} has left the channel";
                case Eid.WHISPER:
                    return $"From <{Username}>: {Text}";
                case Eid.TALK:
                    return $"<{Username}>: {Text}";
                case Eid.CHANNEL:
                    return $"Joined channel: {Text}";
                case Eid.INFO:
                    return $"INFO: {Text}";
                case Eid.ERROR:
                    return $"ERROR: {Text}";
                case Eid.EMOTE:
                    return $"<{Username} {Text}>";
                default:
                    return $"Unhandled EID: {Eid.ToString()} {Text}";
            }
        }
    }
}