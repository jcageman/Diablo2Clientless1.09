using D2NG.MuleManager.Configuration;
using D2NG.MuleManager.Services.MuleManager;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;


var builder = WebApplication.CreateBuilder(args: args);

var configuration = builder.Configuration;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Verbose()
    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
    .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information)
    .CreateLogger();

builder.Logging.AddSerilog();

builder.Services.AddControllers();
builder.Services.AddMarten(configuration.GetConnectionString("Marten"));
builder.Services.AddOptions<MuleManagerConfiguration>()
    .Bind(configuration.GetSection("mulemanager"))
    .ValidateDataAnnotations();
builder.Services.AddScoped<IMuleManagerRepository, MuleManagerRepository>();
builder.Services.AddScoped<IMuleManagerService, MuleManagerService>();
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
