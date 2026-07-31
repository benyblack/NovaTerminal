using System;
using System.Collections.Generic;
using System.Reflection;
using NovaTerminal.Platform.Ssh.Models;
using NovaTerminal.Services.Ssh;
using Xunit;

namespace NovaTerminal.Tests.Ssh;

/// <summary>
/// #121: <c>ActiveSshSessionRegistry</c> retains the session password so the remote file browser and
/// path autocomplete can open their own SFTP connections without re-prompting. It used to retain it as a
/// <see cref="string"/>, which cannot be cleared and which the GC may relocate — so plaintext sat on the
/// managed heap for the whole session with no way to wipe it.
///
/// These assert the two properties that changed. They reach into the private buffer by reflection on
/// purpose: the observable API cannot distinguish "forgotten" from "wiped", and *wiped* is the whole
/// point. A test that only checked <c>TryGetRuntimePassword</c> returns false would have passed against
/// the old code.
/// </summary>
public sealed class ActiveSshSessionSecretHygieneTests
{
    private const string Secret = "correct horse battery staple";

    private static Dictionary<Guid, byte[]> PasswordBuffers(ActiveSshSessionRegistry registry)
    {
        FieldInfo field = typeof(ActiveSshSessionRegistry).GetField(
            "_runtimePasswords",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "ActiveSshSessionRegistry._runtimePasswords is gone. If the storage was reshaped, these "
                + "tests need reshaping with it — do not delete them, they are the only check that the "
                + "retained secret is actually wiped rather than merely dropped.");

        return (Dictionary<Guid, byte[]>)field.GetValue(registry)!;
    }

    private static ActiveSshSessionRegistry RegistryWithSession(Guid sessionId)
    {
        var registry = new ActiveSshSessionRegistry();
        registry.Register(new ActiveSshSessionDescriptor(sessionId, Guid.NewGuid(), SshBackendKind.Native));
        return registry;
    }

    [Fact]
    public void Unregister_WipesTheRetainedBytes_NotJustTheReference()
    {
        Guid sessionId = Guid.NewGuid();
        ActiveSshSessionRegistry registry = RegistryWithSession(sessionId);
        registry.SetRuntimePassword(sessionId, Secret);

        // Hold the array itself, so dropping it from the dictionary cannot hide whether it was wiped.
        byte[] retained = PasswordBuffers(registry)[sessionId];
        Assert.Contains(retained, b => b != 0);

        registry.Unregister(sessionId);

        Assert.All(retained, b => Assert.Equal(0, b));
        Assert.False(registry.TryGetRuntimePassword(sessionId, out _));
    }

    [Fact]
    public void OverwritingThePassword_WipesThePreviousBytes()
    {
        // The re-auth path: a second prompt in the same session replaces the stored secret. Without an
        // explicit wipe the first one is simply abandoned, still legible, for the rest of the process.
        Guid sessionId = Guid.NewGuid();
        ActiveSshSessionRegistry registry = RegistryWithSession(sessionId);

        registry.SetRuntimePassword(sessionId, Secret);
        byte[] first = PasswordBuffers(registry)[sessionId];

        registry.SetRuntimePassword(sessionId, "a different secret entirely");

        Assert.All(first, b => Assert.Equal(0, b));
        Assert.True(registry.TryGetRuntimePassword(sessionId, out string? current));
        Assert.Equal("a different secret entirely", current);
    }

    [Fact]
    public void ClearingWithAnEmptyPassword_WipesTheRetainedBytes()
    {
        Guid sessionId = Guid.NewGuid();
        ActiveSshSessionRegistry registry = RegistryWithSession(sessionId);
        registry.SetRuntimePassword(sessionId, Secret);
        byte[] retained = PasswordBuffers(registry)[sessionId];

        registry.SetRuntimePassword(sessionId, null);

        Assert.All(retained, b => Assert.Equal(0, b));
        Assert.False(registry.TryGetRuntimePassword(sessionId, out _));
    }

    [Theory]
    // The regression risk this change introduces: storage moved from a verbatim string to UTF-8 bytes,
    // so anything that does not survive an encode/decode round trip is a new bug. A user whose password
    // contains a non-ASCII character would silently fail to authenticate.
    [InlineData("plain-ascii-123")]
    [InlineData("café-münchen")]
    [InlineData("пароль")]
    [InlineData("密碼")]
    [InlineData("emoji-\U0001F511-key")]
    [InlineData("trailing space ")]
    [InlineData(" leading space")]
    [InlineData("tab\tand\nnewline")]
    public void RetainedPasswordSurvivesTheUtf8RoundTrip(string password)
    {
        Guid sessionId = Guid.NewGuid();
        ActiveSshSessionRegistry registry = RegistryWithSession(sessionId);

        registry.SetRuntimePassword(sessionId, password);

        Assert.True(registry.TryGetRuntimePassword(sessionId, out string? roundTripped));
        Assert.Equal(password, roundTripped);
    }

    // NOT TESTED, deliberately: that the lock prevents a torn read.
    //
    // I wrote that test and then deleted it. It hammered concurrent reads against concurrent
    // Unregisters and asserted every observation was the whole secret or nothing — and it passed
    // just as happily with the lock removed, verified by mutation. The window between taking the
    // array out of the dictionary and decoding it is a few nanoseconds, so a probabilistic test
    // cannot close it, and forcing it would mean a pause seam in production code purely for the
    // test.
    //
    // A test that passes with and without the thing it claims to guard is worse than no test: it
    // reads as coverage. So the lock's justification is stated in the code and left as reasoning,
    // not dressed up as a verified property.
}
