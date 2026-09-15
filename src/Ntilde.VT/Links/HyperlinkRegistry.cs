using System;
using System.Collections.Generic;

namespace NovaTerminal.VT.Links
{
    /// <summary>
    /// Interns <see cref="Hyperlink"/> instances so that cells belonging to one logical link share a
    /// reference, per OSC 8's (URI, id) identity rule.
    /// </summary>
    /// <remarks>
    /// #95 gap 2. Two behaviours, both from the spec:
    ///
    /// <list type="bullet">
    /// <item><b>With an explicit id</b> — the same (URI, id) pair returns the <em>same</em> instance, even
    /// across unrelated writes. That is the whole point of <c>id</c>: it lets a producer state that
    /// non-contiguous runs, possibly separated by a line break or another pane, are one anchor.</item>
    /// <item><b>Without an id</b> — every call returns a <em>fresh</em> instance, so cells printed in one
    /// OSC 8 run are joined and separate runs are not. This is VTE's heuristic, which the spec recommends
    /// as the easier of the two permitted options.</item>
    /// </list>
    ///
    /// OSC 8 arrives from the remote, so both dimensions are bounded. The interning table only grows for
    /// links that carry an explicit id, but a hostile stream can emit unlimited distinct ids, so entries
    /// are capped and the table is cleared wholesale when the cap is hit rather than evicting one entry at
    /// a time: losing interning degrades hover grouping for old links, it does not corrupt anything, and a
    /// stream pathological enough to reach the cap has no legitimate grouping to preserve.
    ///
    /// No-id links are deliberately not interned, so they cost nothing here — a fresh instance per run is
    /// garbage-collectable as soon as the rows holding it are evicted.
    /// </remarks>
    public sealed class HyperlinkRegistry
    {
        /// <summary>
        /// De facto URI ceiling. The spec records VTE and iTerm2 both capping at 2083 and notes there is no
        /// de jure limit. Longer targets are refused rather than truncated: a truncated URI is a URI that
        /// points somewhere else, which is worse than no link at all.
        /// </summary>
        public const int MaxUriLength = 2083;

        /// <summary>
        /// Ceiling on an explicit id. The spec mentions VTE's 250 while explicitly warning not to rely on
        /// that number, so this is a safety bound, not a compatibility claim. An over-long id is dropped
        /// and the link is treated as having no id, which downgrades grouping without losing the link.
        /// </summary>
        public const int MaxIdLength = 250;

        /// <summary>Cap on distinct explicit-id links held for interning.</summary>
        public const int MaxInternedLinks = 4096;

        private readonly Dictionary<(string Uri, string Id), Hyperlink> _interned = new();

        /// <summary>
        /// Resolves an OSC 8 open into a hyperlink identity, or <c>null</c> if the sequence does not name a
        /// usable target (which is how OSC 8 signals "close the current link").
        /// </summary>
        /// <param name="parameters">The raw <c>params</c> field, between the two semicolons.</param>
        /// <param name="uri">The raw URI field.</param>
        public Hyperlink? Resolve(string? parameters, string? uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
            {
                return null;
            }

            if (uri.Length > MaxUriLength)
            {
                return null;
            }

            string? id = ExtractId(parameters);

            // No id: a fresh identity per OSC 8 open, so this run groups with itself and nothing else.
            if (id is null)
            {
                return new Hyperlink(uri, null);
            }

            var key = (uri, id);
            if (_interned.TryGetValue(key, out Hyperlink? existing))
            {
                return existing;
            }

            if (_interned.Count >= MaxInternedLinks)
            {
                _interned.Clear();
            }

            var link = new Hyperlink(uri, id);
            _interned[key] = link;
            return link;
        }

        /// <summary>
        /// Pulls <c>id</c> out of the OSC 8 params field, or <c>null</c> when absent, empty or unusable.
        /// </summary>
        /// <remarks>
        /// Format is <c>key=value</c> pairs separated by <c>:</c> — e.g. <c>id=xyz123:foo=bar</c>. Only
        /// <c>id</c> is defined today and the field exists for future extension, so unknown keys are
        /// skipped rather than treated as an error. The spec states an empty id and an absent id are
        /// interchangeable, so both yield <c>null</c> here and the caller falls back to a fresh identity.
        /// </remarks>
        internal static string? ExtractId(string? parameters)
        {
            if (string.IsNullOrEmpty(parameters))
            {
                return null;
            }

            int start = 0;
            while (start < parameters.Length)
            {
                int end = parameters.IndexOf(':', start);
                if (end < 0)
                {
                    end = parameters.Length;
                }

                int eq = parameters.IndexOf('=', start);
                if (eq >= 0 && eq < end)
                {
                    // Only 'id' is defined; every other key is skipped so the field stays extensible.
                    if (string.CompareOrdinal(parameters, start, "id", 0, 2) == 0 && eq - start == 2)
                    {
                        int valueStart = eq + 1;
                        int valueLength = end - valueStart;
                        if (valueLength > 0 && valueLength <= MaxIdLength)
                        {
                            string value = parameters.Substring(valueStart, valueLength);
                            return string.IsNullOrWhiteSpace(value) ? null : value;
                        }

                        // Present but empty, or absurdly long: treat as no id.
                        return null;
                    }
                }

                start = end + 1;
            }

            return null;
        }
    }
}
