using System.Collections.Generic;

namespace NovaTerminal.Platform
{
    public interface IShellQuoter
    {
        string QuotePath(string path);
    }
}
