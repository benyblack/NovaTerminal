using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace NovaTerminal.Core.Replay
{
    public class ReplayRunner
    {
        private readonly string _filePath;

        public ReplayRunner(string filePath)
        {
            _filePath = filePath;
        }

        public async Task RunAsync(Func<byte[], Task> onDataCallback, bool realtime = false)
        {
            if (!File.Exists(_filePath))
                throw new FileNotFoundException("Replay file not found", _filePath);

            using var reader = new StreamReader(_filePath);
            string? line;
            long lastOffset = 0;

            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Manual parsing of {"t":123,"d":"BASE64"}
                // Very naive parser for this specific format

                try
                {
                    int tIndex = line.IndexOf("\"t\":");
                    int dIndex = line.IndexOf("\"d\":");
                    if (tIndex == -1 || dIndex == -1) continue;

                    int tEnd = line.IndexOf(',', tIndex);
                    string tStr = line.Substring(tIndex + 4, tEnd - (tIndex + 4));
                    long timeMs = long.Parse(tStr);

                    int dStart = dIndex + 5; // "d":"
                    int dEnd = line.LastIndexOf('"');

                    // Basic bounds check
                    if (dEnd <= dStart) continue;

                    string dataBase64 = line.Substring(dStart, dEnd - dStart);

                    if (realtime)
                    {
                        long delay = timeMs - lastOffset;
                        if (delay > 0) await Task.Delay((int)delay);
                        lastOffset = timeMs;
                    }

                    byte[] data = Convert.FromBase64String(dataBase64);
                    await onDataCallback(data);
                }
                catch
                {
                    // Skip malformed lines
                }
            }
        }
    }
}
