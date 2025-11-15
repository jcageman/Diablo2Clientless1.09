using Serilog;
using System;
using System.Linq;
using System.Text;

namespace D2NG.Core.BNCS.Packet;

public class AuthCheckRequestPacket : BncsPacket
{
    private static readonly byte[] KeyCount = BitConverter.GetBytes(0x02U);

    private static readonly byte[] IsSpawn = BitConverter.GetBytes(0x00);

    public AuthCheckRequestPacket(
        uint clientToken,
        uint serverToken,
        int version,
        uint checksum,
        string info,
        string keyOwner
        ) : base(BuildPacket(
                Sid.AUTH_CHECK,
                BitConverter.GetBytes(clientToken),
                BitConverter.GetBytes(version),
                BitConverter.GetBytes(checksum),
                KeyCount,
                Enumerable.Repeat(new byte[] { 0 }, 76).SelectMany(a => a),
                Encoding.ASCII.GetBytes(info),
                new byte[] { 0 },
                Encoding.ASCII.GetBytes(keyOwner),
                new byte[] { 0 }
            )
        )
    {
        Log.Verbose(BitConverter.ToString(Raw).Replace("-", ""));
        Log.Verbose($"Writing AuthCheck\n" +
            $"\tType: {Type}\n" +
            $"\tClient Token: {clientToken}\n" +
            $"\tServer Token: {serverToken}\n" +
            $"\tVersion: {version}\n" +
            $"\tChecksum: {checksum}\n" +
            $"\tInfo: {info}\n");
    }
}
