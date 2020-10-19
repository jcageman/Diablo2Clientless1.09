using D2NG.Core.BNCS.Packet;
using D2NG.Core.D2GS.Helpers;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Outgoing;
using Serilog;
using System;

namespace PacketSniffer
{
    public static class OutgoingBNCSPackets
    {
        public static void HandleOutgoingPacket(byte[] bytes)
        {
            if(bytes.Length < 2)
            {
                return;
            }

            if (!Enum.IsDefined(typeof(Sid), bytes[1]))
            {
                Log.Information($"Send unknown BNCS packet of type: 0x{(byte)bytes[0],2:X2} with data {bytes.ToPrintString()}");
                return;
            }

            var packetType = (Sid)bytes[1];
            switch (packetType)
            {
                case Sid.NULL:
                case Sid.STOPADV:
                case Sid.SERVERLIST:
                case Sid.CLIENTID:
                case Sid.STARTVERSIONING:
                case Sid.REPORTVERSION:
                case Sid.STARTADVEX:
                case Sid.GETADVLISTEX:
                case Sid.ENTERCHAT:
                case Sid.GETCHANNELLIST:
                case Sid.JOINCHANNEL:
                case Sid.CHATCOMMAND:
                case Sid.CHATEVENT:
                case Sid.LEAVECHAT:
                case Sid.LOCALEINFO:
                case Sid.FLOODDETECTED:
                case Sid.UDPPINGRESPONSE:
                case Sid.CHECKAD:
                case Sid.CLICKAD:
                case Sid.READMEMORY:
                case Sid.REGISTRY:
                case Sid.MESSAGEBOX:
                case Sid.STARTADVEX2:
                case Sid.GAMEDATAADDRESS:
                case Sid.STARTADVEX3:
                case Sid.LOGONCHALLENGEEX:
                case Sid.CLIENTID2:
                case Sid.LEAVEGAME:
                case Sid.ANNOUNCEMENT:
                case Sid.DISPLAYAD:
                case Sid.NOTIFYJOIN:
                case Sid.WRITECOOKIE:
                case Sid.READCOOKIE:
                case Sid.PING:
                case Sid.READUSERDATA:
                case Sid.WRITEUSERDATA:
                case Sid.LOGONCHALLENGE:
                case Sid.LOGONRESPONSE:
                case Sid.CREATEACCOUNT:
                case Sid.SYSTEMINFO:
                case Sid.GAMERESULT:
                case Sid.GETICONDATA:
                case Sid.GETLADDERDATA:
                case Sid.FINDLADDERUSER:
                case Sid.CDKEY:
                case Sid.CHANGEPASSWORD:
                case Sid.CHECKDATAFILE:
                case Sid.GETFILETIME:
                case Sid.QUERYREALMS:
                case Sid.PROFILE:
                case Sid.CDKEY2:
                case Sid.UNKNOWN_37:
                case Sid.UNKNOWN_39:
                case Sid.LOGONRESPONSE2:
                case Sid.CHECKDATAFILE2:
                case Sid.CREATEACCOUNT2:
                case Sid.LOGONREALMEX:
                case Sid.STARTVERSIONING2:
                case Sid.QUERYREALMS2:
                case Sid.QUERYADURL:
                case Sid.CDKEY3:
                case Sid.WARCRAFTUNKNOWN:
                case Sid.NETGAMEPORT:
                case Sid.NEWS_INFO:
                case Sid.OPTIONALWORK:
                case Sid.EXTRAWORK:
                case Sid.REQUIREDWORK:
                case Sid.TOURNAMENT:
                case Sid.AUTH_INFO:
                case Sid.AUTH_CHECK:
                case Sid.AUTH_ACCOUNTCREATE:
                case Sid.AUTH_ACCOUNTLOGON:
                case Sid.AUTH_ACCOUNTLOGONPROOF:
                case Sid.AUTH_ACCOUNTCHANGE:
                case Sid.AUTH_ACCOUNTCHANGEPROOF:
                case Sid.AUTH_ACCOUNTUPGRADE:
                case Sid.AUTH_ACCOUNTUPGRADEPROOF:
                case Sid.SETEMAIL:
                case Sid.RESETPASSWORD:
                case Sid.CHANGEEMAIL:
                case Sid.SWITCHPRODUCT:
                case Sid.REPORTCRASH:
                case Sid.WARDEN:
                case Sid.GAMEPLAYERSEARCH:
                case Sid.FRIENDSLIST:
                case Sid.FRIENDSUPDATE:
                case Sid.FRIENDSADD:
                case Sid.FRIENDSREMOVE:
                case Sid.FRIENDSPOSITION:
                case Sid.CLANFINDCANDIDATES:
                case Sid.CLANINVITEMULTIPLE:
                case Sid.CLANCREATIONINVITATION:
                case Sid.CLANDISBAND:
                case Sid.CLANMAKECHIEFTAIN:
                case Sid.CLANINFO:
                case Sid.CLANQUITNOTIFY:
                case Sid.CLANINVITATION:
                case Sid.CLANREMOVEMEMBER:
                case Sid.CLANINVITATIONRESPONSE:
                case Sid.CLANRANKCHANGE:
                case Sid.CLANSETMOTD:
                case Sid.CLANMOTD:
                case Sid.CLANMEMBERLIST:
                case Sid.CLANMEMBERREMOVED:
                case Sid.CLANMEMBERSTATUSCHANGE:
                case Sid.CLANMEMBERRANKCHANGE:
                case Sid.CLANMEMBERINFORMATION:
                    if (Log.IsEnabled(Serilog.Events.LogEventLevel.Debug))
                    {
                        Log.Debug($"Send BNCS packet of type: {packetType} with data {bytes.ToPrintString()}");
                    }
                    else
                    {
                        Log.Information($"Send BNCS packet of type: {packetType}");
                    }
                    break;
            }
        }
    }
}
