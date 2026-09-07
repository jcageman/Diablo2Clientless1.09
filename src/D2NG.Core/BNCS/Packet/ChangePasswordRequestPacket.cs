using D2NG.Core.BNCS.Hashing;
using System;
using System.Linq;
using System.Text;

namespace D2NG.Core.BNCS.Packet;

/// <summary>
/// SID_CHANGEPASSWORD (0x31), sent after the key check and before logon. Captured from a 1.09 client on 6 Sept 2026:
/// client token, server token, the old password hashed with both tokens exactly as a logon does, the new password
/// hashed once, then the account name. Passwords are lowercased before hashing, as for logon.
/// </summary>
public class ChangePasswordRequestPacket : BncsPacket
{
    public ChangePasswordRequestPacket(uint clientToken, uint serverToken, string username, string oldPassword, string newPassword) :
        base(BuildPacket(
            Sid.CHANGEPASSWORD,
            BitConverter.GetBytes(clientToken),
            BitConverter.GetBytes(serverToken),
            Bsha1.DoubleHash(clientToken, serverToken, oldPassword.ToLowerInvariant()),
            PasswordHash(newPassword),
            Encoding.ASCII.GetBytes(username + "\0")))
    {
    }

    internal static byte[] PasswordHash(string password)
    {
        return Bsha1.GetHash([.. Encoding.UTF8.GetBytes(password.ToLowerInvariant())]).ToArray();
    }
}
