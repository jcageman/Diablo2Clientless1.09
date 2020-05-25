using System.IO;
using YamlDotNet.Serialization;

namespace ConsoleBot
{
    public class Config
    {
        public static Config FromFile(string file)
        {
            return FromString(File.ReadAllText(file));
        }

        public static Config FromString(string file)
        {
            return new Deserializer().Deserialize<Config>(file);
        }

        [YamlMember(Alias = "realm")]
        public string Realm { get; set; }

        [YamlMember(Alias = "username")]
        public string Username { get; set; }

        [YamlMember(Alias = "password")]
        public string Password { get; set; }

        [YamlMember(Alias = "character")]
        public string Character { get; set; }

        [YamlMember(Alias = "keyOwner")]
        public string KeyOwner { get; set; }

        [YamlMember(Alias = "gamefolder")]
        public string GameFolder { get; set; }

        [YamlMember(Alias = "telegramApiKey")]
        public string TelegramApiKey { get; set; }

        [YamlMember(Alias = "telegramChatId")]
        public string TelegramChatId { get; set; }
    }
}
