using D2NG.MuleManager.Configuration;
using D2NG.MuleManager.Services.Refresh;
using System.Threading;
using System.Threading.Tasks;

namespace D2NG.MuleManager.Services.MuleManager;

public interface IMuleManagerService
{
    /// <summary>
    /// Logs on to one account and records each character: what a mule holds, or what a played
    /// character wears. <paramref name="force"/> also visits mules that were full when last seen;
    /// <paramref name="onlyCharacter"/> limits the visit to one character.
    /// </summary>
    Task RefreshAccount(MuleManagerAccount account, RefreshKind kind, bool force, string onlyCharacter, AccountProgress progress, CancellationToken token);
}
