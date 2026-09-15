using System;
using System.IO;

namespace Ntilde.Shell
{
    /// <summary>
    /// File naming for exported workspace bundles. Bundles are JSON, so the "extension" is a
    /// two-part suffix; the pre-rebrand suffix is still recognised on import so existing
    /// exports keep opening with a sensible suggested name.
    /// </summary>
    public static class WorkspaceBundleNaming
    {
        public const string Extension = ".ntildews.json";
        public const string LegacyExtension = ".novaws.json";

        /// <summary>File-picker patterns, new suffix first so it is the default filter.</summary>
        public static readonly string[] PickerPatterns = { "*" + Extension, "*" + LegacyExtension, "*.json" };

        public static string SuggestedFileName(string workspaceName) => $"{workspaceName.Trim()}{Extension}";

        /// <summary>Workspace name to propose for a bundle picked from disk.</summary>
        public static string SuggestedWorkspaceName(string bundlePath)
        {
            string name = Path.GetFileNameWithoutExtension(bundlePath); // drops ".json"
            foreach (string marker in new[] { ".ntildews", ".novaws" })
            {
                if (name.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return name[..^marker.Length];
                }
            }

            return name;
        }
    }
}
