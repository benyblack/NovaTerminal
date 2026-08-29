namespace NovaTerminal.Shots;

/// <summary>
/// Copies the assets a run is actually allowed to ship - README/site/OG/square variants and
/// clips - from the gitignored <c>artifacts/shots/</c> working directory into the committed
/// <c>docs/assets/shots/</c> tree. Masters (the raw <c>@2x</c> captures, Tier 1/2/4) never leave
/// <c>artifacts/</c>: they exist to derive the published variants, not to be published themselves.
/// </summary>
public static class Publisher
{
    /// <summary>The clip file extensions written alongside a Tier 4 clip master's own PNG capture.</summary>
    private static readonly string[] ClipExtensions = [".webm", ".gif"];

    /// <summary>
    /// Where <paramref name="asset"/> belongs under <paramref name="repositoryRoot"/>'s published
    /// assets directory: <c>&lt;root&gt;/docs/assets/shots/&lt;name&gt;&lt;extension&gt;</c>, with
    /// <paramref name="asset"/>'s own <see cref="ShotAsset.File"/> extension carried over (so a
    /// caller can repoint <see cref="ShotAsset.File"/> at a sibling <c>.webm</c>/<c>.gif</c> and
    /// reuse this for a clip rather than the PNG master it was captured alongside).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="asset"/>'s name resolves outside the assets directory - a manifest is
    /// meant to be trustworthy, but a name is still attacker-shaped text once it round-trips
    /// through JSON, and <c>../../etc/passwd</c> must not be allowed to walk this write outside
    /// <c>docs/assets/shots/</c>.
    /// </exception>
    public static string ResolveDestination(ShotAsset asset, string repositoryRoot)
    {
        string assetsDirectory = Path.GetFullPath(Path.Combine(repositoryRoot, "docs", "assets", "shots"));
        string extension = Path.GetExtension(asset.File);
        string destination = Path.GetFullPath(Path.Combine(assetsDirectory, asset.Name + extension));

        bool staysUnderAssetsDirectory =
            string.Equals(destination, assetsDirectory, StringComparison.Ordinal) ||
            destination.StartsWith(assetsDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal);

        if (!staysUnderAssetsDirectory)
        {
            throw new InvalidOperationException(
                $"Asset name '{asset.Name}' resolves outside the published assets directory " +
                $"({assetsDirectory}): '{destination}'.");
        }

        return destination;
    }

    /// <summary>
    /// Publishes every Tier 3 variant (README/site width, plus hero-single's OG card and social
    /// square) and, for every Tier 4 clip master, whichever of its <c>.webm</c>/<c>.gif</c>
    /// siblings <see cref="Encoder"/> actually produced. Tier 1/2 masters and each clip's own
    /// Tier 4 PNG capture are deliberately skipped - see the type remarks.
    /// </summary>
    /// <returns>
    /// The paths that were copied, relative to <paramref name="repositoryRoot"/>, in the order
    /// they were written.
    /// </returns>
    public static IReadOnlyList<string> Publish(ShotRun run, string repositoryRoot)
    {
        string assetsDirectory = Path.GetFullPath(Path.Combine(repositoryRoot, "docs", "assets", "shots"));
        Directory.CreateDirectory(assetsDirectory);

        var published = new List<string>();

        foreach (ShotAsset asset in run.Assets.Where(a => a.Tier == 3))
        {
            published.Add(CopyToDestination(asset, repositoryRoot));
        }

        foreach (ShotAsset clipMaster in run.Assets.Where(a => a.Tier == 4))
        {
            string clipDirectory = Path.GetDirectoryName(clipMaster.File)!;

            foreach (string extension in ClipExtensions)
            {
                string clipFile = Path.Combine(clipDirectory, clipMaster.Name + extension);
                if (!File.Exists(clipFile))
                {
                    // Absent when ffmpeg was not on PATH for this run (see ShotContext.RecordAsync):
                    // the frames were kept, but no clip was ever encoded. Not this method's problem
                    // to raise - Program already warned about it when the run itself happened.
                    continue;
                }

                published.Add(CopyToDestination(clipMaster with { File = clipFile }, repositoryRoot));
            }
        }

        return published;
    }

    private static string CopyToDestination(ShotAsset asset, string repositoryRoot)
    {
        string destination = ResolveDestination(asset, repositoryRoot);
        File.Copy(asset.File, destination, overwrite: true);
        return Path.GetRelativePath(repositoryRoot, destination);
    }
}
