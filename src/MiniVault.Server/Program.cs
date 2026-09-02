using MiniVault.Server.Cli;
using MiniVault.Server.Hosting;
using MiniVault.Server.Vault;

if (CliApp.IsCliInvocation(args))
    return await CliApp.RunAsync(args, Console.Out);

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddMiniVaultConfiguration(args);
builder.Host.UseWindowsService();
builder.Services.AddMiniVaultCore(builder.Configuration);
builder.Services.AddHostedService<VaultStartupCheck>();

var app = builder.Build();

app.MapGet("/v1/health", () => Results.Ok(new { status = "ok" }));

app.Run();
return 0;

public partial class Program { }
