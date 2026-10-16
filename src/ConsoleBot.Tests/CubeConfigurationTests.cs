using System.IO;
using System.Text;
using ConsoleBot.Bots.Types.Cuber;
using D2NG.Core.D2GS.Items;
using Microsoft.Extensions.Configuration;

namespace ConsoleBot.Tests;

public class CubeConfigurationTests
{
    private const string Json = """
        {
            "bot": {
                "cube": {
                    "username": "account",
                    "password": "password",
                    "character": "cuber",
                    "pollInterval": "00:30:00",
                    "recipes": [
                        {
                            "name": "Ko",
                            "requirements": [
                                { "itemNames": [ "LumRune" ], "amount": 3 },
                                { "itemNames": [ "FlawedAmethyst" ], "amount": 1 }
                            ],
                            "result": { "itemNames": [ "KoRune" ] }
                        }
                    ]
                }
            }
        }
        """;

    private static CubeConfiguration Bind(string json)
    {
        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
        var cube = new CubeConfiguration();
        config.GetSection("bot").GetSection("cube").Bind(cube);
        return cube;
    }

    [Fact]
    public void BindsRecipesFromJson()
    {
        var cube = Bind(Json);
        cube.Validate();

        var recipe = Assert.Single(cube.Recipes);
        Assert.Equal("Ko", recipe.Name);
        Assert.Equal([ItemName.LumRune], recipe.Requirements[0].ItemNames);
        Assert.Equal(3, recipe.Requirements[0].Amount);
        Assert.Equal([ItemName.KoRune], recipe.Result.ItemNames);
        Assert.Equal(TimeSpan.FromMinutes(30), cube.PollInterval);
        Assert.Equal(30, cube.MaxItemsPerRun);
    }

    [Fact]
    public void RecipeWithoutRequirements_FailsValidation()
    {
        var cube = Bind(Json.Replace("\"amount\": 3", "\"amount\": 0"));

        Assert.Throws<System.ComponentModel.DataAnnotations.ValidationException>(cube.Validate);
    }
}
