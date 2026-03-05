# Prompt 6 — Optimize GlyphCache Keys

Goal:
Eliminate string allocations in GlyphCache keys.

Current implementation:

string key = $"{text}|{font}|{size}|{scale}"

Replace with:

struct GlyphKey
{
    string Text;
    int FontId;
    float Size;
    float Scale;
}

Requirements:

1. Implement IEquatable<GlyphKey>
2. Implement GetHashCode
3. Replace Dictionary<string, Glyph> with Dictionary<GlyphKey, Glyph>
4. Ensure glyph cache behavior remains identical.

Add tests to verify cache hits.