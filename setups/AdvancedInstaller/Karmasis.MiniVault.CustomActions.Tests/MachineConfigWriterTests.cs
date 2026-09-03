using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Karmasis.MiniVault.CustomActions.Tests
{
    public class MachineConfigWriterTests
    {
        /// <summary>
        /// Parses JSON into the XML shape JsonReaderWriterFactory produces, which both proves the text
        /// is well-formed JSON and lets the tests assert on individual keys without a JSON library.
        /// </summary>
        private static XElement ParseJson(string json)
        {
            using (var reader = JsonReaderWriterFactory.CreateJsonReader(
                       Encoding.UTF8.GetBytes(json), System.Xml.XmlDictionaryReaderQuotas.Max))
            {
                return XElement.Load(reader);
            }
        }

        private static string Value(XElement root, params string[] path)
        {
            var node = root;
            foreach (var name in path)
            {
                node = node.Element(name);
                node.ShouldNotBeNull("missing key: " + string.Join(":", path));
            }

            // JsonReaderWriterFactory maps a JSON null to <name type="null"/>.
            return (string)node.Attribute("type") == "null" ? null : node.Value;
        }

        [Fact]
        public void Render_PfxMode_ProducesTheKeysTheServerReads()
        {
            var json = MachineConfigWriter.Render(new MachineConfig
            {
                ConnectionString = "Server=sql01;Database=MiniVault;Integrated Security=true",
                Url = "https://0.0.0.0:8200",
                CertificatePath = @"C:\certs\minivault.pfx",
                CertificatePassword = "s3cret"
            });

            var root = ParseJson(json);

            Value(root, "ConnectionStrings", "MiniVault")
                .ShouldBe("Server=sql01;Database=MiniVault;Integrated Security=true");
            Value(root, "MasterKey", "Provider").ShouldBe("Dpapi");
            Value(root, "Tls", "Url").ShouldBe("https://0.0.0.0:8200");
            Value(root, "Tls", "Certificate", "Path").ShouldBe(@"C:\certs\minivault.pfx");
            Value(root, "Tls", "Certificate", "Password").ShouldBe("s3cret");
            Value(root, "Tls", "Certificate", "Thumbprint").ShouldBeNull();
            Value(root, "Tls", "Certificate", "StoreName").ShouldBe("My");
            Value(root, "Tls", "Certificate", "StoreLocation").ShouldBe("LocalMachine");
        }

        [Fact]
        public void Render_ThumbprintMode_NormalizesAndLeavesPathAndPasswordNull()
        {
            var json = MachineConfigWriter.Render(new MachineConfig
            {
                ConnectionString = "Server=sql01;Database=MiniVault;Integrated Security=true",
                // As pasted out of certmgr.msc: spaces and a leading left-to-right mark.
                CertificateThumbprint = "\u200e01 23 45 67 89 ab cd ef 01 23 45 67 89 ab cd ef 01 23 45 67"
            });

            var root = ParseJson(json);

            Value(root, "Tls", "Certificate", "Thumbprint")
                .ShouldBe("0123456789ABCDEF0123456789ABCDEF01234567");
            Value(root, "Tls", "Certificate", "Path").ShouldBeNull();
            Value(root, "Tls", "Certificate", "Password").ShouldBeNull();
            // No Tls:Url given, so the documented default is written.
            Value(root, "Tls", "Url").ShouldBe("https://0.0.0.0:8200");
        }

        [Fact]
        public void Render_EscapesBackslashesAndQuotes()
        {
            var connectionString = @"Server=sql01\INST;Database=Mini""Vault"";Integrated Security=true";

            var json = MachineConfigWriter.Render(new MachineConfig
            {
                ConnectionString = connectionString,
                CertificatePath = @"C:\certs\a""b\minivault.pfx",
                CertificatePassword = "pa\\ss\"word"
            });

            json.ShouldContain(@"Server=sql01\\INST");
            json.ShouldContain(@"Mini\""Vault\""");

            var root = ParseJson(json);
            Value(root, "ConnectionStrings", "MiniVault").ShouldBe(connectionString);
            Value(root, "Tls", "Certificate", "Path").ShouldBe(@"C:\certs\a""b\minivault.pfx");
            Value(root, "Tls", "Certificate", "Password").ShouldBe("pa\\ss\"word");
        }

        [Fact]
        public void Render_EscapesControlCharacters()
        {
            var json = MachineConfigWriter.Render(new MachineConfig
            {
                ConnectionString = "Server=a\tb\nc",
                CertificateThumbprint = new string('a', 40)
            });

            json.ShouldContain(@"Server=a\tb\nc");
            ParseJson(json); // still valid JSON
        }

        [Theory]
        [InlineData(null, null, null)]
        [InlineData("", "", "")]
        public void Render_WithoutAnyCertificate_Throws(string path, string password, string thumbprint)
        {
            Should.Throw<ArgumentException>(() => MachineConfigWriter.Render(new MachineConfig
            {
                ConnectionString = "Server=sql01",
                CertificatePath = path,
                CertificatePassword = password,
                CertificateThumbprint = thumbprint
            })).Message.ShouldContain("MV_CERT_PATH");
        }

        [Fact]
        public void Render_WithBothCertificateModes_Throws()
        {
            Should.Throw<ArgumentException>(() => MachineConfigWriter.Render(new MachineConfig
            {
                ConnectionString = "Server=sql01",
                CertificatePath = @"C:\certs\minivault.pfx",
                CertificateThumbprint = new string('a', 40)
            })).Message.ShouldContain("not both");
        }

        [Fact]
        public void Render_WithoutConnectionString_Throws()
        {
            Should.Throw<ArgumentException>(() => MachineConfigWriter.Render(new MachineConfig
            {
                CertificateThumbprint = new string('a', 40)
            })).Message.ShouldContain("MV_CONNECTIONSTRING");
        }

        [Fact]
        public void Render_WithAProtectedConnectionString_WritesOnlyTheProtectedFormAndNullsThePlainKey()
        {
            var json = MachineConfigWriter.Render(new MachineConfig
            {
                ConnectionString = "Server=sql01;Database=MiniVault;User ID=u;Password=p",
                ProtectedConnectionString = "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA",
                CertificateThumbprint = "0123456789ABCDEF0123456789ABCDEF01234567"
            });

            json.ShouldNotContain("Password=p");
            var root = ParseJson(json);
            Value(root, "ConnectionStrings", "MiniVault").ShouldBeNull();
            Value(root, "ConnectionStrings", "MiniVaultProtected").ShouldBe("AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA");
        }

        [Fact]
        public void ConfigProtection_RoundTripsOnThisMachine()
        {
            var protectedValue = ConfigProtection.Protect("Server=sql01;Database=MiniVault;User ID=u;Password='p;w'");

            protectedValue.ShouldNotContain("sql01");
            ConfigProtection.Unprotect(protectedValue).ShouldBe("Server=sql01;Database=MiniVault;User ID=u;Password='p;w'");
        }

        [Fact]
        public void NormalizeThumbprint_RejectsSomethingThatIsNotASha1Thumbprint()
        {
            Should.Throw<ArgumentException>(() => MachineConfigWriter.NormalizeThumbprint("0123456789"));
        }

        [Fact]
        public void Write_CreatesTheFolderAndWritesUtf8WithoutBom()
        {
            using (var directory = new TempDirectory(create: false))
            {
                var path = directory.File("appsettings.json");

                MachineConfigWriter.Write(path, new MachineConfig
                {
                    ConnectionString = "Server=sql01;Database=MiniVault;Integrated Security=true",
                    CertificateThumbprint = new string('b', 40)
                });

                File.Exists(path).ShouldBeTrue();

                var bytes = File.ReadAllBytes(path);
                bytes[0].ShouldBe((byte)'{');

                ParseJson(File.ReadAllText(path));
            }
        }
    }
}
