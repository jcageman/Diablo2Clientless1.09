using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.D2GS.Helpers;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.Exceptions;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Core.D2GS
{
    internal class GameServerConnection : Connection
    {
        private static int instanceCounter;
        private readonly int instanceId;
        internal static readonly short[] PacketSizes =
        {
            1, 8, 1, 12, 1, 1, 1, 6, 6, 11, 6, 6, 9, 13, 12, 16,
            16, 9, 26, 14, 18, 11, 0, 0, 15, 2, 2, 3, 5, 3, 4, 6,
            10, 12, 12, 13, 90, 90, 0, 40, 103,97, 15, 0, 8, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 11, 8,
            13, 0, 6, 0, 0, 13, 0, 11, 11, 0, 0, 0, 16, 15, 7, 1,
            15, 14, 42, 10, 3, 13, 46, 14, 7, 26, 40, 0, 5, 5, 38, 5,
            7, 2, 7, 21, 0, 7, 7, 15, 20, 12, 12, 16, 16, 10, 1, 1,
            1, 1, 1, 32, 10, 13, 6, 2, 21, 6, 13, 8, 6, 18, 5, 10,
            4, 20, 29, 0, 0, 0, 0, 0, 0, 2, 6, 6, 11, 7, 10, 33,
            13, 26, 6, 8, 0, 13, 9, 1, 7, 16, 24, 7, 0, 0, 7, 8,
            10, 7, 8, 24, 3, 8, 0, 7, 1, 7, 0, 7, 0, 0, 0, 0,
            1
        };

        internal event EventHandler<D2gsPacket> PacketReceived;

        internal event EventHandler<D2gsPacket> PacketSent;

        public GameServerConnection()
        {
            instanceId = ++instanceCounter;
        }

        internal override void Initialize()
        {
            var packet1 = _stream.ReadByte();
            var packet2 = _stream.ReadByte();
            if (0xa7 != packet1 || 0x01 != packet2)
            {
                throw new UnableToConnectException("Unexpected packet");
            }
        }

        internal override void WritePacket(byte[] packet)
        {
            _stream.Write(packet, 0, packet.Length);
            PacketSent?.Invoke(this, new D2gsPacket(packet));
        }

        internal void WritePacket(OutGoingPacket packet)
        {
            WritePacket(new byte[] { (byte)packet });
        }

        internal override byte[] ReadPacket()
        {
            var readBytes = new List<byte> { };
            var size = _stream.ReadByte();
            readBytes.Add((byte)size);
            if (size == -1) return null;

            if (size >= 0xF0)
            {
                var secondByte = _stream.ReadByte();
                readBytes.Add((byte)secondByte);
                size = (((size & 0xF) << 8) + secondByte - 2);
            }
            else
            {
                size -= 1;
            }

            if (size == 0) return null;

            var buffer = ReadBytes(size);
            if(buffer.Length == 0)
            {
                Log.Information($"Empty buffer length received");
                return null;
            }
            readBytes.AddRange(buffer);
            var fullString = readBytes.ToArray().ByteArrayToString();
            Log.Verbose($"Instance {instanceId} Full packet received: {fullString}");

            Huffman.Decompress(buffer, out var output);

            var fullPacketString = output.ToPrintString();
            Log.Verbose($"Instance {instanceId} Full decompressed packet received: {fullPacketString}");

            var index = 0;
            do
            {
                var packetType = output[index];
                var packetTypeHex = "0x" + BitConverter.ToString(new byte[] { packetType });

                var packetSize = GetPacketSize(new ArraySegment<byte>(output, index, output.Length - index));
                var packet = new ArraySegment<byte>(output, index, packetSize).ToArray();
                string printableString = packet.ToPrintString();
                PacketReceived?.Invoke(this, new D2gsPacket(packet));

                index += packetSize;
            } while (index < output.Length);

            if (index != output.Length)
            {
                throw new D2GSPacketException("Parsing the entire packet didn't match sum of packets size");
            }

            return output;
        }

        private byte[] ReadBytes(int count)
        {
            var buffer = new byte[count];
            var bytesRead = _stream.Read(buffer, 0, count);
            if(bytesRead != count)
            {
                return new byte[0];
            }
            return buffer;
        }

        int GetChatPacketSize(ArraySegment<byte> input)
        {
            var output = 0;
            if (input.Count < 12)
                throw new D2GSPacketException("Unable to determine packet size");

            const int initial_offset = 10;

            int name_offset = Array.IndexOf(input.Array, (byte)0, input.Offset + initial_offset);

            string name = System.Text.Encoding.UTF8.GetString(input.Array, input.Offset + initial_offset, name_offset - input.Offset - initial_offset);

            if (name_offset == -1)
                throw new D2GSPacketException("Unable to determine packet size");

            name_offset -= input.Offset;
            name_offset -= initial_offset;

            int message_offset = Array.IndexOf(input.Array, (byte)0, input.Offset + initial_offset + name_offset + 1);
            string message = System.Text.Encoding.UTF8.GetString(input.Array, input.Offset + initial_offset + name_offset, message_offset - input.Offset - initial_offset - name_offset);
            if (message_offset == -1)
                throw new D2GSPacketException("Unable to determine packet size"); ;

            message_offset = message_offset - initial_offset - name_offset - input.Offset - 1;

            output = initial_offset + name_offset + 1 + message_offset + 1;

            return output;
        }

        // This was taken from Redvex according to qqbot source
        int GetPacketSize(ArraySegment<byte> input)
        {
            byte identifier = input[0];

            int size = input.Count;

            switch (identifier)
            {
                case 0x26:
                    return GetChatPacketSize(input);
                case 0x5b:
                    if (size >= 3)
                    {
                        return BitConverter.ToInt16(input.ToArray(), 1);
                    }
                    break;
                case 0x94:
                    if (size >= 2)
                    {
                        return input[1] * 3 + 6;
                    }
                    break;
                case 0xaa:
                    if (size >= 7)
                    {
                        return input[6];
                    }
                    break;
                case 0xac:
                    if (size >= 13)
                    {
                        return input[12];
                    }
                    break;
                case 0xae:
                    if (size >= 3)
                    {
                        return 3 + BitConverter.ToInt16(input.ToArray(), 1);
                    }
                    break;
                case 0x9c:
                    if (size >= 3)
                    {
                        return input[2];
                    }
                    break;
                case 0x9d:
                    if (size >= 3)
                    {
                        return input[2];
                    }
                    break;
                default:
                    if (identifier < PacketSizes.Length)
                    {
                        var identifierPacketSize = PacketSizes[identifier];
                        if (identifierPacketSize == 0)
                        {
                            throw new D2GSPacketException("Unable to determine packet size");
                        }

                        return identifierPacketSize;
                    }
                    break;
            }

            throw new D2GSPacketException("Unable to determine packet size");
        }
    }
}