namespace NovaTerminal.VT.Links
{
    /// <summary>A detected link: character range [StartChar, EndChar) in a line plus its resolved URI.</summary>
    public readonly record struct LinkSpan(int StartChar, int EndChar, string Uri);
}
