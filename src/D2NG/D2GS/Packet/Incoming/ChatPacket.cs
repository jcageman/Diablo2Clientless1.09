using D2NG.Extensions;
using Serilog;
using System.IO;
using System.Text;
using D2NG.D2GS.Exceptions;

namespace D2NG.D2GS.Packet.Incoming
{

    public class ChatPacket : D2gsPacket
    {
        public byte ChatType { get; }

        public byte EntityType { get; }

        public uint EntityId { get; }

        public string CharacterName { get; }

        public string Message { get; }
        public ChatPacket(D2gsPacket packet) : base(packet.Raw)
        {
            var reader = new BinaryReader(new MemoryStream(Raw), Encoding.ASCII);
            var id = reader.ReadByte();
            if ((InComingPacket)id != InComingPacket.ReceiveChat)
            {
                throw new D2GSPacketException($"Invalid Packet Id {id}");
            }

            ChatType = reader.ReadByte();
            _ = reader.ReadByte(); // unknown
            EntityType = reader.ReadByte();
            EntityId = reader.ReadUInt32();
            _ = reader.ReadByte(); // unknown
            _ = reader.ReadByte(); // unknown
            CharacterName = reader.ReadNullTerminatedString();
            Message = reader.ReadNullTerminatedString();
            Log.Verbose($"Received message from {CharacterName} with message: {Message}");
        }

        public string RenderText()
        {
            return $"ChatType {ChatType}, EntityType: {EntityType}, EntityId: {EntityId} Character: {CharacterName} send: {Message}";
        }
    }
}
