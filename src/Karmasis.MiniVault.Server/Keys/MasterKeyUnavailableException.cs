namespace Karmasis.MiniVault.Server.Keys;

public sealed class MasterKeyUnavailableException : Exception
{
    public MasterKeyUnavailableException(string message) : base(message) { }
    public MasterKeyUnavailableException(string message, Exception inner) : base(message, inner) { }
}
