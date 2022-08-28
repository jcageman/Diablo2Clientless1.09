﻿using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Bots.Types.Cows
{
    public class CowConfiguration
    {
        public List<AccountConfig> Accounts { get; set; }

        public string PortalCharacterName { get; set; }

        public void Validate()
        {
            if (Accounts == null)
            {
                throw new ValidationException($"{nameof(Accounts)} is required on cow configuration");
            }

            Accounts.ForEach(a => a.Validate());

            if (string.IsNullOrEmpty(PortalCharacterName))
            {
                throw new ValidationException($"{nameof(PortalCharacterName)} is required on cow configuration");
            }
        }
    }
}
