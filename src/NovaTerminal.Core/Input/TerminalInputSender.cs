using System;

namespace NovaTerminal.Core.Input
{
    public static class TerminalInputSender
    {
        private const string BracketedPasteStart = "\x1b[200~";
        private const string BracketedPasteEnd = "\x1b[201~";

        public static string PreparePaste(string content, bool bracketedPasteModeEnabled)
        {
            if (string.IsNullOrEmpty(content))
            {
                return string.Empty;
            }

            string normalizedContent = content.Replace("\r\n", "\r");
            if (!bracketedPasteModeEnabled)
            {
                return normalizedContent;
            }

            // If the content itself contains the end sequence, it could be a bracketed paste hijacking attack.
            // A conservative approach is to strip the embedded terminator before wrapping the paste block.
            string safeContent = normalizedContent.Replace(BracketedPasteEnd, "");
            return BracketedPasteStart + safeContent + BracketedPasteEnd;
        }

        public static void SendBracketedPaste(ITerminalSession session, string content)
        {
            if (session == null || string.IsNullOrEmpty(content))
            {
                return;
            }

            session.SendInput(PreparePaste(content, bracketedPasteModeEnabled: true));
        }
    }
}
