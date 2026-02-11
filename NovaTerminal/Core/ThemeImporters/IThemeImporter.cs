using System.Collections.Generic;

namespace NovaTerminal.Core.ThemeImporters
{
    public interface IThemeImporter
    {
        string Name { get; }
        string Extension { get; }
        IEnumerable<TerminalTheme> Import(string filePath);
    }
}
