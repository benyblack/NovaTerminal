using System.Collections.Generic;
using NovaTerminal.VT;

namespace NovaTerminal.Core.ThemeImporters
{
    public interface IThemeImporter
    {
        string Name { get; }
        string Extension { get; }
        IEnumerable<TerminalTheme> Import(string filePath);
    }
}
