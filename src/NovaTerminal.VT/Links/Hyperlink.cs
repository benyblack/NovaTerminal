namespace NovaTerminal.VT.Links
{
    /// <summary>
    /// One OSC 8 hyperlink identity. Cells that belong to the same logical link hold a reference to the
    /// same instance, so "are these two cells the same link?" is reference equality.
    /// </summary>
    /// <remarks>
    /// #95 gap 2: the side table used to hold a bare URI string per cell, which cannot express identity.
    /// The OSC 8 spec is explicit that identity is the <em>pair</em>:
    ///
    /// <blockquote>Character cells that have the same target URI and the same nonempty <c>id</c> are always
    /// underlined together on mouseover. The same <c>id</c> is only used for connecting character cells
    /// whose URIs is also the same.</blockquote>
    ///
    /// So neither half alone is sufficient. Two adjacent links to the same URI with different ids are two
    /// links; the same id against two different URIs is not one link.
    ///
    /// For links written <em>without</em> an id, the spec allows either of two heuristics and recommends
    /// VTE's: assign a fresh identity each time an OSC 8 with a URI but no id is encountered. We do that,
    /// which means every hyperlink cell has an identity and the ambiguous case simply does not arise. The
    /// alternative (iTerm2's: join adjacent cells with equal URIs) would silently merge two consecutive
    /// distinct links that happen to share a URI — the exact case <c>id</c> exists to disambiguate.
    ///
    /// Instances are interned by <see cref="HyperlinkRegistry"/>. Reference equality is the identity test;
    /// value equality is deliberately <em>not</em> implemented, because two separately-written no-id links
    /// to the same URI are equal by value and must still be distinct links.
    /// </remarks>
    public sealed class Hyperlink
    {
        internal Hyperlink(string uri, string? id)
        {
            Uri = uri;
            Id = id;
        }

        /// <summary>The link target, exactly as received (already URI-encoded per the spec).</summary>
        public string Uri { get; }

        /// <summary>
        /// The explicit <c>id=</c> parameter, or <c>null</c> when the producer did not supply one.
        /// </summary>
        /// <remarks>
        /// The spec treats an empty id and an absent id as interchangeable, so both arrive here as
        /// <c>null</c>. This is exposed for diagnostics and tests; identity comparisons should use
        /// reference equality rather than reading this back out.
        /// </remarks>
        public string? Id { get; }

        public override string ToString() => Id is null ? Uri : $"{Uri} (id={Id})";
    }
}
