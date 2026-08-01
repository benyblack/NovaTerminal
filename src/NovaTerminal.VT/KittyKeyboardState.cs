using System;
using System.Globalization;

namespace NovaTerminal.VT
{
    /// <summary>
    /// Kitty keyboard protocol progressive-enhancement state
    /// (https://sw.kovidgoyal.net/kitty/keyboard-protocol/).
    ///
    /// The protocol keeps a stack of flag sets per screen buffer; the top of the stack IS
    /// the currently active flag set and an empty stack means "all flags off". That mirrors
    /// kitty's own implementation and makes the spec rule "if a pop request empties the
    /// stack, all flags are reset" fall out for free.
    ///
    /// Main and alternate screens keep independent stacks (spec requirement) so a full-screen
    /// editor can raise the keyboard mode inside the alternate screen without disturbing - or
    /// even knowing - the shell's mode on the main screen.
    ///
    /// SCOPE / DEVIATION: NovaTerminal only implements the disambiguate-escape-codes tier
    /// (0b1). Unsupported bits (report event types, report alternate keys, report all keys,
    /// report associated text) are masked out on push/set, so they are never stored and never
    /// echoed by the CSI ? u query. This is deliberate: the spec's detection section tells
    /// applications to "set the desired progressive enhancements and then query" to discover
    /// partial implementations, which only works if the terminal reports the flags actually
    /// in effect. Echoing bits we do not honor would make crossterm/Codex believe key release
    /// events are coming when they are not.
    /// </summary>
    public sealed class KittyKeyboardState
    {
        /// <summary>0b1 - disambiguate escape codes. The only tier NovaTerminal implements.</summary>
        public const int FlagDisambiguateEscapeCodes = 0b1;

        /// <summary>Bit mask of the flags this terminal honors; everything else is dropped.</summary>
        public const int SupportedFlags = FlagDisambiguateEscapeCodes;

        /// <summary>
        /// Stack depth cap. The spec only says "terminals should limit the size of the stack
        /// as appropriate, to prevent Denial-of-Service attacks"; when a push overflows, the
        /// oldest entry is evicted (spec-mandated behavior).
        /// </summary>
        public const int MaxStackDepth = 32;

        private readonly object _gate = new();
        private readonly int[] _mainStack = new int[MaxStackDepth];
        private readonly int[] _altStack = new int[MaxStackDepth];
        private int _mainCount;
        private int _altCount;
        private bool _altActive;

        // Cached top-of-active-stack so the UI thread can read the flags while the PTY
        // reader thread mutates the stack, without taking a lock on every keystroke.
        private volatile int _currentFlags;

        /// <summary>Flags currently in effect for the active screen buffer.</summary>
        public int Flags => _currentFlags;

        /// <summary>True when the disambiguate-escape-codes tier is active.</summary>
        public bool DisambiguateEscapeCodes => (_currentFlags & FlagDisambiguateEscapeCodes) != 0;

        /// <summary>Depth of the active screen buffer's stack (diagnostics/tests).</summary>
        public int StackDepth
        {
            get { lock (_gate) { return _altActive ? _altCount : _mainCount; } }
        }

        /// <summary>True when the alternate screen's stack is the active one.</summary>
        public bool IsAltScreenActive
        {
            get { lock (_gate) { return _altActive; } }
        }

        /// <summary>CSI &gt; flags u - push a new flag set. Missing flags default to 0.</summary>
        public void Push(int flags)
        {
            lock (_gate)
            {
                int[] stack = _altActive ? _altStack : _mainStack;
                ref int count = ref (_altActive ? ref _altCount : ref _mainCount);

                if (count == MaxStackDepth)
                {
                    // Spec: "If a push request is received and the stack is full, the oldest
                    // entry from the stack must be evicted."
                    Array.Copy(stack, 1, stack, 0, MaxStackDepth - 1);
                    count--;
                }

                stack[count++] = Mask(flags);
                RefreshCurrentFlagsNoLock();
            }
        }

        /// <summary>CSI &lt; n u - pop n entries (default 1). Popping past the bottom clears all flags.</summary>
        public void Pop(int count)
        {
            if (count <= 0) count = 1;

            lock (_gate)
            {
                ref int depth = ref (_altActive ? ref _altCount : ref _mainCount);
                depth = Math.Max(0, depth - count);
                RefreshCurrentFlagsNoLock();
            }
        }

        /// <summary>
        /// CSI = flags ; mode u - mode 1 replaces all bits, 2 ORs the set bits in,
        /// 3 clears the set bits. Applies to the top of the active stack; if the stack is
        /// empty an implicit entry is created so the flags become addressable.
        /// </summary>
        public void Set(int flags, int mode)
        {
            if (mode <= 0) mode = 1;

            lock (_gate)
            {
                int[] stack = _altActive ? _altStack : _mainStack;
                ref int count = ref (_altActive ? ref _altCount : ref _mainCount);

                if (count == 0)
                {
                    stack[0] = 0;
                    count = 1;
                }

                int masked = Mask(flags);
                int index = count - 1;
                stack[index] = mode switch
                {
                    2 => stack[index] | masked,
                    3 => stack[index] & ~masked,
                    _ => masked
                };

                RefreshCurrentFlagsNoLock();
            }
        }

        /// <summary>
        /// Selects which per-screen-buffer stack is active. Called by the buffer whenever it
        /// enters or leaves the alternate screen.
        /// </summary>
        public void SetActiveScreen(bool isAltScreen)
        {
            lock (_gate)
            {
                _altActive = isAltScreen;
                RefreshCurrentFlagsNoLock();
            }
        }

        /// <summary>Clears both stacks and returns to the main screen stack (RIS / full reset).</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _mainCount = 0;
                _altCount = 0;
                _altActive = false;
                _currentFlags = 0;
            }
        }

        /// <summary>Reply payload for the CSI ? u query: CSI ? flags u.</summary>
        public string FormatQueryResponse()
        {
            return string.Concat(
                ((char)0x1b).ToString(),
                "[?",
                _currentFlags.ToString(CultureInfo.InvariantCulture),
                "u");
        }

        public KittyKeyboardState Clone()
        {
            var clone = new KittyKeyboardState();
            lock (_gate)
            {
                Array.Copy(_mainStack, clone._mainStack, MaxStackDepth);
                Array.Copy(_altStack, clone._altStack, MaxStackDepth);
                clone._mainCount = _mainCount;
                clone._altCount = _altCount;
                clone._altActive = _altActive;
                clone._currentFlags = _currentFlags;
            }

            return clone;
        }

        private static int Mask(int flags) => flags <= 0 ? 0 : flags & SupportedFlags;

        private void RefreshCurrentFlagsNoLock()
        {
            int count = _altActive ? _altCount : _mainCount;
            int[] stack = _altActive ? _altStack : _mainStack;
            _currentFlags = count > 0 ? stack[count - 1] : 0;
        }
    }
}
