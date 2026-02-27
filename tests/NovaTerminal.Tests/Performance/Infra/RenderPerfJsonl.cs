using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NovaTerminal.Core;
using Xunit.Sdk;

namespace NovaTerminal.Tests.Performance.Infra
{
    internal static class RenderPerfJsonl
    {
        public static List<RenderPerfMetrics> ReadAllFrames(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new XunitException($"Render metrics file was not found: '{path}'.");
            }

            var frames = new List<RenderPerfMetrics>();
            int lineNo = 0;
            foreach (string rawLine in File.ReadLines(path))
            {
                lineNo++;
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    continue;
                }

                try
                {
                    RenderPerfMetrics? parsed = JsonSerializer.Deserialize<RenderPerfMetrics>(rawLine);
                    if (!parsed.HasValue)
                    {
                        throw new XunitException($"Metrics JSONL line {lineNo} parsed to null.");
                    }

                    frames.Add(parsed.Value);
                }
                catch (Exception ex) when (ex is JsonException || ex is NotSupportedException)
                {
                    throw new XunitException($"Failed to parse metrics JSONL line {lineNo}: {ex.Message}");
                }
            }

            if (frames.Count == 0)
            {
                throw new XunitException($"Metrics JSONL had no frame records: '{path}'.");
            }

            return frames;
        }

        public static List<RenderPerfMetrics> SkipWarmup(IReadOnlyList<RenderPerfMetrics> frames, int warmupCount)
        {
            if (frames.Count <= warmupCount)
            {
                throw new XunitException($"Insufficient frames after warmup: total={frames.Count}, warmup={warmupCount}.");
            }

            return frames.Skip(warmupCount).ToList();
        }

        public static double Average(IReadOnlyList<RenderPerfMetrics> frames, Func<RenderPerfMetrics, double> selector)
        {
            if (frames.Count == 0)
            {
                throw new XunitException("Cannot compute average on an empty frame set.");
            }

            double sum = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                sum += selector(frames[i]);
            }

            return sum / frames.Count;
        }

        public static double Percentile(IReadOnlyList<RenderPerfMetrics> frames, Func<RenderPerfMetrics, double> selector, double p = 0.95)
        {
            if (frames.Count == 0)
            {
                throw new XunitException("Cannot compute percentile on an empty frame set.");
            }

            if (p <= 0) p = 0;
            if (p >= 1) p = 1;

            double[] sorted = frames.Select(selector).OrderBy(v => v).ToArray();
            int index = (int)Math.Ceiling(p * sorted.Length) - 1;
            index = Math.Clamp(index, 0, sorted.Length - 1);
            return sorted[index];
        }
    }
}
