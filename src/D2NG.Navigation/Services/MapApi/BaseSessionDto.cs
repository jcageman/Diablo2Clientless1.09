using Newtonsoft.Json;

namespace D2NG.Navigation.Services.MapApi
{
    public class BaseSessionDto
    {
        [JsonProperty("mapid")]
        public int MapId { get; set; }

        [JsonProperty("difficulty")]
        public int Difficulty { get; set; }
    }
}
