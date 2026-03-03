using System.Collections.Generic;

namespace NovaTerminal.Core
{
    public interface IShellQuoter
    {
        string QuotePath(string path);
    }
}
