using System.IO;
using Shouldly;
using Xunit;

namespace Karmasis.MiniVault.CustomActions.Tests
{
    public class CertificateFileStagingTests
    {
        [Fact]
        public void Stage_CopiesThePfxIntoTheProgramDataFolder_AndReturnsTheCopy()
        {
            using (var source = new TempDirectory())
            using (var programData = new TempDirectory())
            {
                var sourcePath = source.File("minivault.pfx");
                File.WriteAllBytes(sourcePath, new byte[] { 0x30, 0x82, 0x01, 0x02 });

                var staged = CertificateFileStaging.Stage(sourcePath, programData.Path);

                staged.ShouldBe(programData.File("minivault.pfx"));
                File.ReadAllBytes(staged).ShouldBe(new byte[] { 0x30, 0x82, 0x01, 0x02 });
                File.Exists(sourcePath).ShouldBeTrue("the original is left where the operator put it");
            }
        }

        [Fact]
        public void Stage_WhenTheFileIsAlreadyInTheProgramDataFolder_ReturnsItWithoutCopying()
        {
            using (var programData = new TempDirectory())
            {
                var sourcePath = programData.File("existing.pfx");
                File.WriteAllText(sourcePath, "pfx");

                var staged = CertificateFileStaging.Stage(sourcePath, programData.Path);

                staged.ShouldBe(sourcePath);
                Directory.GetFiles(programData.Path).Length.ShouldBe(1);
            }
        }

        [Fact]
        public void Stage_IgnoresCaseAndTrailingSeparatorWhenComparingFolders()
        {
            using (var programData = new TempDirectory())
            {
                var sourcePath = programData.File("existing.pfx");
                File.WriteAllText(sourcePath, "pfx");

                var staged = CertificateFileStaging.Stage(sourcePath.ToUpperInvariant(), programData.Path + "\\");

                Path.GetFullPath(staged).ToUpperInvariant().ShouldBe(sourcePath.ToUpperInvariant());
                Directory.GetFiles(programData.Path).Length.ShouldBe(1);
            }
        }

        [Fact]
        public void Stage_OverwritesAPreviousCopyWithTheSameName()
        {
            using (var source = new TempDirectory())
            using (var programData = new TempDirectory())
            {
                var sourcePath = source.File("minivault.pfx");
                File.WriteAllText(sourcePath, "new");
                File.WriteAllText(programData.File("minivault.pfx"), "old");

                var staged = CertificateFileStaging.Stage(sourcePath, programData.Path);

                File.ReadAllText(staged).ShouldBe("new");
            }
        }

        [Fact]
        public void Stage_CreatesTheProgramDataFolderWhenMissing()
        {
            using (var source = new TempDirectory())
            using (var programData = new TempDirectory(create: false))
            {
                var sourcePath = source.File("minivault.pfx");
                File.WriteAllText(sourcePath, "pfx");

                var staged = CertificateFileStaging.Stage(sourcePath, programData.Path);

                File.Exists(staged).ShouldBeTrue();
            }
        }

        [Fact]
        public void Stage_WhenTheSourceDoesNotExist_ThrowsNamingThePathAndTheProperty()
        {
            using (var programData = new TempDirectory())
            {
                var missing = programData.File("missing.pfx");

                var ex = Should.Throw<FileNotFoundException>(() => CertificateFileStaging.Stage(missing, programData.Path));

                ex.Message.ShouldContain(missing);
                ex.Message.ShouldContain("MV_CERT_PATH");
            }
        }
    }
}
