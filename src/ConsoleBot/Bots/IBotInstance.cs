using System.Threading.Tasks;

namespace ConsoleBot.Bots
{
    public interface IBotInstance
    {
        public string GetName();
        public Task<int> Run();
    }
}
