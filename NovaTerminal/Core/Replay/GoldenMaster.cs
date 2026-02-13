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
            bool updateGolden = IsUpdateGoldenEnabled();

            if (!File.Exists(expectedSnapPath))
            {
                if (updateGolden)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(expectedSnapPath)!);
                    File.WriteAllText(expectedSnapPath, actualText);
                    return;
                }

                throw new FileNotFoundException($"Golden master snapshot not found: {expectedSnapPath}. Set UPDATE_GOLDEN=1 to create it.");
            }

            string expectedText = File.ReadAllText(expectedSnapPath);

            // Normalize line endings
            actualText = Normalize(actualText);
            expectedText = Normalize(expectedText);

            if (actualText != expectedText)
            {
                if (updateGolden)
                {
                    File.WriteAllText(expectedSnapPath, actualText);
                    return;
                }

                throw new Exception($"Snapshot mismatch! Expected content from {expectedSnapPath} does not match actual buffer state.");
            }
        }

        private static string Normalize(string input)
        {
            return input.Replace("\r\n", "\n").Trim();
        }

        private static bool IsUpdateGoldenEnabled()
        {
            string? val = Environment.GetEnvironmentVariable("UPDATE_GOLDEN");
            return string.Equals(val, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
