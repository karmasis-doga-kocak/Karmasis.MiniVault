using System;
using Shouldly;
using Xunit;

namespace Karmasis.MiniVault.CustomActions.Tests
{
    public class ProcessRunnerQuotingTests
    {
        [Theory]
        [InlineData(new[] { "init" }, "init")]
        [InlineData(new[] { "init", "--recovery", "single" }, "init --recovery single")]
        [InlineData(new[] { "--out", @"C:\Program Files\a.txt" }, "--out \"C:\\Program Files\\a.txt\"")]
        [InlineData(new[] { "--master-key", "pass word" }, "--master-key \"pass word\"")]
        [InlineData(new[] { "" }, "\"\"")]
        public void Quote_QuotesOnlyWhatNeedsIt(string[] arguments, string expected)
        {
            ProcessRunner.Quote(arguments).ShouldBe(expected);
        }

        [Fact]
        public void Quote_EscapesEmbeddedQuotes()
        {
            ProcessRunner.Quote(new[] { "--master-key", "pa\"ss" })
                .ShouldBe("--master-key \"pa\\\"ss\"");
        }

        [Fact]
        public void Quote_DoublesTheBackslashesThatPrecedeTheClosingQuote()
        {
            // C:\dir\ must not escape the closing quote, so the trailing backslash is doubled.
            ProcessRunner.Quote(new[] { @"C:\a b\" })
                .ShouldBe("\"C:\\a b\\\\\"");
        }

        [Fact]
        public void Quote_EmptyOrNullArgumentVector_IsAnEmptyCommandLine()
        {
            ProcessRunner.Quote(null).ShouldBe(string.Empty);
            ProcessRunner.Quote(new string[0]).ShouldBe(string.Empty);
        }
    }

    public class MiniVaultCliArgumentTests
    {
        private const string OutFile = @"C:\ProgramData\MiniVault\recovery-20260902120000.txt";

        [Fact]
        public void BuildInitArguments_Single_OmitsSharesAndThreshold()
        {
            MiniVaultCli.BuildInitArguments("single", 0, 0, null, OutFile)
                .ShouldBe(new[] { "init", "--recovery", "single", "--out", OutFile });
        }

        [Fact]
        public void BuildInitArguments_DefaultsToSingleWhenTheModeIsBlank()
        {
            MiniVaultCli.BuildInitArguments(null, 0, 0, null, OutFile)
                .ShouldBe(new[] { "init", "--recovery", "single", "--out", OutFile });
        }

        [Fact]
        public void BuildInitArguments_Shamir_AddsSharesAndThreshold()
        {
            MiniVaultCli.BuildInitArguments("Shamir", 3, 2, null, OutFile)
                .ShouldBe(new[] { "init", "--recovery", "shamir", "--out", OutFile, "--shares", "3", "--threshold", "2" });
        }

        [Fact]
        public void BuildInitArguments_WithMasterKeyPassword_AddsMasterKeyLast()
        {
            MiniVaultCli.BuildInitArguments("single", 0, 0, "pa ss", OutFile)
                .ShouldBe(new[] { "init", "--recovery", "single", "--out", OutFile, "--master-key", "pa ss" });
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(3, 4)]
        [InlineData(256, 2)]
        public void BuildInitArguments_ShamirWithBadSharesOrThreshold_Throws(int shares, int threshold)
        {
            Should.Throw<ArgumentException>(
                () => MiniVaultCli.BuildInitArguments("shamir", shares, threshold, null, OutFile));
        }

        [Fact]
        public void BuildInitArguments_UnknownRecoveryMode_Throws()
        {
            Should.Throw<ArgumentException>(
                () => MiniVaultCli.BuildInitArguments("paper", 0, 0, null, OutFile));
        }

        [Fact]
        public void BuildInitArguments_WithoutOutFile_Throws()
        {
            Should.Throw<ArgumentException>(
                () => MiniVaultCli.BuildInitArguments("single", 0, 0, null, null));
        }
    }
}
