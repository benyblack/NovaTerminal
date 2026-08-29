using SkiaSharp;

namespace NovaTerminal.Shots;

/// <summary>
/// Derives the published variants — README-width, site-width, and (for hero-single only) an OG
/// card and a social square — from one already-captured master. Distinct from
/// <see cref="PostProcess"/>, which supplies the pixel operations this composes: this decides
/// which variants exist for a given master, what they are named, and how they are recorded in
/// the manifest.
/// </summary>
public static class VariantBuilder
{
    private const int ReadmeWidth = 1280;
    private const int SiteWidth = 2400;

    private const int OgCardWidth = 1200;
    private const int OgCardHeight = 630;

    private const int SocialSquareSize = 1080;

    /// <summary>The scenario whose master gets the social/OG card treatment, per the brief.</summary>
    private const string CardMasterName = "hero-single";

    /// <summary>The brand gradient's top stop, #0E1014.</summary>
    private static readonly SKColor BrandGradientTop = new(0x0E, 0x10, 0x14);

    /// <summary>The brand gradient's bottom stop, #1B1330.</summary>
    private static readonly SKColor BrandGradientBottom = new(0x1B, 0x13, 0x30);

    // Corner radius, shadow blur and margin at 1x, scaled by ShotRun.Scale below so the window
    // chrome this adds stays proportional across masters captured at different physical scales
    // rather than looking thinner (or, at margin, tighter) on a higher-scale run.
    private const float CornerRadiusAt1x = 12f;
    private const float ShadowBlurAt1x = 24f;
    private const float MarginAt1x = 48f;

    /// <summary>
    /// Builds every variant <paramref name="master"/> is entitled to, writes each to
    /// <paramref name="run"/>'s output directory, records it as a Tier 3 <see cref="ShotAsset"/>
    /// on <paramref name="run"/>, and returns exactly what was recorded.
    /// </summary>
    /// <remarks>
    /// Every variant starts from the same rounded-and-shadowed frame, built once per master:
    /// README/site are that frame scaled to a fixed width with its own aspect ratio intact, so
    /// they read as a floating window rather than a flat rectangle wherever they are embedded;
    /// the OG card and social square go a step further and composite that same frame onto a
    /// fixed-size brand-gradient backdrop, since a card's dimensions are fixed by the platform
    /// it targets rather than following the source's own shape.
    /// </remarks>
    public static IReadOnlyList<ShotAsset> BuildAll(ShotAsset master, ShotRun run)
    {
        var produced = new List<ShotAsset>();

        using SKBitmap source = LoadBitmap(master.File);
        using SKBitmap framed = PostProcess.RoundedWithShadow(
            source,
            cornerRadius: CornerRadiusAt1x * (float)run.Scale,
            shadowBlur: ShadowBlurAt1x * (float)run.Scale,
            margin: (int)Math.Round(MarginAt1x * run.Scale));

        produced.Add(BuildResizedVariant(run, master, framed, "readme", ReadmeWidth));
        produced.Add(BuildResizedVariant(run, master, framed, "site", SiteWidth));

        if (string.Equals(master.Name, CardMasterName, StringComparison.Ordinal))
        {
            using SKBitmap ogCard = PostProcess.OnBackdrop(
                framed, OgCardWidth, OgCardHeight, BrandGradientTop, BrandGradientBottom);
            produced.Add(WriteNamedVariant(run, master, ogCard, "og-card"));

            using SKBitmap socialSquare = PostProcess.OnBackdrop(
                framed, SocialSquareSize, SocialSquareSize, BrandGradientTop, BrandGradientBottom);
            produced.Add(WriteNamedVariant(run, master, socialSquare, "social-square"));
        }

        foreach (ShotAsset asset in produced)
        {
            run.Record(asset);
        }

        return produced;
    }

    /// <summary>
    /// Scales <paramref name="framed"/> to <paramref name="targetWidth"/> wide, keeping its own
    /// aspect ratio (unlike <see cref="PostProcess.OnBackdrop"/>'s fixed-canvas variants), and
    /// writes it as <c>&lt;master.Name&gt;-&lt;suffix&gt;.png</c>.
    /// </summary>
    private static ShotAsset BuildResizedVariant(
        ShotRun run, ShotAsset master, SKBitmap framed, string suffix, int targetWidth)
    {
        int targetHeight = (int)Math.Round((double)targetWidth * framed.Height / framed.Width);

        var resized = new SKBitmap(new SKImageInfo(targetWidth, targetHeight, framed.ColorType, framed.AlphaType));
        using (resized)
        {
            if (!framed.ScalePixels(resized, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)))
            {
                throw new InvalidOperationException(
                    $"SKBitmap.ScalePixels failed to resize '{master.Name}' to {targetWidth} wide.");
            }

            return WriteVariant(run, master, resized, suffix);
        }
    }

    /// <summary>Writes <paramref name="bitmap"/> as <c>&lt;master.Name&gt;-&lt;suffix&gt;.png</c> and records it.</summary>
    private static ShotAsset WriteVariant(ShotRun run, ShotAsset master, SKBitmap bitmap, string suffix)
    {
        string name = $"{master.Name}-{suffix}";
        return WriteNamedVariant(run, master, bitmap, name);
    }

    /// <summary>
    /// Writes <paramref name="bitmap"/> under <paramref name="name"/> verbatim - used for
    /// og-card and social-square, whose filenames are fixed by the brief rather than derived
    /// from the master's own name.
    /// </summary>
    private static ShotAsset WriteNamedVariant(ShotRun run, ShotAsset master, SKBitmap bitmap, string name)
    {
        string path = Path.Combine(run.OutputDirectory, $"{name}.png");
        Rasterizer.WritePng(bitmap, path);

        return new ShotAsset(
            Name: name,
            Tier: 3,
            File: path,
            Width: bitmap.Width,
            Height: bitmap.Height,
            Scenario: master.Scenario,
            Commit: run.Commit,
            Os: run.Os,
            TimestampUtc: DateTime.UtcNow.ToString("O"));
    }

    private static SKBitmap LoadBitmap(string path)
    {
        using FileStream file = File.OpenRead(path);
        return SKBitmap.Decode(file)
            ?? throw new InvalidOperationException($"Could not decode master bitmap '{path}'.");
    }
}
