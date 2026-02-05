using System;
using System.IO;
using System.Text;

namespace NovaTerminal.Core.Replay
{
    public static class GoldenMaster
    {
        public static void AssertMatches(BufferSnapshot actual, string expectedSnapPath)
        {
            string actualText = actual.ToFormattedString();

            if (!File.Exists(expectedSnapPath))
            {
                // If fixture doesn't exist, fail with helpful message (or auto-generate if configured)
                // For now, fail.
                throw new FileNotFoundException($"Golden master snapshot not found: {expectedSnapPath}. Run with GEN_GOLDEN=1 to create it (manual implementation needed).");
            }

            string expectedText = File.ReadAllText(expectedSnapPath);

            // Normalize line endings
            actualText = Normalize(actualText);
            expectedText = Normalize(expectedText);

            if (actualText != expectedText)
            {
                // In a real runner, we'd output a diff. 
                // For now, simple exception.
                throw new Exception($"Snapshot mismatch! Expected content from {expectedSnapPath} does not match actual buffer state.");
            }
        }

        private static string Normalize(string input)
        {
            return input.Replace("\r\n", "\n").Trim();
        }
    }
}
