using Xunit;
using NovaTerminal.Core;

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
        [InlineData(@"C:\a ""b""\c.txt", @"""C:\a \""b\""\c.txt""")]
        public void CmdQuoter_EscapesDoubleQuotesCorrectly(string input, string expected)
        {
            var quoter = new CmdQuoter();
            string actual = quoter.QuotePath(input);
            Assert.Equal(expected, actual);
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
