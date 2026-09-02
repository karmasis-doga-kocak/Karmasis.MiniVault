namespace MiniVault.Contracts;

public sealed class HealthResponse
{
    public string Status { get; set; }
    public bool Initialized { get; set; }
    public int ActiveDataKeyVersion { get; set; }
}
