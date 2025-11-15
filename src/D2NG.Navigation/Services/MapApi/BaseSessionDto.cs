using System.Text.Json.Serialization;

namespace D2NG.Navigation.Services.MapApi;

public class BaseSessionDto
{
    [JsonPropertyName("mapid")]
    public int MapId { get; set; }

    [JsonPropertyName("difficulty")]
    public int Difficulty { get; set; }
}
