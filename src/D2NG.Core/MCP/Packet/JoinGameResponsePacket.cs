using D2NG.Core.MCP.Exceptions;
using Serilog;
using System.IO;
using System.Net;
using System.Text;

namespace D2NG.Core.MCP.Packet
{
    internal class JoinGameResponsePacket : McpPacket
    {
        public ushort RequestId { get; }
        public ushort GameToken { get; }
        public IPAddress D2gsIp { get; }
        public uint GameHash { get; }
        public uint Result { get; }

        public JoinGameResponsePacket(byte[] packet) : base(packet)
        {
            var reader = new BinaryReader(new MemoryStream(Raw), Encoding.ASCII);
            if (Raw.Length != reader.ReadUInt16())
            {
                throw new McpPacketException("Packet length does not match");
            }
            if (Mcp.JOINGAME != (Mcp)reader.ReadByte())
            {
                throw new McpPacketException("Expected Packet Type Not Found");
            }

            RequestId = reader.ReadUInt16();
            GameToken = reader.ReadUInt16();
            _ = reader.ReadUInt16();

            D2gsIp = new IPAddress(reader.ReadUInt32());

            GameHash = reader.ReadUInt32();
            Result = reader.ReadUInt32();
            Validate(Result);
        }

        private void Validate(uint result)
        {
            switch (result)
            {
                case 0x00:
                    break;
                case 0x29:
                    Log.Debug("Password incorrect");
                    break;
                case 0x2A:
                    Log.Debug("Game does not exist");
                    break;
                case 0x2B:
                    Log.Debug("Game is full");
                    break;
                case 0x2C:
                    Log.Debug("You do not meet the level requirements for the game");
                    break;
                case 0x6E:
                    Log.Debug("A dead hardcore chracter cannot join a game");
                    break;
                case 0x71:
                    Log.Debug("A non-hardcore character cannot join a hardcore game");
                    break;
                case 0x73:
                    Log.Debug("Unable to join a Nightmare game");
                    break;
                case 0x74:
                    Log.Debug("Unable to join a Hell Game");
                    break;
                case 0x78:
                    Log.Debug("A non-expansion character cannot join a game created by an expansion character");
                    break;
                case 0x79:
                    Log.Debug("An expansion character cannot join a game created by a non-expansion character");
                    break;
                case 0x7D:
                    Log.Debug("A non-ladder character cannot join a ladder game");
                    break;
                default:
                    Log.Debug("Unknown game join failure");
                    break;
            }
        }
    }
}