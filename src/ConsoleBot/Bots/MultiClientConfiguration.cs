using ConsoleBot.Bots.Types;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Bots
{
    public class MultiClientConfiguration
    {
        public List<AccountCharacter> Accounts { get; set; }

        public virtual void Validate()
        {
            if (Accounts == null)
            {
                throw new ValidationException($"{nameof(Accounts)} is required on multi-client configuration");
            }

            Accounts.ForEach(a => a.Validate());
        }
    }
}
