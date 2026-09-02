namespace MiniVault.Server.Keys;

public sealed class MasterKeyOptions
{
    public const string SectionName = "MasterKey";
    public const string DpapiProvider = "Dpapi";
    public const string EnvironmentProvider = "Environment";

    public string Provider { get; set; } = DpapiProvider;
    /// <summary>DPAPI file path. Default: %ProgramData%\MiniVault\masterkey.bin.</summary>
    public string? Path { get; set; }
}
