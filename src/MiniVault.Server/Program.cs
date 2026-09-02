using System.Reflection;
using Microsoft.Data.SqlClient;
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

var tlsOptions = builder.Configuration.GetSection(TlsOptions.SectionName).Get<TlsOptions>() ?? new TlsOptions();
KestrelConfiguration.Apply(builder, tlsOptions);
builder.Services.Configure<TlsOptions>(builder.Configuration.GetSection(TlsOptions.SectionName));
builder.Services.AddHostedService<TlsStartupCheck>();
builder.Services.AddHostedService<VaultStartupCheck>();

// Everything above stays outside the guard on purpose: it is pure wiring, and KestrelConfiguration.Apply's
// development-certificate check must stay observable to test hosts that build this entry point's IHost themselves.
try
{
    var app = builder.Build();
    app.UseMiniVaultErrorHandling();
    app.UseMiniVaultStatusCodePages();
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapMiniVaultApi();
    await app.StartAsync();
    // Kestrel has now actually bound its sockets: confirm the belt-and-braces assumption every check in
    // KestrelConfiguration.Apply exists to guarantee, so a hosting change that slips a second or non-HTTPS
    // listener through is caught here instead of silently shipping.
    KestrelConfiguration.AssertSingleHttpsAddress(app.Services);
    await app.WaitForShutdownAsync();
    return 0;
}
// A startup failure is an operator's problem, not a developer's: report the reason on one line and exit 3, so a
// Windows service / container restart loop and `sc.exe query` show something actionable instead of a stack trace.
// OperationCanceledException and HostAbortedException are normal shutdowns, and so is the framework-internal
// StopTheHostException (matched by name) that a host resolver throws once it has the IHost it wanted.
// The entry-assembly guard keeps this from firing when something else hosts this entry point in its own process
// (WebApplicationFactory, dotnet-ef): there the exception must keep propagating, or a "refuses to start" test
// would see a cleanly exited application instead of the failure it is asserting.
catch (Exception ex) when (ex is not (OperationCanceledException or HostAbortedException)
                           && ex.GetType().Name != "StopTheHostException"
                           && Assembly.GetEntryAssembly() == typeof(Program).Assembly)
{
    var reason = ex.GetBaseException();
    using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
    var logger = loggerFactory.CreateLogger("MiniVault");
    if (reason is SqlException)
        logger.LogCritical("MiniVault cannot start: Database is not reachable. Check ConnectionStrings:MiniVault. {Reason}", reason.Message);
    else
        logger.LogCritical("MiniVault cannot start: {Reason}", reason.Message);
    return 3;
}

public partial class Program { }
