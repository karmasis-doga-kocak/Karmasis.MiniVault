using MiniVault.Server.Api;
using MiniVault.Server.Auth;
using MiniVault.Server.Cli;
using MiniVault.Server.Hosting;
using MiniVault.Server.Vault;

if (CliApp.IsCliInvocation(args))
    return await CliApp.RunAsync(args, Console.Out);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // A Windows service starts with an arbitrary working directory (e.g. C:\Windows\System32); anchor the content
    // root to the binary's own folder so relative configuration files resolve. Interactive runs keep the default
    // (current directory), matching the documented WindowsServiceHelpers.IsWindowsService() pattern.
    ContentRootPath = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});
builder.Configuration.AddMiniVaultConfiguration(args);
builder.Host.UseWindowsService();
builder.Services.AddMiniVaultCore(builder.Configuration);
builder.Services.AddMiniVaultAuth(builder.Configuration);
builder.Services.AddHostedService<VaultStartupCheck>();

var app = builder.Build();
app.UseMiniVaultErrorHandling();
app.UseMiniVaultStatusCodePages();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapMiniVaultApi();
app.Run();
return 0;

public partial class Program { }
