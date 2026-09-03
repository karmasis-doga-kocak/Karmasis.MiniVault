namespace Karmasis.MiniVault.Server.Secrets;

/// <summary>A caller-supplied secret name, value or content type was rejected. The message is safe to return to the client.</summary>
public sealed class SecretValidationException(string message) : Exception(message);
