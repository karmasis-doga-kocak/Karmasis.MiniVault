namespace Karmasis.MiniVault.Server.Vault;

public class VaultException : Exception
{
    public VaultException(string message) : base(message) { }
    public VaultException(string message, Exception inner) : base(message, inner) { }
}

public sealed class VaultNotInitializedException() : VaultException("The vault is not initialized. Run 'minivault init' first.");

public sealed class VaultAlreadyInitializedException() : VaultException("The vault is already initialized. Use 'minivault recover' to change the master key.");
