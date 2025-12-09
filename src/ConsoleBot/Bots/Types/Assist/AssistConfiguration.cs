using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Bots.Types.Assist;

public class AssistConfiguration
{
    public List<AccountConfig> Accounts { get; set; }

    public string HostCharacterName { get; set; }

    public string LeadCharacterName { get; set; }

    public void Validate()
    {
        if (Accounts == null)
        {
            throw new ValidationException($"{nameof(Accounts)} is required on cow configuration");
        }

        Accounts.ForEach(a => a.Validate());

        if (string.IsNullOrEmpty(LeadCharacterName))
        {
            throw new ValidationException($"{nameof(LeadCharacterName)} is required on assist configuration");
        }
    }
}
