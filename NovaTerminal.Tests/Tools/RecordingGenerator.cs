using System;
using System.IO;
using System.Text;
using NovaTerminal.Core.Replay;

namespace NovaTerminal.Tests.Tools
{
    public static class RecordingGenerator
    {
        public static void GenerateHelloWorld(string path)
        {
            // Ensure directory exists
            var dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            using var recorder = new PtyRecorder(path);

            // simulate "echo 'Hello' + newline"
            // H e l l o \r \n
            recorder.RecordChunk(Encoding.UTF8.GetBytes("Hello\r\n"), 7);

            // simulate red color "World"
            // \x1b[31m W o r l d \x1b[0m
            string redWorld = "\x1b[31mWorld\x1b[0m";
            recorder.RecordChunk(Encoding.UTF8.GetBytes(redWorld), redWorld.Length);
        }
    }
}
