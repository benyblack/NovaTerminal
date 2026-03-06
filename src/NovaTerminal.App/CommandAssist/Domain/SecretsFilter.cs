using System;
using System.Text.RegularExpressions;

namespace NovaTerminal.CommandAssist.Domain;

public sealed partial class SecretsFilter : ISecretsFilter
{
    private static readonly Regex PasswordFlagRegex = PasswordFlag();
    private static readonly Regex TokenQueryRegex = TokenQuery();
    private static readonly Regex SshPassRegex = SshPass();
    private static readonly Regex BearerRegex = BearerToken();
    private static readonly Regex JwtAssignmentRegex = JwtAssignment();
    private static readonly Regex ConnectionStringPasswordRegex = ConnectionStringPassword();

    public RedactionResult Redact(string commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return new RedactionResult(commandText, false);
        }

        string redacted = commandText;
        redacted = PasswordFlagRegex.Replace(redacted, "$1[REDACTED]");
        redacted = TokenQueryRegex.Replace(redacted, "$1[REDACTED]");
        redacted = SshPassRegex.Replace(redacted, "$1[REDACTED]");
        redacted = BearerRegex.Replace(redacted, "$1[REDACTED]");
        redacted = JwtAssignmentRegex.Replace(redacted, "$1[REDACTED]");
        redacted = ConnectionStringPasswordRegex.Replace(redacted, "$1[REDACTED]$3");

        return new RedactionResult(redacted, !string.Equals(commandText, redacted, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"(--password\s+)(?:""[^""]*""|'[^']*'|\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PasswordFlag();

    [GeneratedRegex(@"([?&]token=)([^&\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenQuery();

    [GeneratedRegex(@"(sshpass\s+-p\s+)(?:""[^""]*""|'[^']*'|\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SshPass();

    [GeneratedRegex(@"(Authorization:\s+Bearer\s+)([A-Za-z0-9\-_\.=]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"(\bJWT=)([A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JwtAssignment();

    [GeneratedRegex(@"(\bPassword=)([^;]+)(;?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringPassword();
}
