using System;
using System.Text.RegularExpressions;

namespace NovaTerminal.VT.Links
{
    /// <summary>
    /// One link-detection rule. Adding a new kind of clickable text = appending a LinkRule
    /// to UrlDetector's rule list; nothing else changes.
    /// </summary>
    public sealed class LinkRule
    {
        public string Name { get; }
        public Regex Pattern { get; }
        /// <summary>Maps the matched (post-trim) text to a final URI (e.g. "mailto:" + addr).</summary>
        public Func<string, string> Resolve { get; }
        /// <summary>When true, trailing sentence punctuation and unbalanced closing brackets are trimmed.</summary>
        public bool TrimTrailingPunctuation { get; }

        public LinkRule(string name, Regex pattern, Func<string, string> resolve, bool trimTrailingPunctuation = false)
        {
            Name = name;
            Pattern = pattern;
            Resolve = resolve;
            TrimTrailingPunctuation = trimTrailingPunctuation;
        }
    }
}
