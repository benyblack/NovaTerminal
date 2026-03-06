using System;
using Xunit;
using NovaTerminal.Core;
using NovaTerminal.Core.Storage;

namespace NovaTerminal.Tests.BufferTests
{
    /// <summary>
    /// Tests for extended text and hyperlink preservation in paged scrollback.
    /// </summary>
    public class ScrollbackMetadataTests
    {
        [Fact]
        public void AppendRow_WithExtendedText_IsRetained()
        {
            var pool = new TerminalPagePool();
            var scrollback = new ScrollbackPages(10, pool, maxScrollbackBytes: 16L * 1024 * 1024);

            var ext = new SmallMap<string>();
            ext.Set(2, "🎉"); // emoji at column 2

            var row = new TerminalCell[10];
            scrollback.AppendRow(row, false, extendedText: ext);

            Assert.Equal(1, scrollback.Count);
            var retrieved = scrollback.GetExtendedTextMap(0);
            Assert.NotNull(retrieved);
            Assert.True(retrieved!.TryGet(2, out var emoji));
            Assert.Equal("🎉", emoji);

            pool.Clear();
        }

        [Fact]
        public void AppendRow_WithHyperlink_IsRetained()
        {
            var pool = new TerminalPagePool();
            var scrollback = new ScrollbackPages(10, pool, maxScrollbackBytes: 16L * 1024 * 1024);

            var links = new SmallMap<string>();
            links.Set(5, "https://example.com");

            var row = new TerminalCell[10];
            scrollback.AppendRow(row, false, hyperlinks: links);

            Assert.Equal(1, scrollback.Count);
            var retrieved = scrollback.GetHyperlinkMap(0);
            Assert.NotNull(retrieved);
            Assert.True(retrieved!.TryGet(5, out var url));
            Assert.Equal("https://example.com", url);

            pool.Clear();
        }

        [Fact]
        public void AppendRow_MultipleRowsOnSamePage_AllMetadataRetained()
        {
            var pool = new TerminalPagePool();
            var scrollback = new ScrollbackPages(10, pool, maxScrollbackBytes: 16L * 1024 * 1024);

            var row = new TerminalCell[10];

            // Row 0: emoji in col 0
            var ext0 = new SmallMap<string>(); ext0.Set(0, "あ");
            scrollback.AppendRow(row, false, extendedText: ext0);

            // Row 1: no metadata
            scrollback.AppendRow(row);

            // Row 2: hyperlink in col 3
            var links2 = new SmallMap<string>(); links2.Set(3, "https://nova.dev");
            scrollback.AppendRow(row, false, hyperlinks: links2);

            Assert.Equal(3, scrollback.Count);

            var m0 = scrollback.GetExtendedTextMap(0);
            Assert.NotNull(m0);
            Assert.True(m0!.TryGet(0, out var c0));
            Assert.Equal("あ", c0);

            Assert.Null(scrollback.GetExtendedTextMap(1));
            Assert.Null(scrollback.GetHyperlinkMap(1));

            var m2 = scrollback.GetHyperlinkMap(2);
            Assert.NotNull(m2);
            Assert.True(m2!.TryGet(3, out var u2));
            Assert.Equal("https://nova.dev", u2);

            pool.Clear();
        }

        [Fact]
        public void AppendRow_NoMetadata_GettersReturnNull()
        {
            var pool = new TerminalPagePool();
            var scrollback = new ScrollbackPages(10, pool, maxScrollbackBytes: 16L * 1024 * 1024);

            var row = new TerminalCell[10];
            scrollback.AppendRow(row);

            Assert.Null(scrollback.GetExtendedTextMap(0));
            Assert.Null(scrollback.GetHyperlinkMap(0));

            pool.Clear();
        }

        [Fact]
        public void SmallMap_ForEach_IteratesAllEntries()
        {
            var map = new SmallMap<string>();
            map.Set(1, "one");
            map.Set(3, "three");
            map.Set(5, "five");

            var collected = new System.Collections.Generic.Dictionary<int, string>();
            map.ForEach((k, v) => collected[k] = v);

            Assert.Equal(3, collected.Count);
            Assert.Equal("one", collected[1]);
            Assert.Equal("three", collected[3]);
            Assert.Equal("five", collected[5]);
        }

        [Fact]
        public void TerminalRow_GetExtendedTextMap_ReturnsMapAfterSet()
        {
            var row = new TerminalRow(10);
            row.SetExtendedText(4, "é");

            var map = row.GetExtendedTextMap();
            Assert.NotNull(map);
            Assert.True(map!.TryGet(4, out var text));
            Assert.Equal("é", text);
        }

        [Fact]
        public void TerminalRow_GetHyperlinkMap_ReturnsMapAfterSet()
        {
            var row = new TerminalRow(10);
            row.SetHyperlink(7, "https://example.org");

            var map = row.GetHyperlinkMap();
            Assert.NotNull(map);
            Assert.True(map!.TryGet(7, out var link));
            Assert.Equal("https://example.org", link);
        }

        [Fact]
        public void TerminalPage_Metadata_SurvivesAcrossRows()
        {
            var page = new TerminalPage(64, 10);

            // Row 0: extended text — build map separately and attach
            var extMap = new SmallMap<string>();
            extMap.Set(1, "ñ");
            page.SetExtendedTextFromMap(0, extMap);

            // Row 1: hyperlink — build map separately and attach
            var linkMap = new SmallMap<string>();
            linkMap.Set(9, "http://test.io");
            page.SetHyperlinkFromMap(1, linkMap);

            Assert.Equal("ñ", page.GetExtendedText(0, 1));
            Assert.Equal("http://test.io", page.GetHyperlink(1, 9));

            // Row 2 has nothing
            Assert.Null(page.GetExtendedTextMap(2));
            Assert.Null(page.GetHyperlinkMap(2));

            page.ReturnToPool();
        }
    }
}
