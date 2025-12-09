namespace ConsoleBot.Bots;

internal interface IBotFactory
{
    IBotInstance CreateBot(string botType);
}
