using System;
using System.IO;

namespace NovaTerminal.Platform.Input
{
    /// <summary>
    /// Helpers for pasting an image that lives on the clipboard (e.g. a screenshot) into a
    /// terminal session: produce a temp file path to save the decoded image to, and format
    /// that path for injection. A running CLI such as Claude Code reads the path to attach
    /// the image. The actual decode/encode is done by the UI layer (Avalonia Bitmap), which
    /// normalizes any clipboard image encoding (PNG, CF_DIB, ...) to a single PNG file.
    /// </summary>
    public static class ClipboardImage
    {
        /// <summary>
        /// Returns a unique, not-yet-created path in the system temp directory for an image
        /// with the given extension (e.g. ".png").
        /// </summary>
        public static string GetTempImagePath(string extension)
        {
            string fileName = "nova-clip-" + Guid.NewGuid().ToString("N") + extension;
            return Path.Combine(Path.GetTempPath(), fileName);
        }

        /// <summary>
        /// Formats a path for injection into the session: a trailing space always, wrapped in
        /// double quotes only when the path contains whitespace (so paths with spaces survive).
        /// </summary>
        public static string QuotePathForInput(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            bool hasWhitespace = path.IndexOf(' ') >= 0 || path.IndexOf('\t') >= 0;
            return hasWhitespace ? "\"" + path + "\" " : path + " ";
        }
    }
}
