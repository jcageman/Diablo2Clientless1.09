using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;

namespace ConsoleBot.Bots.Types
{
    public class AccountConfig
    {
        [Required]
        public string Username { get; set; }

        [Required]
        public string Password { get; set; }

        [Required]
        public string Character { get; set; }


        public List<int> HealthSlots = [0, 1];

        public List<int> ManaSlots = [2, 3];

        public bool ResurrectMerc { get; set; } = true;

        public virtual void Validate()
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

            if (HealthSlots.Any(h => h < 0 || h > 3))
            {
                throw new ValidationException($"{nameof(HealthSlots)} should be between 0 and 3");
            }

            if (ManaSlots.Any(h => h < 0 || h > 3))
            {
                throw new ValidationException($"{nameof(ManaSlots)} should be between 0 and 3");
            }

            if (ManaSlots.Intersect(HealthSlots).Any())
            {
                throw new ValidationException($"{nameof(HealthSlots)} + {nameof(ManaSlots)} have overlapping slots");
            }

            var requiredSlots = new HashSet<int> { 0, 1, 2, 3 };
            requiredSlots.ExceptWith(ManaSlots);
            requiredSlots.ExceptWith(HealthSlots);
            if (requiredSlots.Any())
            {
                throw new ValidationException($"{nameof(HealthSlots)} + {nameof(ManaSlots)} are missing some values {string.Join(",", requiredSlots)}");
            }
        }
    }
}
