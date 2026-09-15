using NovaTerminal.Shell;
using Xunit;
using NovaTerminal.Platform;
using NovaTerminal.VT;

namespace NovaTerminal.Tests.Input
{
    public class ShellQuoterTests
    {
        [Theory]
        [InlineData(@"C:\test.txt", @"'C:\test.txt'")]
        [InlineData(@"C:\my folder\test.txt", @"'C:\my folder\test.txt'")]
        [InlineData(@"C:\O'Brien\test.txt", @"'C:\O''Brien\test.txt'")]
        [InlineData(@"C:\a b'c.txt", @"'C:\a b''c.txt'")]
        public void PwshQuoter_EscapesApostrophesCorrectly(string input, string expected)
        {
            var quoter = new PwshQuoter();
            string actual = quoter.QuotePath(input);
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(@"C:\test.txt", @"""C:\test.txt""")]
        [InlineData(@"C:\my folder\test.txt", @"""C:\my folder\test.txt""")]
        public void CmdQuoter_WrapsPathInDoubleQuotes(string input, string expected)
        {
            var quoter = new CmdQuoter();
            string actual = quoter.QuotePath(input);
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(@"C:\%APPDATA%\x.txt")]   // env expansion, even inside quotes
        [InlineData(@"C:\a!VAR!b.txt")]       // delayed expansion
        [InlineData("/mnt/c/foo\" & del x \"bar")] // WSL-mapped name with a quote breakout
        public void CmdQuoter_FlagsUnneutralizableMetacharacters(string input)
        {
            var quoter = new CmdQuoter();
            Assert.True(quoter.HasUnsafeMetacharacters(input));
        }

        [Theory]
        [InlineData(@"C:\normal path\x.txt")]
        [InlineData(@"C:\O'Brien\x.txt")]     // apostrophe is harmless to cmd
        public void CmdQuoter_AllowsSafePaths(string input)
        {
            var quoter = new CmdQuoter();
            Assert.False(quoter.HasUnsafeMetacharacters(input));
        }

        [Fact]
        public void PosixAndPwshQuoters_TreatEverythingAsSafe()
        {
            Assert.False(((IShellQuoter)new PosixShQuoter()).HasUnsafeMetacharacters(@"$(rm -rf ~)"));
            Assert.False(((IShellQuoter)new PwshQuoter()).HasUnsafeMetacharacters(@"$(rm -rf ~)"));
        }

        [Theory]
        [InlineData(@"/home/user/test.txt", @"'/home/user/test.txt'")]
        [InlineData(@"/home/user/my folder/test.txt", @"'/home/user/my folder/test.txt'")]
        [InlineData(@"/home/a'b.txt", @"'/home/a'\''b.txt'")]
        public void PosixShQuoter_EscapesApostrophesCorrectly(string input, string expected)
        {
            var quoter = new PosixShQuoter();
            string actual = quoter.QuotePath(input);
            Assert.Equal(expected, actual);
        }
    }
}
