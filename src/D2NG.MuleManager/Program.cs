using D2NG.Core;
using D2NG.MuleManager.Configuration;
using D2NG.MuleManager.Services.MuleManager;
using D2NG.MuleManager.Services.Refresh;
using D2NG.MuleManager.Services.Stock;
using D2NG.Mule;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using System.Text.Json.Serialization;


var builder = WebApplication.CreateBuilder(args: args);

var configuration = builder.Configuration;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Verbose()
    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
    .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information)
    .CreateLogger();

builder.Logging.AddSerilog();

var dataDirectory = configuration.GetSection("mulemanager").Get<MuleManagerConfiguration>()?.DataDirectory;
if (!string.IsNullOrEmpty(dataDirectory))
{
    GameDataLocation.Use(dataDirectory);
}

builder.Services.AddControllers().AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddMuleStore(configuration.GetConnectionString("Marten"));
builder.Services.AddOptions<MuleManagerConfiguration>()
    .Bind(configuration.GetSection("mulemanager"))
    .ValidateDataAnnotations();
builder.Services.AddScoped<IMuleManagerService, MuleManagerService>();
builder.Services.AddSingleton<StockCache>();
builder.Services.AddSingleton<GearTablesProvider>();
builder.Services.AddSingleton<PresetProvider>();
builder.Services.AddSingleton<RefreshQueue>();
builder.Services.AddHostedService<RefreshWorker>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("CorsPolicy",
        policy => policy.AllowAnyOrigin()
        .AllowAnyMethod()
        .AllowAnyHeader());
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseCors("CorsPolicy");
app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthorization();

app.MapControllers();

await app.RunAsync();
