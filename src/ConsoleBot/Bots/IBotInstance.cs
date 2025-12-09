using System.Threading.Tasks;

namespace ConsoleBot.Bots;

public interface IBotInstance
{
    string GetName();
    Task Run();
}
