using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using NovaTerminal.Core;
using NovaTerminal;
using SkiaSharp;
using Xunit;

namespace NovaTerminal.Tests.RenderTests;

public sealed class GlyphFallbackTests
{
    [Fact]
    public void ResolveTypefaceForCodePoint_DingbatArrow_PrefersSymbolFallbackOverEmojiShortcut()
    {
        using var primary = SKTypeface.FromFamilyName("Consolas");
        using var emoji = SKTypeface.FromFamilyName("Segoe UI Emoji");
        using var symbol = SKTypeface.FromFamilyName("Segoe UI Symbol");

        if (primary == null || emoji == null || symbol == null)
        {
            return;
        }

        if (primary.ContainsGlyph(0x279C) || !symbol.ContainsGlyph(0x279C))
        {
            return;
        }

        object operation = System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(TerminalDrawOperation));

        SetField(operation, "_fallbackCache", new ConcurrentDictionary<string, SKTypeface?>());
        SetField(operation, "_fallbackChain", new[] { emoji, symbol });

        MethodInfo resolve = typeof(TerminalDrawOperation).GetMethod(
            "ResolveTypefaceForCodePoint",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        var resolved = (SKTypeface)resolve.Invoke(operation, new object[] { 0x279C, primary })!;

        Assert.Equal(symbol.FamilyName, resolved.FamilyName);
    }

    private static void SetField(object instance, string fieldName, object value)
    {
        FieldInfo field = typeof(TerminalDrawOperation).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(instance, value);
    }
}
