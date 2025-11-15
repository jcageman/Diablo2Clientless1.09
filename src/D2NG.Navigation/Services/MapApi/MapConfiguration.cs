using System.ComponentModel.DataAnnotations;

namespace D2NG.Navigation.Services.MapApi;

public class MapConfiguration
{
    [Required]
    [Url]
    public string ApiUrl { get; set; }
}
