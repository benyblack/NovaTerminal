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
    /// Every extension this pipeline ever writes into <c>docs/assets/shots/</c> - the single
    /// source of truth <see cref="Prune"/> filters against, so a stray non-generated top-level
    /// file (a README, a future format nobody taught Prune about) can never match and be deleted.
    /// Built from <see cref="ClipExtensions"/> rather than restating <c>.webm</c>/<c>.gif</c> a
    /// second time.
    /// </summary>
    private static readonly string[] ManagedExtensions = [".png", ".webp", .. ClipExtensions];

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

    /// <summary>
    /// Deletes committed files under <c>docs/assets/shots/</c> that this run did not (re)publish -
    /// the fix for a real gap discovered while auditing Task 18.5's WebP policy change:
    /// <see cref="Publish"/> only ever copies, so a variant that stops being produced (a rename,
    /// a scenario dropped from the catalogue, or - the case that motivated this - a WebP sibling
    /// that no longer qualifies under the smaller-than-its-PNG gate) leaves its old file committed
    /// forever with nothing to notice or remove it.
    /// </summary>
    /// <remarks>
    /// Three deliberate safety properties, in order of how much damage getting them wrong would
    /// cause:
    /// <list type="number">
    /// <item>
    /// <b>The subset-run guard.</b> <c>shots.ps1 &lt;single-scenario&gt; --publish</c> is a
    /// supported invocation, and it populates a run's manifest - and therefore
    /// <paramref name="published"/> - with only that one scenario's variants. Pruning against
    /// that list would delete every OTHER scenario's committed assets. <paramref
    /// name="isFullCatalogueRun"/> must be computed by the caller from whether the invocation's
    /// resolved scenario set is the whole catalogue (see <c>Program.Main</c>) - this method
    /// cannot infer that from <paramref name="published"/> alone, and refuses to run (a no-op
    /// with a console warning, not an exception - a subset publish must still succeed) rather
    /// than guess.
    /// </item>
    /// <item>
    /// <b>Non-recursive, top-level scan only.</b> <see cref="Directory.GetFiles(string)"/>'s
    /// default <see cref="SearchOption.TopDirectoryOnly"/> structurally excludes
    /// <c>docs/assets/shots/hero/</c> - no hardcoded "skip the folder named hero" exception is
    /// needed, and none could be trusted as much as the directory tree itself. That folder holds
    /// manually captured hero shots (<see cref="Program"/> never writes into it) which never
    /// appear in any manifest; a recursive scan would read them as universally "not published
    /// this run" and delete them.
    /// </item>
    /// <item>
    /// <b>Extension allowlist.</b> Only <see cref="ManagedExtensions"/> are even considered, so a
    /// stray file this pipeline never generated (a README, a future format) cannot match.
    /// </item>
    /// </list>
    /// Matches against <paramref name="published"/> - the paths <see cref="Publish"/> actually
    /// wrote this run, not the manifest - so a Tier 4 clip <see cref="Publish"/> itself skipped
    /// (ffmpeg absent, see its own remarks) is correctly treated as "not written this run" and
    /// left alone, rather than "should exist" and therefore stale.
    /// </remarks>
    /// <param name="dryRun">
    /// When true, computes and returns exactly what would be deleted without touching disk - the
    /// auditability an unreviewable delete list would otherwise lack.
    /// </param>
    /// <returns>
    /// The paths deleted (or, under <paramref name="dryRun"/>, that would be deleted), relative
    /// to <paramref name="repositoryRoot"/>.
    /// </returns>
    public static IReadOnlyList<string> Prune(
        IReadOnlyList<string> published, string repositoryRoot, bool isFullCatalogueRun, bool dryRun)
    {
        if (!isFullCatalogueRun)
        {
            Console.Error.WriteLine(
                "[shots] --prune requires a full-catalogue run (no scenario name filter on the " +
                "command line) - skipping prune, since a subset run's published list would make " +
                "every other scenario's committed assets look stale and delete them.");
            return [];
        }

        string assetsDirectory = Path.GetFullPath(Path.Combine(repositoryRoot, "docs", "assets", "shots"));
        if (!Directory.Exists(assetsDirectory))
        {
            return [];
        }

        var publishedBasenames = published
            .Select(p => Path.GetFileName(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var stale = new List<string>();

        foreach (string file in Directory.GetFiles(assetsDirectory))
        {
            if (!ManagedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!publishedBasenames.Contains(Path.GetFileName(file)))
            {
                stale.Add(file);
            }
        }

        if (!dryRun)
        {
            foreach (string file in stale)
            {
                File.Delete(file);
            }
        }

        return stale.Select(f => Path.GetRelativePath(repositoryRoot, f)).ToArray();
    }

    private static string CopyToDestination(ShotAsset asset, string repositoryRoot)
    {
        string destination = ResolveDestination(asset, repositoryRoot);
        File.Copy(asset.File, destination, overwrite: true);
        return Path.GetRelativePath(repositoryRoot, destination);
    }
}
