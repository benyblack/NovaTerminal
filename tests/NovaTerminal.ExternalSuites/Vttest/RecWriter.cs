using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace NovaTerminal.ExternalSuites.Vttest
{
    public sealed class RecWriter : IAsyncDisposable
    {
        private readonly StreamWriter _writer;
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        public RecWriter(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            _writer = new StreamWriter(filePath, append: false, new UTF8Encoding(false))
            {
                NewLine = "\r\n",
                AutoFlush = false // flush on Dispose; switch to true if you prefer safety over speed
            };
        }

        public void WriteData(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty) return;

            long timestamp = _sw.ElapsedMilliseconds;
            string base64 = Convert.ToBase64String(data);

            // manual JSON to avoid escaping overhead
            _writer.Write("{\"t\":");
            _writer.Write(timestamp);
            _writer.Write(",\"d\":\"");
            _writer.Write(base64);
            _writer.WriteLine("\"}");
        }

        public async ValueTask DisposeAsync()
        {
            await _writer.FlushAsync();
            _writer.Dispose();
        }
    }
}
