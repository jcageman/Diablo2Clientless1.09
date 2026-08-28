using System.ComponentModel.DataAnnotations;

namespace D2NG.Navigation.Services.MapApi;

/// <summary>
/// Navigation settings, bound from the <c>map</c> section. The bot asks an external map API for the
/// layout of each generated area and paths through the answer, so this must point at a reachable
/// service for any movement to work.
/// </summary>
public class MapConfiguration
{
    /// <summary>
    /// Base URL of the map API, for example <c>http://localhost:8080</c>. Must be a valid absolute
    /// URL.
    /// </summary>
    [Required]
    [Url]
    public string ApiUrl { get; set; }
}
