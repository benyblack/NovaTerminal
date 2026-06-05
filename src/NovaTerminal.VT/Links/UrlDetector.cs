using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace NovaTerminal.VT.Links
{
    /// <summary>Detects links in a single line of text by applying an ordered list of LinkRules.</summary>
    public sealed class UrlDetector
    {
        private readonly IReadOnlyList<LinkRule> _rules;

        public UrlDetector(IReadOnlyList<LinkRule>? rules = null) => _rules = rules ?? DefaultRules;

        public static IReadOnlyList<LinkRule> DefaultRules { get; } = new[]
        {
            new LinkRule(
                "scheme",
                new Regex(@"[a-zA-Z][a-zA-Z0-9+.\-]*://[^\s]+", RegexOptions.Compiled),
                text => text,
                trimTrailingPunctuation: true),
        };

        public IReadOnlyList<LinkSpan> Detect(string line)
        {
            if (string.IsNullOrEmpty(line)) return Array.Empty<LinkSpan>();

            var spans = new List<LinkSpan>();
            foreach (var rule in _rules)
            {
                foreach (Match m in rule.Pattern.Matches(line))
                {
                    int start = m.Index;
                    int end = m.Index + m.Length; // exclusive
                    if (rule.TrimTrailingPunctuation) end = TrimTrailingEnd(line, start, end);
                    if (end <= start) continue;
                    string text = line.Substring(start, end - start);
                    spans.Add(new LinkSpan(start, end, rule.Resolve(text)));
                }
            }

            // Deterministic order, then drop overlaps (earliest span wins).
            spans.Sort((a, b) => a.StartChar != b.StartChar ? a.StartChar - b.StartChar : a.EndChar - b.EndChar);
            var result = new List<LinkSpan>();
            int lastEnd = -1;
            foreach (var s in spans)
            {
                if (s.StartChar >= lastEnd) { result.Add(s); lastEnd = s.EndChar; }
            }
            return result;
        }

        private static int TrimTrailingEnd(string line, int start, int end)
        {
            const string punct = ".,;:!?\"'";
            while (end > start)
            {
                char c = line[end - 1];
                if (punct.Contains(c)) { end--; continue; }
                if (c == ')' && CountChar(line, start, end, '(') < CountChar(line, start, end, ')')) { end--; continue; }
                if (c == ']' && CountChar(line, start, end, '[') < CountChar(line, start, end, ']')) { end--; continue; }
                if (c == '}' && CountChar(line, start, end, '{') < CountChar(line, start, end, '}')) { end--; continue; }
                break;
            }
            return end;
        }

        private static int CountChar(string line, int start, int end, char target)
        {
            int n = 0;
            for (int i = start; i < end; i++) if (line[i] == target) n++;
            return n;
        }
    }
}
