using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Bots.Types.CS
{
    public class CsConfiguration : MultiClientConfiguration
    {
        public string TeleportCharacterName { get; set; }

        public override void Validate()
        {
            base.Validate();
            if (string.IsNullOrEmpty(TeleportCharacterName))
            {
                throw new ValidationException($"{nameof(TeleportCharacterName)} is required on cs configuration");
            }
        }
    }
}
