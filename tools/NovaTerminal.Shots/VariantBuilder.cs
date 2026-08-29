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

    /// <summary>
    /// How much of hero-single's own top the OG card and social square crop down to before
    /// framing - the banner plus the first command block or two, not the whole transcript.
    /// </summary>
    /// <remarks>
    /// hero-single's full master composited at OnBackdrop's ~86% fill puts its transcript text
    /// at roughly a third of native size, which reads as texture rather than text once a link
    /// preview or a social timeline renders the card at 300-500px wide - the banner survives
    /// that, the six lines of git status and test output below it do not. Raising the fill
    /// fraction cannot fix this: 86% to 94% buys about 9% more pixels, nowhere near the ~3x this
    /// needs. Cropping to the hero region first means what actually reaches the card is already
    /// large in the source, not shrunk twice.
    /// </remarks>
    private const float CardHeroCropFraction = 0.55f;

    /// <summary>
    /// The card backdrop's top stop, #262B36 - about 3x #0E1014's luminance. Deliberately not
    /// the transparent-background README/site path's own colour scheme: that path already reads
    /// correctly (its shadow lands on whatever page hosts it), but #0E1014 is RGB(14,16,20), and
    /// even a fully opaque shadow over it can only swing ~14 units before blur and quantization
    /// eat the rest - the alpha-140 shadow PostProcess.RoundedWithShadow actually draws produces
    /// an ~8-unit edge, invisible in practice. No shadow alpha fixes that; the defect is the
    /// backdrop's own luminance. #262B36 (RGB 38,43,54) leaves enough headroom for the same
    /// alpha-140 shadow to produce a real 15-20 unit edge while staying dark and on-brand.
    /// </summary>
    private static readonly SKColor CardGradientTop = new(0x26, 0x2B, 0x36);

    /// <summary>The card backdrop's bottom stop, #332A4A - shifted up from #1B1330 by the same proportion as the top stop.</summary>
    private static readonly SKColor CardGradientBottom = new(0x33, 0x2A, 0x4A);

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

        float cornerRadius = CornerRadiusAt1x * (float)run.Scale;
        float shadowBlur = ShadowBlurAt1x * (float)run.Scale;
        int margin = (int)Math.Round(MarginAt1x * run.Scale);

        using SKBitmap source = LoadBitmap(master.File);
        using SKBitmap framed = PostProcess.RoundedWithShadow(source, cornerRadius, shadowBlur, margin);

        produced.Add(BuildResizedVariant(run, master, framed, "readme", ReadmeWidth));
        produced.Add(BuildResizedVariant(run, master, framed, "site", SiteWidth));

        if (string.Equals(master.Name, CardMasterName, StringComparison.Ordinal))
        {
            // The full master, framed - not cropped: og-card and social-square get their own,
            // smaller frame below, built from a crop of the hero region rather than the whole
            // transcript. See CardHeroCropFraction's remarks for why.
            int cropHeight = (int)Math.Round(source.Height * CardHeroCropFraction);
            using SKBitmap heroCrop = PostProcess.Crop(source, new SKRectI(0, 0, source.Width, cropHeight));
            using SKBitmap cardFramed = PostProcess.RoundedWithShadow(heroCrop, cornerRadius, shadowBlur, margin);

            using SKBitmap ogCard = PostProcess.OnBackdrop(
                cardFramed, OgCardWidth, OgCardHeight, CardGradientTop, CardGradientBottom);
            produced.Add(WriteNamedVariant(run, master, ogCard, "og-card"));

            using SKBitmap socialSquare = PostProcess.OnBackdrop(
                cardFramed, SocialSquareSize, SocialSquareSize, CardGradientTop, CardGradientBottom);
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
    /// <remarks>
    /// Never upscales. <paramref name="framed"/>'s own width is the scenario's logical width
    /// times <see cref="ShotRun.Scale"/> plus the margin this task's own framing adds, which for
    /// several real masters (agent-session-journal, settings-appearance-tab,
    /// settings-agent-access-tab) is well under 2400 - blowing those up to the nominal site
    /// width would visibly soften their thin monospace text. Capping to whichever is smaller
    /// means these three simply publish narrower than the nominal width instead of upscaled.
    /// </remarks>
    private static ShotAsset BuildResizedVariant(
        ShotRun run, ShotAsset master, SKBitmap framed, string suffix, int targetWidth)
    {
        int effectiveWidth = Math.Min(targetWidth, framed.Width);
        int targetHeight = (int)Math.Round((double)effectiveWidth * framed.Height / framed.Width);

        var resized = new SKBitmap(new SKImageInfo(effectiveWidth, targetHeight, framed.ColorType, framed.AlphaType));
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
