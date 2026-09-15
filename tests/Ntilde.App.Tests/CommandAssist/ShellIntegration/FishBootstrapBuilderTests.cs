using NovaTerminal.CommandAssist.ShellIntegration.Fish;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration;

public sealed class FishBootstrapBuilderTests : IDisposable
{
    private readonly string _tempRoot;

    public FishBootstrapBuilderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nova_command_assist_fish_bootstrap_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void BuildScript_ContainsStructuredLifecycleMarkers()
    {
        string script = FishBootstrapBuilder.BuildScript();

        Assert.Contains("]7;", script);
        Assert.Contains("]133;A", script);
        Assert.Contains("]133;B", script);
        Assert.Contains("]133;C;", script);
        Assert.Contains("]133;D;", script);
    }

    [Fact]
    public void BuildScript_EmitsCommandStartMarkAfterTheUserPromptRenders()
    {
        string script = FishBootstrapBuilder.BuildScript();

        // The fish_prompt EVENT fires before the prompt function runs, so it
        // can only carry A. fish has no post-prompt event, so B is emitted by
        // re-defining fish_prompt as "copy of the user's prompt, then B" --
        // the copy keeps the user's prompt output byte-for-byte.
        Assert.Contains("functions --copy fish_prompt __nova_user_fish_prompt", script);

        int redefIndex = script.IndexOf("function fish_prompt\n", StringComparison.Ordinal);
        Assert.True(redefIndex > 0, "fish_prompt must be re-defined around the copied original");

        int originalCallIndex = script.IndexOf("    __nova_user_fish_prompt", redefIndex, StringComparison.Ordinal);
        int markIndex = script.IndexOf("133;B", redefIndex, StringComparison.Ordinal);
        Assert.True(originalCallIndex > redefIndex, "the copied user prompt must run first");
        Assert.True(markIndex > originalCallIndex, "B must be emitted after the user's prompt output");
    }

    [Fact]
    public void BuildScript_SkipsPromptWrappingWhenFishPromptIsUnavailable()
    {
        string script = FishBootstrapBuilder.BuildScript();

        // Degradation guard, mirroring the PSReadLine probe in the PowerShell
        // bootstrap: if fish_prompt cannot be resolved we emit A only rather
        // than replacing the user's prompt with a synthesized one.
        Assert.Contains("if functions -q fish_prompt", script);
    }

    [Fact]
    public void BuildScript_GuardsThePromptWrapAgainstBeingReSourced()
    {
        string script = FishBootstrapBuilder.BuildScript();

        // This file IS $__fish_config_dir/config.fish for the session, so it is
        // re-entered by anything that re-sources fish's config (fish's own
        // `source $__fish_config_dir/config.fish`, `exec fish`, a user alias).
        // Without the second half of this condition the re-run would copy the
        // CURRENT fish_prompt -- by then our own wrapper -- into
        // __nova_user_fish_prompt, and the redefinition would call itself
        // forever.
        Assert.Contains(
            "if functions -q fish_prompt; and not functions -q __nova_user_fish_prompt",
            script);
    }

    [Fact]
    public void BuildScript_DoubleSourcing_CannotWrapTheWrapper()
    {
        // The structural argument, executed: fish is not runnable on Windows CI,
        // so model the two statements the guarded block performs against a tiny
        // function table and run the whole bootstrap body twice, exactly as a
        // re-source would.
        var functions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fish_prompt"] = "user prompt body",
        };

        // Mirrors the emitted script: guard, copy, redefine.
        void RunBootstrapPromptSection()
        {
            if (!functions.ContainsKey("fish_prompt")) return;
            if (functions.ContainsKey("__nova_user_fish_prompt")) return; // the fix
            functions["__nova_user_fish_prompt"] = functions["fish_prompt"];
            functions["fish_prompt"] = "call __nova_user_fish_prompt; print 133;B";
        }

        RunBootstrapPromptSection();
        RunBootstrapPromptSection();
        RunBootstrapPromptSection();

        // The copied "original" is still the user's function, never the wrapper --
        // which is precisely what makes the wrapper's self-call terminate.
        Assert.Equal("user prompt body", functions["__nova_user_fish_prompt"]);
        Assert.DoesNotContain("133;B", functions["__nova_user_fish_prompt"]);

        // And the guard is really the thing doing the work: drop it and the same
        // three passes produce a self-referential copy.
        var unguarded = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fish_prompt"] = "user prompt body",
        };
        for (int i = 0; i < 2; i++)
        {
            unguarded["__nova_user_fish_prompt"] = unguarded["fish_prompt"];
            unguarded["fish_prompt"] = "call __nova_user_fish_prompt; print 133;B";
        }
        Assert.Contains("__nova_user_fish_prompt", unguarded["__nova_user_fish_prompt"]);
    }

    [Fact]
    public void BuildScript_DoesNotAccumulatePromptMarks_AcrossPromptCycles()
    {
        string script = FishBootstrapBuilder.BuildScript();

        // Builder-level stand-in for bash's real-shell accumulation test (fish is
        // not installed on Windows CI). fish cannot grow a marker per cycle the way
        // a PS1/PROMPT string can, because the mark is a `printf` *statement* in a
        // function body rather than text appended to a variable: the wrap happens
        // exactly once at bootstrap, guarded against re-entry, and each prompt cycle
        // simply calls it. Pin both halves of that.
        Assert.Contains(
            "if functions -q fish_prompt; and not functions -q __nova_user_fish_prompt",
            script);

        // Exactly one B emission site, and it is inside the redefined fish_prompt.
        Assert.Equal(1, CountOccurrences(script, "133;B"));
        int redefIndex = script.IndexOf("function fish_prompt\n", StringComparison.Ordinal);
        Assert.True(script.IndexOf("133;B", StringComparison.Ordinal) > redefIndex);

        // The mark is never appended to a variable the way bash/zsh do it, so there
        // is no accumulating string to guard.
        Assert.DoesNotContain("fish_prompt=", script);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    [Fact]
    public void BuildScript_UsesNativeFishEventHandlers()
    {
        string script = FishBootstrapBuilder.BuildScript();

        // fish's native event hooks are fish_preexec and fish_prompt
        // (the latter fires once per prompt cycle, after the previous
        // command finishes). Using these keeps integration prompt-owner
        // friendly: we don't override fish_prompt itself, we just hook it.
        Assert.Contains("fish_preexec", script);
        Assert.Contains("fish_prompt", script);
        Assert.Contains("function", script);
    }

    [Fact]
    public void BuildScript_Base64EncodesAcceptedCommandPayload()
    {
        string script = FishBootstrapBuilder.BuildScript();

        Assert.Contains("base64", script);
    }

    [Fact]
    public void BuildScript_DetectsBsdDateAndFallsBackToSecondPrecision()
    {
        string script = FishBootstrapBuilder.BuildScript();

        // Regression guard for the macOS/BSD `date +%s%N` portability bug.
        // The bootstrap detects whether `+%N` produced digits and falls
        // back to plain `date +%s` * 1000 when it didn't.
        Assert.Contains("string match", script);
        Assert.Contains("date +%s", script);
    }

    /// <summary>
    /// fish's <c>math</c> defaults to scale 6, so the nanosecond division printed
    /// <c>1780000000123.456787</c> and every duration reached the wire as a fractional value.
    /// <c>AnsiParser</c> reads that field with <c>long.TryParse</c>, so a fraction is not a rounded
    /// duration - it is no duration at all, and fish sessions recorded none.
    /// </summary>
    [Fact]
    public void BuildScript_ForcesIntegerMathSoDurationsParse()
    {
        string script = FishBootstrapBuilder.BuildScript();

        Assert.Contains("math -s0 \"$raw / 1000000\"", script);
        Assert.Contains("math -s0 (date +%s) \"* 1000\"", script);
        Assert.Contains("math -s0 $now_ms - $__nova_command_start_ms", script);

        // No unqualified `math` call anywhere: every one of them feeds an integer field.
        Assert.DoesNotContain("(math $", script);
        Assert.DoesNotContain("math \"$raw", script);
    }

    [Fact]
    public void WriteScript_WritesConfigFishInsideFishConfigSubdirectory()
    {
        // Fish reads config from $XDG_CONFIG_HOME/fish/config.fish. The
        // bootstrap must live inside a per-shell <root>/fish/config.fish
        // layout so XDG_CONFIG_HOME can simply point at <root>.
        string path = FishBootstrapBuilder.WriteScript(_tempRoot);

        Assert.True(File.Exists(path));
        Assert.StartsWith(_tempRoot, path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("config.fish", path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fish", Path.GetDirectoryName(path)!, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
