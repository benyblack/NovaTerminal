using NovaTerminal.Core.Storage;

namespace NovaTerminal.Core
{
    public record struct TerminalMemoryMetrics(
        long ScrollbackBytes,
        int ActivePages,
        int PooledPages,
        int ViewportCells,
        int GlyphCacheEntries,
        long GlyphCacheAtlasBytes
    );

    public partial class TerminalBuffer
    {
        public TerminalMemoryMetrics GetMemoryMetrics(int glyphCacheEntries = 0, long glyphCacheAtlasBytes = 0)
        {
            ScrollbackMetrics sb;
            int viewportCells;

            bool lockTaken = EnterReadLockIfNeeded();
            try
            {
                sb = _scrollback.GetMetrics();
                viewportCells = Rows * Cols;
            }
            finally { ExitReadLockIfNeeded(Lock, lockTaken); }

            return new TerminalMemoryMetrics
            {
                ScrollbackBytes = sb.BytesUsed,
                ActivePages = sb.ActivePages,
                PooledPages = sb.PooledPages,
                ViewportCells = viewportCells,
                GlyphCacheEntries = glyphCacheEntries,
                GlyphCacheAtlasBytes = glyphCacheAtlasBytes
            };
        }
    }
}
