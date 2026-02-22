using System;
using System.IO;
using NovaTerminal.Core;

namespace NovaTerminal.Tests.Infra
{
    internal static class MetricsArtifactWriter
    {
        private static readonly object _gate = new();

        internal static void WriteRendererStatisticsSnapshot(string scenario)
        {
            if (!ShouldWrite()) return;

            string? root = Environment.GetEnvironmentVariable("METRICS_ARTIFACT_DIR");
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.Combine(AppContext.BaseDirectory, "metrics");
            }

            string safeScenario = MakeSafeFilePart(scenario);
            string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff");
            string path = Path.Combine(root, $"{safeScenario}_{stamp}.txt");

            lock (_gate)
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(path, RendererStatistics.GetReport());
            }
        }

        private static bool ShouldWrite()
        {
            string? value = Environment.GetEnvironmentVariable("WRITE_METRICS_ARTIFACTS");
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static string MakeSafeFilePart(string value)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(c, '_');
            }

            return string.IsNullOrWhiteSpace(value) ? "metrics" : value;
        }
    }
}
