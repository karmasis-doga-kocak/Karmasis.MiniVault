using System;
using System.Security.Cryptography;
using System.Text;

namespace Karmasis.MiniVault.CustomActions
{
    /// <summary>
    /// DPAPI (LocalMachine) protection of configuration values, byte-for-byte what the server's
    /// MiniVault.Server.Hosting.ProtectedConfiguration and deploy/windows/install.ps1 do: the same
    /// application entropy, UTF-8 in, base64 out. The value is bound to the machine that produced it.
    /// </summary>
    public static class ConfigProtection
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Karmasis.MiniVault.Config.v1");

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain))
            {
                throw new ArgumentException("plain is required.", "plain");
            }

            var bytes = Encoding.UTF8.GetBytes(plain);
            try
            {
                return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine));
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
            }
        }

        public static string Unprotect(string protectedBase64)
        {
            if (string.IsNullOrEmpty(protectedBase64))
            {
                throw new ArgumentException("protectedBase64 is required.", "protectedBase64");
            }

            var plain = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64.Trim()), Entropy, DataProtectionScope.LocalMachine);
            try
            {
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
            }
        }
    }
}
