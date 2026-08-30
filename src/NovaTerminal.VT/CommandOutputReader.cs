using System;
using System.Collections.Generic;
using System.Text;

namespace NovaTerminal.VT
{
    /// <summary>
    /// The caps a <see cref="CommandOutputReader"/> tail read walks under. Counted the same way
    /// the reader counts them: logical lines after soft-wrapped physical rows are joined,
    /// characters after that, physical rows as the independent backstop.
    /// </summary>
    /// <remarks>
    /// The reader's own defaults (<see cref="CommandOutputReader.MaxOutputLines"/> etc.) are sized
    /// for error recognition - the useful part of a huge output is at the end. Consumers that want
    /// a whole response rather than its tail (the Agent Output panel) pass a larger budget; the
    /// cap is still applied before the string leaves the reader, so a budget is a hard bound on
    /// both the work and the result.
    /// </remarks>
    /// <param name="MaxLines">Logical lines kept.</param>
    /// <param name="MaxChars">Character ceiling on the assembled tail.</param>
    /// <param name="MaxRows">Upper bound on physical rows walked.</param>
    public readonly record struct OutputTailBudget(int MaxLines, int MaxChars, int MaxRows)
    {
        public static readonly OutputTailBudget Default = new(
            CommandOutputReader.MaxOutputLines,
            CommandOutputReader.MaxOutputChars,
            CommandOutputReader.MaxOutputRows);
    }

    /// <summary>
    /// Reads the tail of a finished command's <i>output region</i> out of the grid: the rows
    /// between the <c>OSC 133;C</c> edge (the shell accepted the line and is about to run it) and
    /// the <c>OSC 133;D</c> edge (it finished).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is output, not stderr.</b> A terminal has one grid. stdout and stderr are interleaved
    /// on it by the time anything here can see them, and nothing in the byte stream distinguishes
    /// them - no escape sequence, no attribute, nothing. Every name in this file therefore says
    /// <c>OutputTail</c>. Consumers that want "the error message" are pattern-matching a tail that
    /// usually ends with one; they are not reading a separate stream, and code that pretends
    /// otherwise would be lying to its callers.
    /// </para>
    /// <para>
    /// <b>Why a tail.</b> A build that prints ten thousand lines and fails is the normal case, and
    /// the useful part is at the end. <see cref="MaxOutputLines"/> logical lines and
    /// <see cref="MaxOutputChars"/> characters bound both the work and the result: the walk starts
    /// at the last row and stops as soon as either budget is spent, so the cost of a capture is the
    /// same whether the command printed forty lines or forty million. That ordering matters - the
    /// cap is applied <i>before</i> the string leaves this class, so redaction downstream never
    /// sees more than 8 KB either.
    /// </para>
    /// <para>
    /// <b>Soft wraps are not line breaks.</b> Physical rows that the terminal wrapped are joined
    /// with no separator, because they are one logical line of output; <c>'\n'</c> is emitted only
    /// where the output really broke the line. A recogniser matching
    /// <c>"is not recognized as a name of a cmdlet"</c> would otherwise fail on any pane narrow
    /// enough to wrap that phrase, which is most of them. The line budget counts logical lines for
    /// the same reason.
    /// </para>
    /// <para>
    /// <b>Staleness, same contract as <see cref="GridQueryReader"/>.</b> The region start is a
    /// <see cref="ShellIntegrationMark"/> and obeys the two-case rule spelled out on that type: a
    /// <see cref="ShellIntegrationMark.Generation"/> mismatch is fatal (after a scrollback reset a
    /// stale <see cref="ShellIntegrationMark.AbsoluteRow"/> resolves to a perfectly plausible row
    /// holding unrelated content, and no arithmetic can catch it), and only within one generation
    /// does a negative derived row mean "aged out".
    /// </para>
    /// <para>
    /// <b>Eviction clamps rather than fails.</b> A long-running command whose output pushed the C
    /// edge out of scrollback leaves a negative derived start row. That is not a reason to return
    /// nothing: the request was for the <i>last</i> forty lines, and those are still present. The
    /// start is clamped to the oldest surviving row. Nothing wrong is returned - the rows read are
    /// still rows of this command's output - only fewer of them than the region nominally holds.
    /// The one thing that cannot be clamped is a generation mismatch, and that returns nothing.
    /// </para>
    /// <para>
    /// <b>Known limitation: a full-screen program.</b> If the command drove the alt screen and left
    /// it before <c>D</c> (<c>vim</c>, <c>less</c>, a TUI installer), the main screen the region
    /// resolves against holds the content that was restored underneath it, not the program's
    /// output. The rows are real rows of this pane's main screen and the generation is intact, so
    /// nothing here can tell the difference. The honest description of the result is "what the
    /// user can see between where the command started and where it ended", which is exactly what
    /// it is; a heuristic recogniser reading pre-command scrollback finds no error signature and
    /// says nothing, which is the safe outcome. An alt screen that is still <i>active</i> at
    /// <c>D</c> is rejected outright.
    /// </para>
    /// <para>
    /// <b>Lock discipline.</b> Both entry points take the buffer read lock unless the calling
    /// thread already holds a lock (<see cref="TerminalBuffer.Lock"/> is non-recursive). Both are
    /// called from the PTY parse thread, which is the point: at <c>D</c> the grid still holds the
    /// command's output, and by the time a UI-thread continuation runs the next prompt has been
    /// painted over it.
    /// </para>
    /// <para>
    /// Neither method throws. Every ambiguity resolves to <c>false</c> or to a shorter answer.
    /// </para>
    /// </remarks>
    public static class CommandOutputReader
    {
        /// <summary>
        /// Logical lines of output kept. Counted after soft-wrapped physical rows have been joined,
        /// so a single wrapped error message is one line however narrow the pane is.
        /// </summary>
        public const int MaxOutputLines = 40;

        /// <summary>
        /// Character ceiling on the captured tail. 8 KB is roughly 40 lines of a very wide pane; it
        /// is the binding constraint only when the output is unusually dense.
        /// </summary>
        public const int MaxOutputChars = 8 * 1024;

        /// <summary>
        /// Upper bound on physical rows walked, independent of the logical-line budget. A pane one
        /// column wide would otherwise turn 40 logical lines into an unbounded walk.
        /// </summary>
        public const int MaxOutputRows = 512;

        /// <summary>
        /// Marks where a command's output region begins, at the moment the shell accepts the line
        /// (<c>OSC 133;C</c>).
        /// </summary>
        /// <param name="buffer">The live buffer, read at <c>C</c> time.</param>
        /// <param name="commandLineMark">
        /// The <c>OSC 133;B</c> mark for the line being submitted, when there is one. It is used
        /// only to find the <i>last</i> physical row of the input line, which matters when the
        /// command line soft-wrapped: the cursor may be parked on the first of several rows.
        /// Pass <c>null</c> for a bare <c>C</c> (legal FinalTerm, and what several third-party
        /// integrations emit) and the cursor row is used instead.
        /// </param>
        /// <param name="outputStart">
        /// The first row of the output region, as an eviction-stable mark. Column is always 0:
        /// output starts at the left edge of the row after the input line.
        /// </param>
        /// <returns>
        /// <c>false</c> when there is no usable coordinate space - no buffer, an active alt screen,
        /// a cursor outside the buffer. Never throws.
        /// </returns>
        public static bool TryCaptureOutputStart(
            TerminalBuffer buffer,
            ShellIntegrationMark? commandLineMark,
            out ShellIntegrationMark outputStart)
        {
            outputStart = default;
            if (buffer is null)
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
                return TryCaptureOutputStartLocked(buffer, commandLineMark, out outputStart);
            }
            finally
            {
                if (lockTaken)
                {
                    buffer.Lock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Reads the tail of the output region that begins at <paramref name="outputStart"/> and
        /// ends at the cursor.
        /// </summary>
        /// <param name="buffer">The live buffer, read at <c>D</c> time.</param>
        /// <param name="outputStart">The mark produced by <see cref="TryCaptureOutputStart"/>.</param>
        /// <param name="outputTail">
        /// At most <see cref="MaxOutputLines"/> logical lines and <see cref="MaxOutputChars"/>
        /// characters, joined with <c>'\n'</c>, with trailing blank rows and per-row trailing
        /// blanks dropped. Empty when the command printed nothing, which is a real answer and not
        /// a failure.
        /// </param>
        /// <returns>
        /// <c>false</c> when the mark cannot be trusted (dead generation, alt screen, cursor above
        /// the region) or there is no buffer. Never throws.
        /// </returns>
        public static bool TryReadOutputTail(
            TerminalBuffer buffer,
            ShellIntegrationMark outputStart,
            out string outputTail)
        {
            return TryReadOutputTail(buffer, outputStart, OutputTailBudget.Default, out outputTail);
        }

        /// <summary>
        /// The same read as
        /// <see cref="TryReadOutputTail(TerminalBuffer, ShellIntegrationMark, out string)"/> under a
        /// caller-chosen <see cref="OutputTailBudget"/>.
        /// </summary>
        public static bool TryReadOutputTail(
            TerminalBuffer buffer,
            ShellIntegrationMark outputStart,
            in OutputTailBudget budget,
            out string outputTail)
        {
            outputTail = string.Empty;
            if (buffer is null)
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
                return TryReadOutputTailLocked(buffer, outputStart, budget, out outputTail);
            }
            finally
            {
                if (lockTaken)
                {
                    buffer.Lock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// The most recent output on the main screen, walked back from the cursor until the budget
        /// is spent - for a session with no <c>OSC 133;C</c> mark to bound the region.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the markless fallback.</b> Without shell integration there is no
        /// <see cref="ShellIntegrationMark"/> for <see cref="TryCaptureOutputStart"/> to anchor on,
        /// and the honest answer to "what did the last command print" is then "whatever rows are
        /// still on the grid above the cursor" - prompt lines included. That is a weaker contract
        /// than the marked read, and callers must present it as one: the result is a display tail,
        /// not a vouched-for output region.
        /// </para>
        /// <para>
        /// Everything else is the same walk: soft-wrapped rows joined, trailing blanks dropped, the
        /// budget applied before the string leaves this class.
        /// </para>
        /// </remarks>
        public static bool TryReadRecentTail(TerminalBuffer buffer, out string outputTail)
        {
            return TryReadRecentTail(buffer, OutputTailBudget.Default, out outputTail);
        }

        /// <summary>See <see cref="TryReadRecentTail(TerminalBuffer, out string)"/>.</summary>
        public static bool TryReadRecentTail(
            TerminalBuffer buffer,
            in OutputTailBudget budget,
            out string outputTail)
        {
            outputTail = string.Empty;
            if (buffer is null)
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
                return TryReadRecentTailLocked(buffer, budget, out outputTail);
            }
            finally
            {
                if (lockTaken)
                {
                    buffer.Lock.ExitReadLock();
                }
            }
        }

        private static bool TryCaptureOutputStartLocked(
            TerminalBuffer buffer,
            ShellIntegrationMark? commandLineMark,
            out ShellIntegrationMark outputStart)
        {
            outputStart = default;

            // A command accepted while a full-screen program owns the grid has no output region in
            // the scrollback sense: the alt screen has no shared row numbering and is discarded
            // wholesale on exit.
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

            // The last physical row of the submitted input line. The cursor alone is not it: at C
            // the user may have submitted from the middle of a line that wrapped over three rows,
            // and the two rows below the cursor are still input.
            int inputEndRow = cursorRow;
            if (commandLineMark is ShellIntegrationMark mark &&
                mark.Generation == buffer.Scrollback.Generation &&
                !mark.IsAltScreen &&
                GridQueryReader.TryReadCommandLine(buffer, mark, out GridCommandLine line) &&
                line.EndRow >= cursorRow &&
                line.EndRow < totalRows)
            {
                inputEndRow = line.EndRow;
            }

            long absoluteStart = buffer.Scrollback.TotalRowsEvicted + inputEndRow + 1;
            outputStart = new ShellIntegrationMark(
                Row: inputEndRow + 1,
                Column: 0,
                AbsoluteRow: absoluteStart,
                IsAltScreen: false,
                Generation: buffer.Scrollback.Generation);
            return true;
        }

        private static bool TryReadOutputTailLocked(
            TerminalBuffer buffer,
            ShellIntegrationMark outputStart,
            in OutputTailBudget budget,
            out string outputTail)
        {
            outputTail = string.Empty;

            // Coordinate-space epoch first, for the reason on ShellIntegrationMark: after a
            // scrollback reset the stale row is positive, plausible, and someone else's content.
            if (outputStart.Generation != buffer.Scrollback.Generation)
            {
                return false;
            }

            if (outputStart.IsAltScreen || buffer.IsAltScreenActive)
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

            long derivedStart = outputStart.AbsoluteRow - buffer.Scrollback.TotalRowsEvicted;

            // Evicted: the oldest surviving row is the best truthful start. See the class remarks -
            // this is a shorter answer, not a wrong one.
            int startRow = derivedStart < 0 ? 0 : (int)Math.Min(derivedStart, totalRows);
            if (startRow > cursorRow)
            {
                // The region has not been written to yet (a command that finished before printing
                // anything, with the prompt not yet repainted). Empty output, truthfully.
                return true;
            }

            return TryReadTailFromRowLocked(buffer, startRow, budget, out outputTail);
        }

        private static bool TryReadRecentTailLocked(
            TerminalBuffer buffer,
            in OutputTailBudget budget,
            out string outputTail)
        {
            outputTail = string.Empty;

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

            return TryReadTailFromRowLocked(buffer, 0, budget, out outputTail);
        }

        /// <summary>
        /// The shared bounded walk: from <paramref name="startRow"/> down to the cursor, newest
        /// first, stopping when any part of the budget is spent. Both readers above have already
        /// validated the coordinate space; this method only walks and assembles.
        /// </summary>
        private static bool TryReadTailFromRowLocked(
            TerminalBuffer buffer,
            int startRow,
            in OutputTailBudget budget,
            out string outputTail)
        {
            outputTail = string.Empty;

            int cols = buffer.Cols;
            int cursorRow = buffer.Scrollback.Count + buffer.InternalCursorRow;

            // Deferred autowrap: after a character lands on the last column the cursor stays parked
            // there with the wrap pending, so its *text* position is one past it.
            int cursorTextCol = buffer.IsPendingWrap
                ? Math.Min(buffer.InternalCursorCol + 1, cols)
                : buffer.InternalCursorCol;
            cursorTextCol = Math.Clamp(cursorTextCol, 0, cols);

            int endRow = cursorRow;

            // Trailing blank rows are slack, not output: at D the shell has emitted the final
            // newline and the cursor sits on a row the next prompt has not painted yet.
            while (endRow > startRow && IsRowBlank(buffer, endRow, endRow == cursorRow ? cursorTextCol : cols))
            {
                endRow--;
            }

            var rows = new List<PhysicalRow>();
            int logicalLines = 0;
            int collected = 0;

            for (int row = endRow; row >= startRow; row--)
            {
                bool wrapsIntoNext = row < endRow && buffer.IsRowWrappedAbsolute(row);
                int endCol = row == cursorRow
                    ? cursorTextCol
                    : wrapsIntoNext
                        ? SoftWrappedRowEnd(buffer, row, cols)
                        : LastNonBlankColumn(buffer, row, cols) + 1;

                // A row that soft-wrapped into the next one keeps its trailing cells: they are the
                // middle of a logical line, and trimming them would glue the two halves of a
                // wrapped word's neighbours together.
                string text = ReadRowText(buffer, row, endCol, trimTrailingBlanks: !wrapsIntoNext);
                rows.Add(new PhysicalRow(text, wrapsIntoNext));
                collected += text.Length;

                bool startsLogicalLine = row == startRow || row == 0 || !buffer.IsRowWrappedAbsolute(row - 1);
                if (startsLogicalLine)
                {
                    logicalLines++;
                }

                if (rows.Count >= budget.MaxRows)
                {
                    break;
                }

                if (startsLogicalLine && (logicalLines >= budget.MaxLines || collected >= budget.MaxChars))
                {
                    break;
                }
            }

            rows.Reverse();

            var builder = new StringBuilder(Math.Min(collected, budget.MaxChars) + rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                builder.Append(rows[i].Text);
                if (i < rows.Count - 1 && !rows[i].WrapsIntoNext)
                {
                    builder.Append('\n');
                }
            }

            string assembled = builder.ToString();
            if (assembled.Length > budget.MaxChars)
            {
                assembled = assembled[^budget.MaxChars..];
            }

            outputTail = assembled;
            return true;
        }

        private readonly record struct PhysicalRow(string Text, bool WrapsIntoNext);

        /// <summary>
        /// One row's cells in <c>[0, endCol)</c>, with the cell-to-character rules
        /// <see cref="GridQueryReader"/> uses: wide continuations skipped, unset cells read as a
        /// space. Trailing blanks are dropped when <paramref name="trimTrailingBlanks"/> says so,
        /// since a row of output that ends a logical line is not padded with meaning.
        /// </summary>
        private static string ReadRowText(TerminalBuffer buffer, int row, int endCol, bool trimTrailingBlanks)
        {
            var text = new StringBuilder(Math.Max(endCol, 0));
            for (int col = 0; col < endCol; col++)
            {
                if (buffer.GetCellAbsolute(col, row).IsWideContinuation)
                {
                    continue;
                }

                string grapheme = buffer.GetGraphemeAbsolute(col, row);
                text.Append(string.IsNullOrEmpty(grapheme) || grapheme == "\0" ? " " : grapheme);
            }

            if (trimTrailingBlanks)
            {
                int end = text.Length;
                while (end > 0 && text[end - 1] == ' ')
                {
                    end--;
                }

                text.Length = end;
            }

            return text.ToString();
        }

        /// <summary>
        /// Exclusive end column for a soft-wrapped row: the full width, minus the one-cell hole a
        /// double-width character leaves when it does not fit in the last column and wraps early.
        /// Mirrors <c>GridQueryReader.SoftWrappedRowEnd</c>.
        /// </summary>
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

        private static bool IsRowBlank(TerminalBuffer buffer, int row, int endCol)
        {
            for (int col = 0; col < endCol; col++)
            {
                if (!IsBlank(buffer.GetCellAbsolute(col, row)))
                {
                    return false;
                }
            }

            return true;
        }

        private static int LastNonBlankColumn(TerminalBuffer buffer, int row, int cols)
        {
            for (int col = cols - 1; col >= 0; col--)
            {
                if (!IsBlank(buffer.GetCellAbsolute(col, row)))
                {
                    return col;
                }
            }

            return -1;
        }

        /// <summary>
        /// A cell holding nothing. The trailing half of a double-width character stores a space but
        /// is not blank - it sits inside content.
        /// </summary>
        private static bool IsBlank(in TerminalCell cell)
            => !cell.IsWideContinuation
               && !cell.HasExtendedText
               && (cell.Character == ' ' || cell.Character == '\0');
    }
}
