using System;
using System.Collections.Generic;
using System.Text;

namespace NovaTerminal.VT
{
    /// <summary>
    /// Reads the live command line out of the terminal grid: the cells from an
    /// <c>OSC 133;B</c> mark (prompt end / first cell of user input) to the cursor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Placement.</b> This lives in <c>NovaTerminal.VT</c> rather than in
    /// <c>NovaTerminal.CommandAssist</c> (where the V2 plan first sketched it) because the
    /// extraction is pure buffer walking — wrap flags, paged scrollback, wide-cell
    /// continuations, the deferred-autowrap cursor — and the layering tests forbid
    /// <c>NovaTerminal.CommandAssist</c> from referencing <c>NovaTerminal.VT</c>. Command
    /// Assist consumes the result through the App boundary.
    /// </para>
    /// <para>
    /// <b>Lock discipline.</b> <see cref="TryReadCommandLine"/> takes the buffer read lock
    /// itself unless the calling thread already holds a read or write lock —
    /// <see cref="TerminalBuffer.Lock"/> is non-recursive, so blind re-entry would throw. The
    /// underlying cell accessors assert the lock is held.
    /// </para>
    /// <para>
    /// <b>It never throws and never guesses.</b> Every ambiguity resolves to <c>false</c>:
    /// a mark from a dead coordinate generation, a mark whose line has aged out of scrollback,
    /// an alt-screen mark or an active alt screen, a cursor above the mark, a mark position
    /// outside the buffer, or an implausibly large span (see <see cref="MaxSpanRows"/>).
    /// </para>
    /// <para>
    /// <b>Right-prompt (RPROMPT) exclusion.</b> zsh's <c>RPROMPT</c> — and fish's
    /// <c>fish_right_prompt</c>, and starship's right prompt — paints right-aligned text on the
    /// same physical row as the input. Naively reading "mark column to last non-blank cell"
    /// swallows it. Stopping at the cursor instead is wrong, because the cursor is often
    /// mid-line. The rule used here, applied <i>only</i> to the final row of the span and
    /// <i>only</i> when the cursor is on that row, reads the row as
    /// <c>[input][gap][badge]</c> and requires all of:
    /// <list type="number">
    /// <item><description>the trailing content must end within
    /// <see cref="MaxRightPromptIndent"/> columns of the right edge (right-aligned text does;
    /// typed input generally does not, and <c>ZLE_RPROMPT_INDENT</c> defaults to 1);</description></item>
    /// <item><description>the gap must start at or after the cursor. Nothing left of the
    /// cursor is ever discarded;</description></item>
    /// <item><description>the gap is the <i>widest</i> run of blank cells in that region, so a
    /// multi-segment right prompt such as <c>12:34  ok</c> is trimmed whole instead of being cut
    /// at its own internal gap (which would keep the wide run and the left segment — worse than
    /// not trimming at all);</description></item>
    /// <item><description>the gap must be at least <see cref="MinRightPromptGap"/> cells wide
    /// <i>and strictly wider than the badge it separates</i>. A right prompt is a small label
    /// pushed to the edge by the row's slack; a stray double space inside typed input is
    /// not;</description></item>
    /// <item><description>the badge must be no wider than
    /// <c>Cols / </c><see cref="MaxRightPromptWidthDivisor"/> columns. Right prompts are badges,
    /// not sentences.</description></item>
    /// </list>
    /// All five must hold. Conditions 4 and 5 are what stop the reader deleting typed input:
    /// without them a row such as <c>echo aaaa...aa  bbbb</c> that happens to reach the right
    /// edge, with the cursor parked at the start of the line (Home), silently loses its
    /// <c>bbbb</c> — condition 2 does not protect content to the <i>right</i> of a mid-line
    /// cursor. The failure mode is deliberately asymmetric: an unrecognised right prompt comes
    /// back as extra text, which a consumer can survive, while a mis-recognised one deletes what
    /// the user typed. A badge wider than the gap in front of it, or wider than a third of the
    /// row, is therefore kept.
    /// Shells drop the right prompt as soon as input grows into it, which is why rows other than
    /// the cursor's are exempt: a soft-wrapped row is full of input by definition.
    /// </para>
    /// <para>
    /// <b>Multiline.</b> The span always runs to the end of the logical line the cursor is on
    /// (soft-wrapped continuations are followed <i>past</i> the cursor row — the cursor may be
    /// on the first of three wrapped rows). Hard line breaks inside the span are emitted as
    /// <c>'\n'</c> and raise <see cref="GridCommandLine.IsMultiline"/>; the text is returned
    /// raw, continuation-prompt paint included. See <see cref="GridCommandLine.IsMultiline"/>
    /// for why, and for what consumers may do with it.
    /// </para>
    /// <para>
    /// <b>Known limitation: multiline entries read from an earlier line.</b> The span stops at
    /// the end of the cursor's logical line, so if the user arrows <i>up</i> inside a
    /// continuation entry the reader returns that line alone with
    /// <see cref="GridCommandLine.IsMultiline"/> clear — truthful for the region read, but not
    /// the whole entry. The alternative, extending across hard breaks while the row below has
    /// content, was rejected: zsh prints its completion listing directly below the input line,
    /// so that rule would misfire on every tab completion, which is precisely the case Command
    /// Assist most needs to get right.
    /// </para>
    /// <para>
    /// <b>Known limitation.</b> A prompt that ends exactly on the last column leaves the cursor
    /// parked on that column with the wrap deferred, and the mark records only the column — not
    /// the deferred-wrap bit. The reader then starts one cell early and the text picks up the
    /// prompt's final character. Recording pending-wrap on the mark would fix it; it is not
    /// worth the cross-layer churn for a prompt that exactly fills the terminal width.
    /// </para>
    /// </remarks>
    public static class GridQueryReader
    {
        /// <summary>
        /// Upper bound on the number of physical rows a single command line may span. A span
        /// larger than this means the mark is being used outside its <c>B</c>-to-<c>C</c>
        /// window (so the "command line" is really command output), and building the string
        /// would be both wrong and expensive.
        /// </summary>
        public const int MaxSpanRows = 512;

        /// <summary>
        /// Blank cells required before a right-aligned prompt is recognised as one. A floor, not
        /// the whole test: the gap must also be strictly wider than the badge it separates.
        /// </summary>
        public const int MinRightPromptGap = 2;

        /// <summary>
        /// How far from the right edge a right-aligned prompt may stop. zsh's
        /// <c>ZLE_RPROMPT_INDENT</c> defaults to 1; 2 leaves a little slack.
        /// </summary>
        public const int MaxRightPromptIndent = 2;

        /// <summary>
        /// The widest a right prompt may be, as a fraction of the terminal width: a badge may
        /// occupy at most <c>Cols / MaxRightPromptWidthDivisor</c> columns. Anything larger is
        /// far more likely to be typed input that happens to reach the right edge, and is kept.
        /// </summary>
        public const int MaxRightPromptWidthDivisor = 3;

        /// <summary>
        /// Extracts the command line between <paramref name="mark"/> and the cursor.
        /// </summary>
        /// <returns>
        /// <c>true</c> and a populated <paramref name="result"/> when the mark is live and
        /// resolves to a readable span; <c>false</c> otherwise. Never throws.
        /// </returns>
        public static bool TryReadCommandLine(
            TerminalBuffer buffer,
            ShellIntegrationMark mark,
            out GridCommandLine result)
        {
            result = default;
            if (buffer is null)
            {
                return false;
            }

            // Non-recursive lock: only acquire if this thread is not already inside one. The
            // upgradeable-read case cannot arise on today's call paths, but re-entering from
            // inside one would throw, and this method's contract is that it never does.
            bool lockTaken = false;
            if (!buffer.Lock.IsReadLockHeld &&
                !buffer.Lock.IsWriteLockHeld &&
                !buffer.Lock.IsUpgradeableReadLockHeld)
            {
                buffer.Lock.EnterReadLock();
                lockTaken = true;
            }

            try
            {
                return TryReadCommandLineLocked(buffer, mark, out result);
            }
            finally
            {
                if (lockTaken)
                {
                    buffer.Lock.ExitReadLock();
                }
            }
        }

        private static bool TryReadCommandLineLocked(
            TerminalBuffer buffer,
            ShellIntegrationMark mark,
            out GridCommandLine result)
        {
            result = default;

            // Coordinate-space epoch first: after a scrollback reset a stale AbsoluteRow
            // resolves to a perfectly plausible row holding unrelated content, so no
            // arithmetic check downstream can catch it.
            if (mark.Generation != buffer.Scrollback.Generation)
            {
                return false;
            }

            // The alt screen has no scrollback and no shared row numbering; a full-screen
            // application owns the grid and there is no shell line editor to read.
            if (mark.IsAltScreen || buffer.IsAltScreenActive)
            {
                return false;
            }

            int cols = buffer.Cols;
            if (cols <= 0 || mark.Column < 0 || mark.Column >= cols)
            {
                return false;
            }

            long derivedRow = mark.AbsoluteRow - buffer.Scrollback.TotalRowsEvicted;
            if (derivedRow < 0)
            {
                return false; // the marked line aged out of history
            }

            int totalRows = buffer.InternalTotalLines;
            int startRow = (int)derivedRow;
            if (startRow >= totalRows)
            {
                return false;
            }

            int cursorRow = buffer.Scrollback.Count + buffer.InternalCursorRow;

            // Deferred autowrap: after a character lands on the last column the cursor stays
            // parked there with the wrap pending, so its *text* position is one past it.
            int cursorTextCol = buffer.IsPendingWrap
                ? Math.Min(buffer.InternalCursorCol + 1, cols)
                : buffer.InternalCursorCol;

            if (cursorRow < startRow)
            {
                return false; // cursor above the mark: the mark is stale
            }

            if (cursorRow == startRow && cursorTextCol < mark.Column)
            {
                return false; // cursor left of the mark on the mark's own row
            }

            // Follow soft wraps past the cursor: the cursor may be on the first of several
            // physical rows that make up one logical line.
            int endRow = cursorRow;
            while (endRow < totalRows - 1 && buffer.IsRowWrappedAbsolute(endRow))
            {
                endRow++;
                if (endRow - startRow + 1 > MaxSpanRows)
                {
                    return false;
                }
            }

            if (endRow - startRow + 1 > MaxSpanRows)
            {
                return false;
            }

            var text = new StringBuilder();
            int cursorOffset = -1;
            bool isMultiline = false;
            bool rightPromptTrimmed = false;

            for (int row = startRow; row <= endRow; row++)
            {
                int firstCol = row == startRow ? mark.Column : 0;
                bool isCursorRow = row == cursorRow;
                bool isLastRow = row == endRow;
                bool wrapped = !isLastRow && buffer.IsRowWrappedAbsolute(row);

                int endCol = wrapped
                    ? SoftWrappedRowEnd(buffer, row, cols)
                    : ContentRowEnd(
                        buffer,
                        row,
                        firstCol,
                        cols,
                        isCursorRow && isLastRow ? cursorTextCol : (int?)null,
                        ref rightPromptTrimmed);

                for (int col = firstCol; col < endCol; col++)
                {
                    if (isCursorRow && col == cursorTextCol)
                    {
                        cursorOffset = text.Length;
                    }

                    if (buffer.GetCellAbsolute(col, row).IsWideContinuation)
                    {
                        continue; // trailing half of a double-width cell
                    }

                    string grapheme = buffer.GetGraphemeAbsolute(col, row);
                    text.Append(string.IsNullOrEmpty(grapheme) || grapheme == "\0" ? " " : grapheme);
                }

                if (isCursorRow && cursorOffset < 0)
                {
                    // Cursor at (or past) the end of the row's content.
                    cursorOffset = text.Length;
                }

                if (!isLastRow && !wrapped)
                {
                    text.Append('\n');
                    isMultiline = true;
                }
            }

            if (cursorOffset < 0)
            {
                cursorOffset = text.Length;
            }

            result = new GridCommandLine(
                Text: text.ToString(),
                CursorOffset: cursorOffset,
                IsMultiline: isMultiline,
                RightPromptTrimmed: rightPromptTrimmed,
                StartRow: startRow,
                EndRow: endRow);
            return true;
        }

        /// <summary>
        /// Reads the last <paramref name="maxChars"/> characters of grid text that sit immediately
        /// behind the cursor, following soft-wrapped predecessor rows backwards as far as needed.
        /// </summary>
        /// <param name="buffer">The buffer to read.</param>
        /// <param name="maxChars">How many characters to read back from the cursor.</param>
        /// <param name="text">
        /// The text ending at the cursor. Shorter than <paramref name="maxChars"/> when the grid
        /// does not hold that much behind the cursor, which is itself an answer.
        /// </param>
        /// <returns>
        /// <c>false</c> when there is no readable grid at all (no buffer, an active alt screen, a
        /// cursor outside the buffer). Never throws.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This exists for one caller and one question: <b>did the shell echo what the user
        /// typed?</b> In a markless session the host has no <c>OSC 133;B</c> mark to read from, so
        /// it cannot say where the command line starts — but it can still ask whether a candidate
        /// string is painted on the screen ending at the cursor, which is what an echoing line
        /// editor produces and what a hidden password prompt does not. See
        /// <c>TerminalPane.WasAccumulatedTextEchoedAtCursor</c>.
        /// </para>
        /// <para>
        /// It deliberately does <i>not</i> try to identify the command line. It reads a fixed
        /// number of characters backwards and hands them over; deciding whether they match is the
        /// caller's job, and a mismatch means "do not capture" rather than "capture something
        /// else".
        /// </para>
        /// <para>
        /// Wide characters are compared as text, not columns: the trailing half of a double-width
        /// cell is skipped exactly as <see cref="TryReadCommandLine"/> skips it, so one CJK
        /// character is one character here too. Lock discipline matches
        /// <see cref="TryReadCommandLine"/>.
        /// </para>
        /// </remarks>
        public static bool TryReadTextEndingAtCursor(
            TerminalBuffer buffer,
            int maxChars,
            out string text)
        {
            text = string.Empty;
            if (buffer is null || maxChars < 0)
            {
                return false;
            }

            bool lockTaken = false;
            if (!buffer.Lock.IsReadLockHeld &&
                !buffer.Lock.IsWriteLockHeld &&
                !buffer.Lock.IsUpgradeableReadLockHeld)
            {
                buffer.Lock.EnterReadLock();
                lockTaken = true;
            }

            try
            {
                return TryReadTextEndingAtCursorLocked(buffer, maxChars, out text);
            }
            finally
            {
                if (lockTaken)
                {
                    buffer.Lock.ExitReadLock();
                }
            }
        }

        private static bool TryReadTextEndingAtCursorLocked(
            TerminalBuffer buffer,
            int maxChars,
            out string text)
        {
            text = string.Empty;

            // A full-screen application owns the grid; there is no echoing line editor to check.
            if (buffer.IsAltScreenActive)
            {
                return false;
            }

            int cols = buffer.Cols;
            if (cols <= 0)
            {
                return false;
            }

            int totalRows = buffer.InternalTotalLines;
            int cursorRow = buffer.Scrollback.Count + buffer.InternalCursorRow;
            if (cursorRow < 0 || cursorRow >= totalRows)
            {
                return false;
            }

            // Deferred autowrap: after a character lands on the last column the cursor stays
            // parked there with the wrap pending, so its *text* position is one past it.
            int cursorTextCol = buffer.IsPendingWrap
                ? Math.Min(buffer.InternalCursorCol + 1, cols)
                : buffer.InternalCursorCol;
            if (cursorTextCol < 0)
            {
                return false;
            }

            cursorTextCol = Math.Min(cursorTextCol, cols);

            if (maxChars == 0)
            {
                return true;
            }

            var rows = new List<string>();
            int collected = 0;
            int row = cursorRow;
            int rowsRead = 0;

            while (true)
            {
                int endCol = row == cursorRow
                    ? cursorTextCol
                    : SoftWrappedRowEnd(buffer, row, cols);

                string rowText = ReadRowText(buffer, row, endCol);
                rows.Add(rowText);
                collected += rowText.Length;
                rowsRead++;

                if (collected >= maxChars ||
                    row <= 0 ||
                    rowsRead >= MaxSpanRows ||
                    !buffer.IsRowWrappedAbsolute(row - 1))
                {
                    break;
                }

                row--;
            }

            rows.Reverse();
            string joined = string.Concat(rows);
            text = joined.Length > maxChars
                ? joined.Substring(joined.Length - maxChars)
                : joined;
            return true;
        }

        /// <summary>
        /// The text of one row's cells in <c>[0, endCol)</c>, with the same cell-to-character
        /// rules <see cref="TryReadCommandLineLocked"/> uses: wide continuations are skipped,
        /// and an unset cell reads as a space.
        /// </summary>
        private static string ReadRowText(TerminalBuffer buffer, int row, int endCol)
        {
            var text = new StringBuilder();
            for (int col = 0; col < endCol; col++)
            {
                if (buffer.GetCellAbsolute(col, row).IsWideContinuation)
                {
                    continue;
                }

                string grapheme = buffer.GetGraphemeAbsolute(col, row);
                text.Append(string.IsNullOrEmpty(grapheme) || grapheme == "\0" ? " " : grapheme);
            }

            return text.ToString();
        }

        /// <summary>
        /// Exclusive end column for a soft-wrapped row. Such a row is full of input by
        /// definition, except for the one-cell hole a double-width character leaves when it
        /// does not fit in the last column and wraps early.
        /// </summary>
        /// <remarks>
        /// The hole is not distinguishable from content. A typed space in the last column of a
        /// row whose next row begins with a wide character produces a byte-identical grid to the
        /// early-wrap hole, because nothing records <i>why</i> the cell is blank. The reader
        /// drops it either way, so that one case loses a typed space. The alternative — keeping
        /// it — inserts a phantom space into the middle of every command line that wraps before
        /// a CJK or emoji character, which is both far more common and harder for a consumer to
        /// undo.
        /// </remarks>
        private static int SoftWrappedRowEnd(TerminalBuffer buffer, int row, int cols)
        {
            if (cols >= 2 &&
                IsBlank(buffer.GetCellAbsolute(cols - 1, row)) &&
                buffer.GetCellAbsolute(0, row + 1).IsWide)
            {
                return cols - 1;
            }

            return cols;
        }

        /// <summary>
        /// Exclusive end column for a row that is not soft-wrapped: trailing blanks are
        /// dropped, but the cursor always stays inside the text (the user may have typed
        /// trailing spaces and parked the cursor after them), and a right-aligned prompt is
        /// excluded when <paramref name="cursorTextCol"/> is supplied.
        /// </summary>
        private static int ContentRowEnd(
            TerminalBuffer buffer,
            int row,
            int firstCol,
            int cols,
            int? cursorTextCol,
            ref bool rightPromptTrimmed)
        {
            int lastNonBlank = LastNonBlankColumn(buffer, row, firstCol, cols);
            int contentEnd = Math.Max(firstCol, lastNonBlank + 1);

            if (cursorTextCol is int cursorCol)
            {
                if (contentEnd > cursorCol && lastNonBlank >= cols - 1 - MaxRightPromptIndent)
                {
                    int gapStart = FindRightPromptGapStart(
                        buffer, row, Math.Max(firstCol, cursorCol), lastNonBlank, cols);
                    if (gapStart >= 0)
                    {
                        rightPromptTrimmed = true;
                        contentEnd = gapStart;
                    }
                }

                return Math.Max(cursorCol, Math.Max(contentEnd, firstCol));
            }

            return contentEnd;
        }

        /// <summary>
        /// Start column of the blank run that separates a right-aligned prompt from the rest of
        /// the row, or <c>-1</c> when the row does not look like one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The candidate is the <i>widest</i> blank run at or after <paramref name="floor"/> and
        /// left of <paramref name="lastNonBlank"/> — the row's dominant slack, which is what a
        /// right-aligned paint produces. Taking the rightmost qualifying run instead (the
        /// previous rule) cuts a multi-segment right prompt such as <c>12:34  ok</c> at its own
        /// internal gap, keeping the wide run and the left segment: worse than not trimming.
        /// Ties resolve to the leftmost run, which yields the larger badge and so the more
        /// conservative answer below.
        /// </para>
        /// <para>
        /// The run then has to look like a separator rather than a typo: at least
        /// <see cref="MinRightPromptGap"/> cells wide, strictly wider than the badge it
        /// separates, and that badge no wider than
        /// <paramref name="cols"/><c> / </c><see cref="MaxRightPromptWidthDivisor"/> columns.
        /// Failing any of these keeps the whole row — over-returning is recoverable, deleting
        /// typed input is not.
        /// </para>
        /// </remarks>
        private static int FindRightPromptGapStart(
            TerminalBuffer buffer, int row, int floor, int lastNonBlank, int cols)
        {
            int bestStart = -1;
            int bestEnd = -1;
            int bestWidth = 0;

            int col = lastNonBlank - 1;
            while (col >= floor)
            {
                if (!IsBlank(buffer.GetCellAbsolute(col, row)))
                {
                    col--;
                    continue;
                }

                int runStart = col;
                while (runStart - 1 >= floor && IsBlank(buffer.GetCellAbsolute(runStart - 1, row)))
                {
                    runStart--;
                }

                // Scanning right to left, ">=" keeps the leftmost of equally wide runs.
                int width = col - runStart + 1;
                if (width >= bestWidth)
                {
                    bestWidth = width;
                    bestStart = runStart;
                    bestEnd = col;
                }

                col = runStart - 1;
            }

            if (bestStart < 0 || bestWidth < MinRightPromptGap)
            {
                return -1;
            }

            int badgeWidth = lastNonBlank - bestEnd;
            if (badgeWidth >= bestWidth)
            {
                return -1; // the gap does not dominate: this is spacing inside typed input
            }

            if (badgeWidth > cols / MaxRightPromptWidthDivisor)
            {
                return -1; // too big to be a badge
            }

            return bestStart;
        }

        private static int LastNonBlankColumn(TerminalBuffer buffer, int row, int firstCol, int cols)
        {
            for (int col = cols - 1; col >= firstCol; col--)
            {
                if (!IsBlank(buffer.GetCellAbsolute(col, row)))
                {
                    return col;
                }
            }

            return firstCol - 1;
        }

        /// <summary>
        /// A cell holding nothing. The trailing half of a double-width character stores a space
        /// but is <i>not</i> blank — it sits inside content, and treating it as a gap would let
        /// two adjacent CJK characters look like a right-prompt separator.
        /// </summary>
        private static bool IsBlank(in TerminalCell cell)
            => !cell.IsWideContinuation
               && !cell.HasExtendedText
               && (cell.Character == ' ' || cell.Character == '\0');
    }
}
