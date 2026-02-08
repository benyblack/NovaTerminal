using SkiaSharp;
using System.Threading;

namespace NovaTerminal.Core
{
    public class SharedSKTypeface
    {
        private SKTypeface? _typeface;
        private int _refCount;

        public SKTypeface? Typeface => _typeface;

        public SharedSKTypeface(SKTypeface typeface)
        {
            _typeface = typeface;
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
                _typeface?.Dispose();
                _typeface = null;
            }
        }
    }
}
