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

            const string bracketedPasteStart = "\x1b[200~";
            const string bracketedPasteEnd = "\x1b[201~";
            string safeContent = content.Replace(bracketedPasteEnd, "");
            session.SendInput(bracketedPasteStart + safeContent + bracketedPasteEnd);
        }
    }
}
