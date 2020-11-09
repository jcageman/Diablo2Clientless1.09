namespace ConsoleBot.Bots
{
    interface IBotFactory
    {
        IBotInstance CreateBot(string botType);
    }
}
