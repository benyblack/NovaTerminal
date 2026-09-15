using System.Collections.Generic;
using Ntilde.VT;

namespace Ntilde.Shell.ThemeImporters
{
    public interface IThemeImporter
    {
        string Name { get; }
        string Extension { get; }
        IEnumerable<TerminalTheme> Import(string filePath);
    }
}
