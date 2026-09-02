using MiniVault.Server.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddMiniVaultConfiguration(args);
builder.Host.UseWindowsService();

var app = builder.Build();

app.MapGet("/v1/health", () => Results.Ok(new { status = "ok" }));

app.Run();

public partial class Program { }
