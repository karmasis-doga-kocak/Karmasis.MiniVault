namespace MiniVault.Server.Keys;

/// <summary>Reads the KEK from the MINIVAULT__MASTERKEY environment variable (base64 of 32 bytes). Container scenario.</summary>
public sealed class EnvironmentMasterKeyProvider : IMasterKeyProvider
{
    public const string VariableName = "MINIVAULT__MASTERKEY";

    public string Name => MasterKeyOptions.EnvironmentProvider;
    public bool CanStore => false;

    public bool Exists() => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(VariableName));

    public byte[] GetKek()
    {
        var value = Environment.GetEnvironmentVariable(VariableName);
        if (string.IsNullOrWhiteSpace(value))
            throw new MasterKeyUnavailableException($"Environment variable {VariableName} is not set.");

        byte[] kek;
        try { kek = Convert.FromBase64String(value.Trim()); }
        catch (FormatException ex) { throw new MasterKeyUnavailableException($"{VariableName} is not valid base64.", ex); }

        if (kek.Length != MasterKey.Size)
            throw new MasterKeyUnavailableException($"{VariableName} must decode to {MasterKey.Size} bytes, got {kek.Length}.");
        return kek;
    }

    public void Store(byte[] kek) =>
        throw new NotSupportedException($"The Environment provider cannot store the master key. Set {VariableName} to the value printed by 'minivault init'.");
}
