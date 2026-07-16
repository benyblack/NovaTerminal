using System.Collections.Generic;

namespace NovaTerminal.Platform
{
    public interface IShellQuoter
    {
        string QuotePath(string path);

        /// <summary>
        /// True when <paramref name="path"/> contains a metacharacter this shell would
        /// interpret even inside quotes, so it cannot be safely inserted as literal text.
        /// Callers should refuse the drop rather than emit something that expands (#170).
        /// Default: nothing is unsafe (POSIX single-quotes and PowerShell single-quotes
        /// fully neutralize their content).
        /// </summary>
        bool HasUnsafeMetacharacters(string path) => false;
    }
}
