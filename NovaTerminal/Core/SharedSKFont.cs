using SkiaSharp;
using System.Threading;

namespace NovaTerminal.Core
{
    public class SharedSKFont
    {
        private SKFont? _font;
        private int _refCount;

        public SKFont? Font => _font;

        public SharedSKFont(SKFont font)
        {
            _font = font;
            _refCount = 1;
        }

        public void Increment()
        {
            Interlocked.Increment(ref _refCount);
        }

        public void Dispose()
        {
            if (Interlocked.Decrement(ref _refCount) == 0)
            {
                _font?.Dispose();
                _font = null;
            }
        }
    }
}
