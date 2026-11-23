using D2NG.MuleManager.UI;
using D2NG.MuleManager.UI.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBaseAddress = builder.Configuration["apiBaseAddress"];
var address = string.IsNullOrWhiteSpace(apiBaseAddress) ? builder.HostEnvironment.BaseAddress : apiBaseAddress;
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(address) });
builder.Services.AddScoped<ApiClient>();
builder.Services.AddScoped<UiState>();
builder.Services.AddMudServices();

await builder.Build().RunAsync();
