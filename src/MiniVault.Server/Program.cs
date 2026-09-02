using MiniVault.Server.Hosting;
using MiniVault.Server.Vault;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddMiniVaultConfiguration(args);
builder.Host.UseWindowsService();
builder.Services.AddMiniVaultCore(builder.Configuration);
builder.Services.AddHostedService<VaultStartupCheck>();

var app = builder.Build();

app.MapGet("/v1/health", () => Results.Ok(new { status = "ok" }));

app.Run();

public partial class Program { }
