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

            using var recorder = new PtyRecorder(path, 80, 24);

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

            using var recorder = new PtyRecorder(path, 80, 24);

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

            using var recorder = new PtyRecorder(path, 80, 24);

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
        public static void GeneratePowerlinePrompt(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            using var recorder = new PtyRecorder(path, 80, 24);

            // \u1b[38;2;40;44;52m\u1b[48;2;152;195;121m \uf17c \u1b[38;2;152;195;121m\u1b[48;2;97;175;239m\ue0b0\u1b[38;2;40;44;52m \uf07c ~/nova \u1b[38;2;97;175;239m\u1b[49m\ue0b0\u1b[0m 
            // Simplified powerline-like sequence
            string prompt = "\x1b[42;30m \uf17c Linux \x1b[44;32m\ue0b0\x1b[30m ~/nova \x1b[0;34m\ue0b0\x1b[0m ";
            var data = Encoding.UTF8.GetBytes(prompt);
            recorder.RecordChunk(data, data.Length);
        }

        public static void GenerateMixedUnicode(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            using var recorder = new PtyRecorder(path, 80, 24);

            // ASCII + Emoji + CJK + Symbols
            string text = "Hello \U0001f600 (Smile) | \u4e2d\u6587 (Chinese) | \u2665 (Heart)\r\n";
            var data = Encoding.UTF8.GetBytes(text);
            recorder.RecordChunk(data, data.Length);

            // Emoji ZWJ sequences
            string zwj = "Family: \U0001f468\u200d\U0001f469\u200d\U0001f467\r\n";
            var zwjData = Encoding.UTF8.GetBytes(zwj);
            recorder.RecordChunk(zwjData, zwjData.Length);
        }

        public static void GenerateWrappedText(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            using var recorder = new PtyRecorder(path, 80, 24);

            // Long line with mixed content to test wrapping near emoji/cjk
            string longLine = "This is a very long line that should wrap eventually. " +
                              "Here is some Unicode: \u4e2d\u6587\u4e2d\u6587\u4e2d\u6587\u4e2d\u6587 " +
                              "and some Emojis: \U0001f600\U0001f601\U0001f602\U0001f603\U0001f604 " +
                              "to see how it wraps at the edge of the terminal buffer.\r\n";
            var data = Encoding.UTF8.GetBytes(longLine);
            recorder.RecordChunk(data, data.Length);
        }

        public static void GenerateMidnightCommander(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            using var recorder = new PtyRecorder(path, 80, 24);

            // 1. Enter Alt Screen
            string enterAlt = "\x1b[?1049h";
            recorder.RecordChunk(Encoding.UTF8.GetBytes(enterAlt), enterAlt.Length);

            // 2. Draw a box (top line, bottom line, sides)
            // Assuming 80x24
            string box = "\x1b[1;1H\x1b[44;37m" + new string('=', 80) +
                         "\x1b[24;1H" + new string('=', 80);
            for (int i = 2; i < 24; i++)
            {
                box += $"\x1b[{i};1H|\x1b[{i};80H|";
            }
            box += "\x1b[12;30H Midnight Commander ";
            box += "\x1b[0m";
            recorder.RecordChunk(Encoding.UTF8.GetBytes(box), box.Length);
        }

        public static void GenerateMidnightCommander_Resized_100x30(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            using var recorder = new PtyRecorder(path, 80, 24);

            // 1. Draw 100x30 box
            string box = "\x1b[1;1H\x1b[44;37m" + new string('=', 100) +
                         "\x1b[30;1H" + new string('=', 100);
            for (int i = 2; i < 30; i++)
            {
                box += $"\x1b[{i};1H|\x1b[{i};100H|";
            }
            box += "\x1b[15;40H Midnight Commander (100x30) "; // Centered roughly
            box += "\x1b[0m";
            recorder.RecordChunk(Encoding.UTF8.GetBytes(box), box.Length);
        }

        public static void GenerateMidnightCommander_Menu(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            using var recorder = new PtyRecorder(path, 80, 24);

            // 1. Draw base MC interface (Cyan background blue box)
            string baseUi = "\x1b[1;1H\x1b[44;37m" + new string(' ', 80 * 24) + "\x1b[1;1H";
            recorder.RecordChunk(Encoding.UTF8.GetBytes(baseUi), baseUi.Length);

            // 2. Simulate F9 (Menu) -> "Left" Menu Drops Down
            // Menu at (1,1) to (10,6), Grey background
            string menu = "\x1b[2;2H\x1b[47;30m Listing mode \x1b[3;2H Quick view   \x1b[4;2H Info         \x1b[5;2H Tree         \x1b[0m";
            recorder.RecordChunk(Encoding.UTF8.GetBytes(menu), menu.Length);
        }

        public static void GenerateMidnightCommander_Dialog(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            using var recorder = new PtyRecorder(path, 80, 24);

            // 1. Base UI
            string baseUi = "\x1b[1;1H\x1b[44;37m" + new string(' ', 80 * 24) + "\x1b[1;1H";
            recorder.RecordChunk(Encoding.UTF8.GetBytes(baseUi), baseUi.Length);

            // 2. Center Dialog " Delete "
            // Box at (10, 20) with red background
            string dialog = "\x1b[10;20H\x1b[41;37m      Delete      \x1b[11;20H  file.txt? [Y/n] \x1b[12;20H                  \x1b[0m";
            recorder.RecordChunk(Encoding.UTF8.GetBytes(dialog), dialog.Length);
        }

        public static void GenerateOhMyPosh(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            using var recorder = new PtyRecorder(path, 80, 24);

            // Prompt on left, Time on right (column 70)
            string prompt = "\x1b[1;1H\x1b[42;30m nova \x1b[44;32m\ue0b0\x1b[30m ~/projects \x1b[0;34m\ue0b0\x1b[0m ";
            string rightPart = "\x1b[1;70H\x1b[90m10:37:00\x1b[0m";
            string total = prompt + rightPart + "\x1b[1;20H"; // Cursor back to input area

            recorder.RecordChunk(Encoding.UTF8.GetBytes(total), total.Length);
        }
    }
}
