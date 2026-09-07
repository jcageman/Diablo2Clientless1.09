using System.Text;

namespace D2NG.Core.BNCS.Packet;

/// <summary>
/// SID_CREATEACCOUNT2 (0x3D), sent after the key check: the password hashed once, then the account name. Captured
/// from a 1.09 client on 6 Sept 2026; the hash bytes matched the new-password hash of a change-password packet for
/// the same password, so both share <see cref="ChangePasswordRequestPacket.PasswordHash"/>.
/// </summary>
public class CreateAccountRequestPacket : BncsPacket
{
    public CreateAccountRequestPacket(string username, string password) :
        base(BuildPacket(
            Sid.CREATEACCOUNT2,
            ChangePasswordRequestPacket.PasswordHash(password),
            Encoding.ASCII.GetBytes(username + "\0")))
    {
    }
}
