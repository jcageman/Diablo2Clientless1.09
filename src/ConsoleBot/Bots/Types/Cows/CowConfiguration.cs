using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Bots.Types.Cows;

public class CowConfiguration : MultiClientConfiguration
{
    public string PortalCharacterName { get; set; }

    public override void Validate()
    {
        if (string.IsNullOrEmpty(PortalCharacterName))
        {
            throw new ValidationException($"{nameof(PortalCharacterName)} is required on cow configuration");
        }
    }
}
