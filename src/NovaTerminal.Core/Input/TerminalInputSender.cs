using System;

namespace NovaTerminal.Core.Input
{
    public static class TerminalInputSender
    {
        public static void SendBracketedPaste(ITerminalSession session, string content)
        {
            if (session == null || string.IsNullOrEmpty(content))
            {
                return;
            }

            // Standard VT sequence for starting a pasted block of text
            const string bracketedPasteStart = "\x1b[200~";
            // Standard VT sequence for ending a pasted block of text
            const string bracketedPasteEnd = "\x1b[201~";

            // If the content itself contains the end sequence, it could be a bracketed paste hijacking attack.
            // But for a simple terminal emulator where we are reading *local* files that the user dragged in,
            // the risk is lower (they chose to paste it).
            // A safest approach: remove or escape trailing the end sequence.
            string safeContent = content.Replace(bracketedPasteEnd, "");
            safeContent = safeContent.Replace("\r\n", "\r");
            session.SendInput(bracketedPasteStart + safeContent + bracketedPasteEnd);
        }
    }
}
