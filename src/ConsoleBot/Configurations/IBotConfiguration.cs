using System.Threading.Tasks;

namespace ConsoleBot.Configurations
{
    public interface IBotConfiguration
    {
        public Task<int> Run();
    }
}
