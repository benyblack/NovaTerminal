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

        public static void GenerateVimExit(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            using var recorder = new PtyRecorder(path);

            // 1. Initial State: "Before Vim"
            string initial = "Before Vim\r\n";
            recorder.RecordChunk(Encoding.UTF8.GetBytes(initial), initial.Length);

            // 2. Enter Alt Screen (Xterm 1049) -> Saves cursor + Switches to Alt + Clears
            string enterAlt = "\x1b[?1049h";
            recorder.RecordChunk(Encoding.UTF8.GetBytes(enterAlt), enterAlt.Length);

            // 3. Inside Vim: Write "Inside Vim" at 5,5
            string inside = "\x1b[5;5HInside Vim";
            recorder.RecordChunk(Encoding.UTF8.GetBytes(inside), inside.Length);

            // 4. Exit Alt Screen (Xterm 1049) -> Restores cursor + Switches to Main
            string exitAlt = "\u001b[?1049l";
            recorder.RecordChunk(Encoding.UTF8.GetBytes(exitAlt), exitAlt.Length);
        }

        public static void GenerateAltScreenCursor(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            using var recorder = new PtyRecorder(path);

            // 1. Move to (10, 10) [1-based: 11, 11] and Save Cursor (Main)
            // CSI 11 ; 11 H
            string setMain = "\u001b[11;11H";
            recorder.RecordChunk(Encoding.UTF8.GetBytes(setMain), setMain.Length);

            string saveMain = "\u001b7"; // DECSC (Save Cursor)
            recorder.RecordChunk(Encoding.UTF8.GetBytes(saveMain), saveMain.Length);

            // 2. Enter Alt Screen
            string enterAlt = "\u001b[?1049h";
            recorder.RecordChunk(Encoding.UTF8.GetBytes(enterAlt), enterAlt.Length);

            // 3. Move to (5, 5) [1-based: 6, 6] and Save Cursor (Alt)
            // Note: If logic is buggy, this overwrites Main's saved cursor
            string setAlt = "\u001b[6;6H";
            recorder.RecordChunk(Encoding.UTF8.GetBytes(setAlt), setAlt.Length);

            string saveAlt = "\u001b7";
            recorder.RecordChunk(Encoding.UTF8.GetBytes(saveAlt), saveAlt.Length);

            // 4. Move random place (0,0)
            string moveRandom = "\u001b[1;1H";
            recorder.RecordChunk(Encoding.UTF8.GetBytes(moveRandom), moveRandom.Length);

            // 5. Restore Cursor (Alt) -> Should go to (5, 5)
            string restoreAlt = "\u001b8";
            recorder.RecordChunk(Encoding.UTF8.GetBytes(restoreAlt), restoreAlt.Length);

            // 6. Exit Alt Screen
            string exitAlt = "\u001b[?1049l";
            recorder.RecordChunk(Encoding.UTF8.GetBytes(exitAlt), exitAlt.Length);

            // 7. Restore Cursor (Main) -> Should go to (10, 10)
            // If buggy, it might go to (5, 5)
            string restoreMain = "\u001b8";
            recorder.RecordChunk(Encoding.UTF8.GetBytes(restoreMain), restoreMain.Length);
        }
    }
}
