﻿using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Exceptions;
using Serilog;
using System;
using System.IO;
using System.Text;

namespace D2NG.Core.D2GS.Packet.Incoming
{
    internal class GameFlags : D2gsPacket
    {
        public Difficulty Difficulty { get; }
        public bool Hardcore { get; }
        public bool Expansion { get; }
        public bool Ladder { get; }

        public GameFlags(D2gsPacket packet) : base(packet.Raw)
        {
            var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
            var id = reader.ReadByte();
            if (InComingPacket.GameFlags != (InComingPacket)id)
            {
                throw new D2GSPacketException($"Invalid Packet Id {id}");
            }
            Difficulty = (Difficulty)reader.ReadByte();
            _ = reader.ReadByte();
            Hardcore = (reader.ReadByte() & 0x08) != 0;
            _ = reader.ReadUInt16();
            Expansion = reader.ReadByte() != 0;
            Ladder = reader.ReadByte() != 0;
            reader.Close();
            Log.Verbose(BitConverter.ToString(packet.Raw));
            Log.Verbose($"(0x{packet.Raw[0],2:X2}) Game flags:\n" +
                        $"\tDifficulty: {Difficulty}\n" +
                        $"\tType: {(Hardcore ? "Hardcore" : "Softcore")}" +
                        $" {(Expansion ? "Expansion" : "")}" +
                        $" {(Ladder ? "Ladder" : "")}");
        }
    }
}
