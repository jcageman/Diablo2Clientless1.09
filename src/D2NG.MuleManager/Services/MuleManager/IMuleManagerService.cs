using System.Threading.Tasks;

namespace D2NG.MuleManager.Services.MuleManager
{
    public interface IMuleManagerService
    {
        Task<bool> UpdateAllAccounts();
    }
}
