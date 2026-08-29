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
    /// Five deliberate safety properties, in order of how much damage getting them wrong would
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
    /// than guess. This is a check about <i>requested scope</i> and is orthogonal to the next
    /// item, which is about <i>achieved output</i>: a run can request the whole catalogue and
    /// still under-produce it.
    /// </item>
    /// <item>
    /// <b>The production-completeness guard.</b> <paramref name="isFullCatalogueRun"/> reflects
    /// what this invocation was <i>asked</i> to run, not what it actually <i>produced</i> - and
    /// there is more than one sanctioned or unsanctioned way for those to diverge without
    /// touching <paramref name="isFullCatalogueRun"/> itself. Two are known and each has its own
    /// signal and its own warning, deliberately not merged into one boolean or one message,
    /// because each has a different remedy:
    /// <list type="bullet">
    /// <item>
    /// A scenario throws. <c>Program.Main</c> catches it, counts it as a failure, and continues
    /// the loop - the request still covers the whole catalogue, but the failed scenario never
    /// called <c>run.Record</c>, so its assets are silently absent from <paramref
    /// name="published"/>. Signalled via <paramref name="failedScenarios"/>; the remedy is fix
    /// the scenario and re-run.
    /// </item>
    /// <item>
    /// ffmpeg is unavailable. This is a <i>sanctioned</i> degradation, not a failure - the spec
    /// deliberately lets a run succeed without ffmpeg, stills-only, with a warning
    /// (<see cref="ShotContext.RecordAsync"/> keeps the frames and returns normally rather than
    /// throwing) - but it means that scenario's <c>.webm</c>/<c>.gif</c> were not (re)produced
    /// this run either, and are just as silently absent from <paramref name="published"/> as a
    /// thrown exception's assets would be. Signalled via <paramref
    /// name="clipEncodingSkippedFor"/> (see <see cref="ShotRun.ClipEncodingSkippedFor"/>); the
    /// remedy is install ffmpeg, not fix a scenario - reporting it as a phantom scenario failure
    /// would send an operator chasing the wrong thing.
    /// </item>
    /// </list>
    /// Both cases share the same underlying problem - the disk cannot tell "this run didn't
    /// produce it" apart from "nobody produces this anymore" - and the same resolution: refuse
    /// the whole prune (not just the affected scenarios' own files) with a no-op and a console
    /// warning, never an exception, for the same reason as the subset-run guard above. This list
    /// closes the two ways under-production is known to happen today, not the concept in
    /// general - a hypothetical third way would need its own signal threaded through the same
    /// way <paramref name="failedScenarios"/> and <paramref name="clipEncodingSkippedFor"/> are.
    /// </item>
    /// <item>
    /// <b>Non-recursive, top-level scan only.</b> <see cref="Directory.GetFiles(string)"/>'s
    /// default <see cref="SearchOption.TopDirectoryOnly"/> structurally excludes
    /// <c>docs/assets/shots/hero/</c> - no hardcoded "skip the folder named hero" exception is
    /// needed, and none could be trusted as much as the directory tree itself. That folder holds
    /// manually captured hero shots (<see cref="Program"/> never writes into it) which never
    /// appear in any manifest; a recursive scan would read them as universally "not published
    /// this run" and delete them. The converse follows by construction and is <i>not</i>
    /// protected: a hero shot committed directly at the top level of
    /// <c>docs/assets/shots/</c> (rather than under <c>hero/</c>) is scanned like any other file
    /// and will be deleted by a full-catalogue prune if its basename never appears in <paramref
    /// name="published"/>. This is intended, not an oversight - keep manually curated hero shots
    /// under <c>hero/</c> if they must never be pruned.
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
    /// <param name="failedScenarios">
    /// The names of scenarios that threw during this run (see <c>Program.Main</c>'s per-scenario
    /// catch), regardless of how many scenarios were requested. Any non-empty list refuses the
    /// prune outright - see the production-completeness guard above.
    /// </param>
    /// <param name="clipEncodingSkippedFor">
    /// The names of scenarios whose clip encoding was skipped this run because ffmpeg was
    /// unavailable (<see cref="ShotRun.ClipEncodingSkippedFor"/>) - a sanctioned degradation, not
    /// a failure, but one that refuses the prune outright the same way <paramref
    /// name="failedScenarios"/> does, with its own warning naming the real cause. See the
    /// production-completeness guard above.
    /// </param>
    /// <returns>
    /// The paths deleted (or, under <paramref name="dryRun"/>, that would be deleted), relative
    /// to <paramref name="repositoryRoot"/>.
    /// </returns>
    public static IReadOnlyList<string> Prune(
        IReadOnlyList<string> published,
        string repositoryRoot,
        bool isFullCatalogueRun,
        bool dryRun,
        IReadOnlyList<string> failedScenarios,
        IReadOnlyList<string> clipEncodingSkippedFor)
    {
        if (!isFullCatalogueRun)
        {
            Console.Error.WriteLine(
                "[shots] --prune requires a full-catalogue run (no scenario name filter on the " +
                "command line) - skipping prune, since a subset run's published list would make " +
                "every other scenario's committed assets look stale and delete them.");
            return [];
        }

        // Both checked (not else-if'd on each other) and both allowed to print, so a run that
        // hits both at once - a thrown scenario AND ffmpeg missing - reports both real causes
        // instead of only the first one found, on the theory that whoever reads this needs the
        // complete picture before deciding what to re-run or install.
        bool productionIncomplete = false;

        if (failedScenarios.Count > 0)
        {
            Console.Error.WriteLine(
                "[shots] --prune skipped: this run was incomplete - " +
                $"{failedScenarios.Count} scenario(s) failed and published nothing " +
                $"({string.Join(", ", failedScenarios)}). Their previously-committed assets " +
                "would look indistinguishable from stale files and be deleted. Re-run without " +
                "failures, then retry --prune.");
            productionIncomplete = true;
        }

        if (clipEncodingSkippedFor.Count > 0)
        {
            Console.Error.WriteLine(
                "[shots] --prune skipped: ffmpeg was unavailable for " +
                $"{clipEncodingSkippedFor.Count} scenario(s) this run " +
                $"({string.Join(", ", clipEncodingSkippedFor)}), so their .webm/.gif clips were " +
                "not (re)produced and would look indistinguishable from stale files and be " +
                "deleted. Install ffmpeg (or put it on PATH), then re-run and retry --prune.");
            productionIncomplete = true;
        }

        if (productionIncomplete)
        {
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
