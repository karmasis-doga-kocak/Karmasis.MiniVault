using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Karmasis.MiniVault.CustomActions
{
    /// <summary>
    /// The machine-wide configuration written to %ProgramData%\MiniVault\appsettings.json.
    /// Mirrors Step 2 of deploy/windows/install.ps1.
    /// </summary>
    public sealed class MachineConfig
    {
        /// <summary>ConnectionStrings:MiniVault, the plain string. Required unless <see cref="ProtectedConnectionString"/> is set.</summary>
        public string ConnectionString { get; set; }

        /// <summary>ConnectionStrings:MiniVaultProtected - the DPAPI-protected form (<see cref="ConfigProtection.Protect"/>).
        /// When set it is what gets written, and ConnectionStrings:MiniVault is written as null so the binary's own
        /// LocalDB default cannot shadow it.</summary>
        public string ProtectedConnectionString { get; set; }

        /// <summary>MasterKey:Provider. Defaults to Dpapi for the Windows service install.</summary>
        public string MasterKeyProvider { get; set; }

        /// <summary>Tls:Url, e.g. https://0.0.0.0:8200.</summary>
        public string Url { get; set; }

        /// <summary>Tls:Certificate:Path (PFX mode). Mutually exclusive with <see cref="CertificateThumbprint"/>.</summary>
        public string CertificatePath { get; set; }

        /// <summary>Tls:Certificate:Password (PFX mode).</summary>
        public string CertificatePassword { get; set; }

        /// <summary>Tls:Certificate:Thumbprint (store mode). Mutually exclusive with <see cref="CertificatePath"/>.</summary>
        public string CertificateThumbprint { get; set; }

        /// <summary>Tls:Certificate:StoreName. Defaults to My.</summary>
        public string CertificateStoreName { get; set; }

        /// <summary>Tls:Certificate:StoreLocation. Defaults to LocalMachine.</summary>
        public string CertificateStoreLocation { get; set; }
    }

    /// <summary>
    /// Renders and writes the machine-wide appsettings.json.
    /// Hand-built JSON on purpose: the custom-actions assembly is loaded out of a temp folder by the
    /// installer, so it must not drag a Newtonsoft.Json (or any other) dependency along with it.
    /// </summary>
    public static class MachineConfigWriter
    {
        public const string DefaultMasterKeyProvider = "Dpapi";
        public const string DefaultUrl = "https://0.0.0.0:8200";
        public const string DefaultStoreName = "My";
        public const string DefaultStoreLocation = "LocalMachine";

        /// <summary>Normalizes a certmgr.msc-pasted thumbprint to 40 uppercase hex characters.</summary>
        public static string NormalizeThumbprint(string thumbprint)
        {
            if (string.IsNullOrEmpty(thumbprint))
            {
                return null;
            }

            var builder = new StringBuilder(thumbprint.Length);
            foreach (var c in thumbprint)
            {
                if (Uri.IsHexDigit(c))
                {
                    builder.Append(char.ToUpperInvariant(c));
                }
            }

            var normalized = builder.ToString();
            if (normalized.Length != 40)
            {
                throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
                    "Certificate thumbprint '{0}' is not a SHA-1 thumbprint: it must normalize to exactly 40 hexadecimal characters (got {1}).",
                    thumbprint, normalized.Length));
            }

            return normalized;
        }

        /// <summary>Renders the configuration as UTF-8 JSON text (no BOM is added here).</summary>
        public static string Render(MachineConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }

            var hasProtectedConnectionString = !string.IsNullOrEmpty(config.ProtectedConnectionString);
            if (string.IsNullOrEmpty(config.ConnectionString) && !hasProtectedConnectionString)
            {
                throw new ArgumentException("A connection string is required (MV_CONNECTIONSTRING).", "config");
            }

            var hasPath = !string.IsNullOrEmpty(config.CertificatePath);
            var hasThumbprint = !string.IsNullOrEmpty(config.CertificateThumbprint);

            if (hasPath && hasThumbprint)
            {
                throw new ArgumentException(
                    "Specify only one of MV_CERT_PATH or MV_CERT_THUMBPRINT, not both.", "config");
            }

            if (!hasPath && !hasThumbprint)
            {
                throw new ArgumentException(
                    "Specify exactly one of MV_CERT_PATH (with MV_CERT_PASSWORD) or MV_CERT_THUMBPRINT.", "config");
            }

            var provider = string.IsNullOrEmpty(config.MasterKeyProvider) ? DefaultMasterKeyProvider : config.MasterKeyProvider;
            var url = string.IsNullOrEmpty(config.Url) ? DefaultUrl : config.Url;
            var storeName = string.IsNullOrEmpty(config.CertificateStoreName) ? DefaultStoreName : config.CertificateStoreName;
            var storeLocation = string.IsNullOrEmpty(config.CertificateStoreLocation) ? DefaultStoreLocation : config.CertificateStoreLocation;
            var thumbprint = hasThumbprint ? NormalizeThumbprint(config.CertificateThumbprint) : null;

            var json = new StringBuilder();
            json.Append("{\n");
            json.Append("  \"ConnectionStrings\": {\n");
            if (hasProtectedConnectionString)
            {
                // Explicit null: a JSON null overrides the plain LocalDB default the binary's own appsettings.json
                // carries, so only the protected value is left for the server to resolve.
                json.Append("    \"MiniVault\": null,\n");
                json.Append("    \"MiniVaultProtected\": ").Append(JsonString(config.ProtectedConnectionString)).Append("\n");
            }
            else
            {
                json.Append("    \"MiniVault\": ").Append(JsonString(config.ConnectionString)).Append("\n");
            }
            json.Append("  },\n");
            json.Append("  \"MasterKey\": {\n");
            json.Append("    \"Provider\": ").Append(JsonString(provider)).Append("\n");
            json.Append("  },\n");
            json.Append("  \"Tls\": {\n");
            json.Append("    \"Url\": ").Append(JsonString(url)).Append(",\n");
            json.Append("    \"Certificate\": {\n");
            json.Append("      \"Path\": ").Append(hasPath ? JsonString(config.CertificatePath) : "null").Append(",\n");
            json.Append("      \"Password\": ").Append(hasPath ? JsonString(config.CertificatePassword) : "null").Append(",\n");
            json.Append("      \"Thumbprint\": ").Append(hasThumbprint ? JsonString(thumbprint) : "null").Append(",\n");
            json.Append("      \"StoreName\": ").Append(JsonString(storeName)).Append(",\n");
            json.Append("      \"StoreLocation\": ").Append(JsonString(storeLocation)).Append("\n");
            json.Append("    }\n");
            json.Append("  }\n");
            json.Append("}\n");

            return json.ToString();
        }

        /// <summary>Renders the configuration and writes it as UTF-8 (no BOM), creating the folder if needed.</summary>
        public static void Write(string path, MachineConfig config)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("path is required.", "path");
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, Render(config), new UTF8Encoding(false));
        }

        /// <summary>Escapes a string as a JSON string literal, including the surrounding quotes.</summary>
        internal static string JsonString(string value)
        {
            if (value == null)
            {
                return "null";
            }

            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');

            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < ' ' || c == '\u007f')
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }
                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }
    }
}
