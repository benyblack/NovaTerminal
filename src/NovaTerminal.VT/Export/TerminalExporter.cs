using System;
using System.Text;
using System.Collections.Generic;

namespace NovaTerminal.Core.Export
{
    public static class TerminalExporter
    {
        public static string ExportToPlainText(TerminalBuffer buffer)
        {
            buffer.Lock.EnterReadLock();
            try
            {
                var sb = new StringBuilder();
                for (int r = 0; r < buffer.Rows; r++)
                {
                    int lastNonEmpty = -1;
                    var rowSb = new StringBuilder();
                    for (int c = 0; c < buffer.Cols; c++)
                    {
                        var cell = buffer.GetCell(c, r);
                        if (cell.IsWideContinuation) continue;
                        
                        string text = buffer.GetGrapheme(c, r);
                        rowSb.Append(text);
                        if (!string.IsNullOrWhiteSpace(text) && text != "\0")
                        {
                            lastNonEmpty = rowSb.Length;
                        }
                    }
                    
                    if (lastNonEmpty >= 0)
                    {
                        sb.AppendLine(rowSb.ToString().Substring(0, lastNonEmpty));
                    }
                    else
                    {
                        sb.AppendLine();
                    }
                }
                
                return sb.ToString().TrimEnd();
            }
            finally
            {
                buffer.Lock.ExitReadLock();
            }
        }

        public static string ExportToAnsi(TerminalBuffer buffer)
        {
            buffer.Lock.EnterReadLock();
            try
            {
                var sb = new StringBuilder();
                TerminalCell lastCell = TerminalCell.Default;
                bool isFirstCell = true;

                for (int r = 0; r < buffer.Rows; r++)
                {
                    // Find logical end of line (to avoid writing trailing background colors)
                    int eol = buffer.Cols - 1;
                    while (eol >= 0)
                    {
                        var cell = buffer.GetCell(eol, r);
                        if (cell.Character != ' ' || !cell.IsDefaultBackground)
                        {
                            break;
                        }
                        eol--;
                    }

                    for (int c = 0; c <= eol; c++)
                    {
                        var cell = buffer.GetCell(c, r);
                        if (cell.IsWideContinuation) continue;

                        if (isFirstCell || StateChanged(lastCell, cell))
                        {
                            AppendSgrSequence(sb, cell, isFirstCell ? TerminalCell.Default : lastCell);
                            lastCell = cell;
                            isFirstCell = false;
                        }

                        sb.Append(buffer.GetGrapheme(c, r));
                    }
                    
                    // Reset at end of line if needed
                    if (!isFirstCell && (!lastCell.IsDefaultBackground || !lastCell.IsDefaultForeground))
                    {
                        sb.Append("\x1b[0m");
                        lastCell = TerminalCell.Default;
                    }
                    
                    sb.AppendLine();
                }
                return sb.ToString().TrimEnd();
            }
            finally
            {
                buffer.Lock.ExitReadLock();
            }
        }

        private static bool StateChanged(TerminalCell a, TerminalCell b)
        {
            if (a.IsBold != b.IsBold ||
                a.IsItalic != b.IsItalic ||
                a.IsUnderline != b.IsUnderline ||
                a.IsInverse != b.IsInverse ||
                a.IsStrikethrough != b.IsStrikethrough ||
                a.IsFaint != b.IsFaint ||
                a.IsHidden != b.IsHidden ||
                a.IsBlink != b.IsBlink)
                return true;

            if (a.IsDefaultForeground != b.IsDefaultForeground) return true;
            if (!b.IsDefaultForeground && a.Fg != b.Fg) return true;

            if (a.IsDefaultBackground != b.IsDefaultBackground) return true;
            if (!b.IsDefaultBackground && a.Bg != b.Bg) return true;

            return false;
        }

        private static void AppendSgrSequence(StringBuilder sb, TerminalCell cell, TerminalCell lastCell)
        {
            var codes = new List<int>();

            // Always reset if we're turning off an attribute, it's safer
            bool needsReset = (lastCell.IsBold && !cell.IsBold) ||
                              (lastCell.IsItalic && !cell.IsItalic) ||
                              (lastCell.IsUnderline && !cell.IsUnderline) ||
                              (lastCell.IsInverse && !cell.IsInverse) ||
                              (lastCell.IsStrikethrough && !cell.IsStrikethrough) ||
                              (lastCell.IsFaint && !cell.IsFaint) ||
                              (lastCell.IsHidden && !cell.IsHidden) ||
                              (lastCell.IsBlink && !cell.IsBlink) ||
                              (!lastCell.IsDefaultForeground && cell.IsDefaultForeground) ||
                              (!lastCell.IsDefaultBackground && cell.IsDefaultBackground);

            if (needsReset)
            {
                codes.Add(0); // Reset
                // Re-apply attributes that are active
                if (cell.IsBold) codes.Add(1);
                if (cell.IsFaint) codes.Add(2);
                if (cell.IsItalic) codes.Add(3);
                if (cell.IsUnderline) codes.Add(4);
                if (cell.IsBlink) codes.Add(5);
                if (cell.IsInverse) codes.Add(7);
                if (cell.IsHidden) codes.Add(8);
                if (cell.IsStrikethrough) codes.Add(9);
                
                // Colors will be added below
            }
            else
            {
                // Only add deltas
                if (!lastCell.IsBold && cell.IsBold) codes.Add(1);
                if (!lastCell.IsFaint && cell.IsFaint) codes.Add(2);
                if (!lastCell.IsItalic && cell.IsItalic) codes.Add(3);
                if (!lastCell.IsUnderline && cell.IsUnderline) codes.Add(4);
                if (!lastCell.IsBlink && cell.IsBlink) codes.Add(5);
                if (!lastCell.IsInverse && cell.IsInverse) codes.Add(7);
                if (!lastCell.IsHidden && cell.IsHidden) codes.Add(8);
                if (!lastCell.IsStrikethrough && cell.IsStrikethrough) codes.Add(9);
            }

            bool fgChanged = needsReset ? !cell.IsDefaultForeground : (!cell.IsDefaultForeground && (lastCell.IsDefaultForeground || lastCell.Fg != cell.Fg));
            if (fgChanged)
            {
                if (cell.IsPaletteForeground)
                {
                    int idx = (int)cell.Fg;
                    if (idx < 8) codes.Add(30 + idx);
                    else if (idx < 16) codes.Add(90 + (idx - 8));
                    else { codes.Add(38); codes.Add(5); codes.Add(idx); }
                }
                else
                {
                    var color = TermColor.FromUint(cell.Fg);
                    codes.Add(38); codes.Add(2); codes.Add(color.R); codes.Add(color.G); codes.Add(color.B);
                }
            }

            bool bgChanged = needsReset ? !cell.IsDefaultBackground : (!cell.IsDefaultBackground && (lastCell.IsDefaultBackground || lastCell.Bg != cell.Bg));
            if (bgChanged)
            {
                if (cell.IsPaletteBackground)
                {
                    int idx = (int)cell.Bg;
                    if (idx < 8) codes.Add(40 + idx);
                    else if (idx < 16) codes.Add(100 + (idx - 8));
                    else { codes.Add(48); codes.Add(5); codes.Add(idx); }
                }
                else
                {
                    var color = TermColor.FromUint(cell.Bg);
                    codes.Add(48); codes.Add(2); codes.Add(color.R); codes.Add(color.G); codes.Add(color.B);
                }
            }

            if (codes.Count > 0)
            {
                sb.Append("\x1b[");
                sb.Append(string.Join(";", codes));
                sb.Append('m');
            }
        }
    }
}
