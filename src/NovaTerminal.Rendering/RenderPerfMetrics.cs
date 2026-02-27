namespace NovaTerminal.Core
{
    public struct RenderPerfMetrics
    {
        public long FrameIndex { get; set; }
        public double FrameTimeMs { get; set; }
        public int DirtyRows { get; set; }
        public int DirtySpansTotal { get; set; }
        public int DirtySpanCount { get; set; }
        public int SpanRenderCount { get; set; }
        public int RowRenderCount { get; set; }
        public int DirtyCellsEstimated { get; set; }
        public int DrawCallsText { get; set; }
        public int DrawCallsRects { get; set; }
        public int DrawCallsTotal { get; set; }
        public int RowPictureCacheHits { get; set; }
        public int RowPictureCacheMisses { get; set; }
        public int PictureBuilds { get; set; }
        public int FlushCount { get; set; }
        public int AtlasAlphaGlyphs { get; set; }
        public int AtlasColorGlyphs { get; set; }
        public int DirectDrawTextCount { get; set; }
        public int ShapedTextRuns { get; set; }
        public long AllocBytesThisFrame { get; set; }
    }
}
