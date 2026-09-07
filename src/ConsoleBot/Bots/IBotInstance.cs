using System.Threading.Tasks;

namespace ConsoleBot.Bots;

public interface IBotInstance
{
    string GetName();
    Task Run();

    /// <summary>
    /// Whether one pass is the whole job. Farming bots run games until stopped; setup bots finish
    /// and should not be started again by the host loop.
    /// </summary>
    bool RunsOnce => false;
}
