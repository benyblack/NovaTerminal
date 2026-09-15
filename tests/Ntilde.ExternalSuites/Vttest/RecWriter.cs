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
        private bool _hasWrittenV2Header;

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

        public void WriteHeader(int cols, int rows)
        {
            if (_hasWrittenV2Header)
            {
                throw new InvalidOperationException("Replay header has already been written.");
            }

            _writer.Write("{\"type\":\"novarec\",\"v\":2,\"cols\":");
            _writer.Write(cols);
            _writer.Write(",\"rows\":");
            _writer.Write(rows);
            _writer.Write(",\"date\":\"");
            _writer.Write(DateTime.UtcNow.ToString("O"));
            _writer.WriteLine("\",\"shell\":\"native-ssh\"}");
            _hasWrittenV2Header = true;
        }

        public void WriteDataEvent(ReadOnlySpan<byte> data)
        {
            if (!_hasWrittenV2Header)
            {
                throw new InvalidOperationException("WriteHeader must be called before writing v2 replay events.");
            }

            if (data.IsEmpty) return;

            long timestamp = _sw.ElapsedMilliseconds;
            string base64 = Convert.ToBase64String(data);
            _writer.Write("{\"t\":");
            _writer.Write(timestamp);
            _writer.Write(",\"type\":\"data\",\"d\":\"");
            _writer.Write(base64);
            _writer.WriteLine("\",\"cols\":null,\"rows\":null,\"n\":null,\"i\":null,\"s\":null}");
        }

        public void WriteResizeEvent(int cols, int rows)
        {
            if (!_hasWrittenV2Header)
            {
                throw new InvalidOperationException("WriteHeader must be called before writing v2 replay events.");
            }

            long timestamp = _sw.ElapsedMilliseconds;
            _writer.Write("{\"t\":");
            _writer.Write(timestamp);
            _writer.Write(",\"type\":\"resize\",\"d\":null,\"cols\":");
            _writer.Write(cols);
            _writer.Write(",\"rows\":");
            _writer.Write(rows);
            _writer.WriteLine(",\"n\":null,\"i\":null,\"s\":null}");
        }

        public async ValueTask DisposeAsync()
        {
            await _writer.FlushAsync();
            _writer.Dispose();
        }
    }
}
