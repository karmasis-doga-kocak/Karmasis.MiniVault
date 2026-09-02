namespace MiniVault.Contracts;

public sealed class SetSecretRequest
{
    public string Value { get; set; }
    public string ContentType { get; set; }
}
