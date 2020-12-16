using System.ComponentModel.DataAnnotations;

namespace ConsoleBot.Bots.Types
{
    public class AccountCharacter
    {
        [Required]
        public string Username { get; set; }

        [Required]
        public string Password { get; set; }

        [Required]
        public string Character { get; set; }

        public void Validate()
        {
            if (string.IsNullOrEmpty(Username))
            {
                throw new ValidationException($"{nameof(Username)} is required on account");
            }

            if (string.IsNullOrEmpty(Password))
            {
                throw new ValidationException($"{nameof(Password)} is required on account");
            }

            if (string.IsNullOrEmpty(Character))
            {
                throw new ValidationException($"{nameof(Character)} is required on account");
            }
        }
    }
}
