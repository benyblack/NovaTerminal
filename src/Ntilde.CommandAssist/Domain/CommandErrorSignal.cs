using System;
using System.Collections.Generic;
using System.Linq;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Domain;

/// <summary>
/// Which family of shell produced a message. Recognisers switch on this rather than on the raw
/// <c>ShellKind</c> string, because the wording of a message is a property of the family
/// (<c>bash</c> and <c>zsh</c> both say "command not found"; <c>pwsh</c> and Windows PowerShell
/// both say "is not recognized as ... a cmdlet") while the fix sometimes is not.
/// </summary>
public enum AssistShellFamily
{
    Unknown = 0,
    PowerShell,
    Cmd,
    Posix,
    Fish,
}

/// <summary>
/// The pre-chewed form of a <see cref="CommandFailureContext"/> that recognisers read.
/// </summary>
/// <remarks>
/// <para>
/// Every recogniser needs the same four things - the first token of the command, the shell family,
/// the output tail, and a case-insensitive way to ask whether the tail contains a phrase - and
/// each one computing them for itself was how the V1 service ended up with three branches that
/// disagreed about what "the command" meant. Building it once also makes the cost of the
/// table O(1) in the number of recognisers rather than O(n) in string allocations.
/// </para>
/// <para>
/// <see cref="OutputTail"/> is never null here (absent output is the empty string) but
/// <see cref="HasOutput"/> distinguishes "the command printed nothing" from "we could not read the
/// grid". That distinction is load-bearing: with no output at all the service is allowed to fall
/// back on weaker inference, and with output that simply does not match anything it is not.
/// </para>
/// </remarks>
public sealed class CommandErrorSignal
{
    private readonly string _lowerOutput;

    public CommandErrorSignal(CommandFailureContext context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        CommandText = (context.CommandText ?? string.Empty).Trim();
        Tokens = Tokenize(CommandText);
        PrimaryToken = Tokens.Count > 0 ? Tokens[0] : string.Empty;
        SecondToken = Tokens.Count > 1 ? Tokens[1] : null;
        OutputTail = context.OutputTail ?? string.Empty;
        HasOutput = !string.IsNullOrWhiteSpace(context.OutputTail);
        _lowerOutput = OutputTail.ToLowerInvariant();
        Shell = ClassifyShell(context.ShellKind);
        ExitCode = context.ExitCode;
        IsRemote = context.IsRemote;
        WorkingDirectory = context.WorkingDirectory;
    }

    public CommandFailureContext Context { get; }

    public string CommandText { get; }

    /// <summary>The command split on whitespace. Quoting is not modelled; nothing here needs it.</summary>
    public IReadOnlyList<string> Tokens { get; }

    /// <summary>The program being invoked - <c>gti</c> in <c>gti status</c>. Empty for an empty line.</summary>
    public string PrimaryToken { get; }

    /// <summary>The subcommand, when there is one - <c>status</c> in <c>git status</c>.</summary>
    public string? SecondToken { get; }

    public string OutputTail { get; }

    /// <summary>False when the pane captured nothing, which is not the same as "printed nothing".</summary>
    public bool HasOutput { get; }

    public AssistShellFamily Shell { get; }

    public int? ExitCode { get; }

    public bool IsRemote { get; }

    public string? WorkingDirectory { get; }

    /// <summary>Whether the shell writes Windows-style paths and uses <c>.\</c> to run a local file.</summary>
    public bool IsWindowsShell => Shell is AssistShellFamily.PowerShell or AssistShellFamily.Cmd;

    public bool OutputContains(string phrase)
        => phrase.Length > 0 && _lowerOutput.Contains(phrase.ToLowerInvariant(), StringComparison.Ordinal);

    public bool OutputContainsAny(params string[] phrases)
        => phrases.Any(OutputContains);

    /// <summary>
    /// The command with its first token swapped for <paramref name="replacement"/>, arguments kept.
    /// </summary>
    public string WithPrimaryToken(string replacement)
    {
        if (PrimaryToken.Length == 0 || !CommandText.StartsWith(PrimaryToken, StringComparison.Ordinal))
        {
            return replacement;
        }

        return replacement + CommandText[PrimaryToken.Length..];
    }

    /// <summary>
    /// The command with its <em>second</em> token swapped - the git-subcommand case, where the
    /// program name is right and the verb is not.
    /// </summary>
    public string WithSecondToken(string replacement)
    {
        if (SecondToken is null)
        {
            return CommandText;
        }

        int start = CommandText.IndexOf(SecondToken, PrimaryToken.Length, StringComparison.Ordinal);
        if (start < 0)
        {
            return CommandText;
        }

        return CommandText[..start] + replacement + CommandText[(start + SecondToken.Length)..];
    }

    private static AssistShellFamily ClassifyShell(string? shellKind)
    {
        if (string.IsNullOrWhiteSpace(shellKind))
        {
            return AssistShellFamily.Unknown;
        }

        string kind = shellKind.Trim().ToLowerInvariant();
        return kind switch
        {
            "pwsh" or "powershell" or "windowspowershell" => AssistShellFamily.PowerShell,
            "cmd" or "cmd.exe" => AssistShellFamily.Cmd,
            "bash" or "zsh" or "sh" or "dash" or "ksh" => AssistShellFamily.Posix,
            "fish" => AssistShellFamily.Fish,
            _ => AssistShellFamily.Unknown,
        };
    }

    private static IReadOnlyList<string> Tokenize(string commandText)
    {
        if (commandText.Length == 0)
        {
            return Array.Empty<string>();
        }

        return commandText.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
