using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using Avalonia.Media;

namespace NovaTerminal.Core
{
    public class SixelDecoder
    {
        private class SixelColor
        {
            public byte R, G, B;
            public SixelColor(byte r, byte g, byte b) { R = r; G = g; B = b; }
        }

        private readonly Dictionary<int, SixelColor> _palette = new();
        private int _currentColorIdx = 0;
        private int _cursorX = 0;
        private int _cursorY = 0;
        private int _maxWidth = 0;
        private int _maxHeight = 0;

        // Sixel data is represented as vertical bit-slices.
        // We use a dictionary or a list of bands to handle sparse/infinite vertically.
        // Each band is 6 pixels high.
        private readonly List<byte[]> _bands = new();

        public SixelDecoder()
        {
            // Default palette (simplified VT340 or similar)
            _palette[0] = new SixelColor(0, 0, 0);
            _palette[1] = new SixelColor(0, 0, 255);
            _palette[2] = new SixelColor(255, 0, 0);
            _palette[3] = new SixelColor(0, 255, 0);
            _palette[4] = new SixelColor(255, 0, 255);
            _palette[5] = new SixelColor(0, 255, 255);
            _palette[6] = new SixelColor(255, 255, 0);
            _palette[7] = new SixelColor(255, 255, 255);
        }

        public SKBitmap? Decode(string dcs)
        {
            // dcs is everything between 'q' and 'ST'
            int qIdx = dcs.IndexOf('q');
            if (qIdx == -1) return null;

            string header = dcs.Substring(0, qIdx);
            string data = dcs.Substring(qIdx + 1);

            // Parse header: Ps ; Pi ; Pj
            // Ps: Pixel Aspect Ratio (default 2)
            // Pi: Background Select (0=remain, 1=flush)
            // Pj: (don't care for now)

            _cursorX = 0;
            _cursorY = 0;
            _maxWidth = 0;
            _maxHeight = 0;
            _bands.Clear();
            _placements.Clear();

            int i = 0;
            while (i < data.Length)
            {
                char c = data[i];

                if (c == '#') // Color palette
                {
                    i++;
                    int start = i;
                    while (i < data.Length && (char.IsDigit(data[i]) || data[i] == ';')) i++;
                    string[] parts = data.Substring(start, i - start).Split(';');
                    if (parts.Length > 0 && int.TryParse(parts[0], out int idx))
                    {
                        if (parts.Length == 5) // Set color: idx; type; p1; p2; p3
                        {
                            int type = int.Parse(parts[1]);
                            int p1 = int.Parse(parts[2]);
                            int p2 = int.Parse(parts[3]);
                            int p3 = int.Parse(parts[4]);

                            if (type == 2) // RGB 0-100
                            {
                                _palette[idx] = new SixelColor(
                                    (byte)(p1 * 255 / 100),
                                    (byte)(p2 * 255 / 100),
                                    (byte)(p3 * 255 / 100));
                            }
                            else if (type == 1) // HLS (simplified conversion)
                            {
                                // TODO: Full HLS to RGB conversion if needed
                                _palette[idx] = new SixelColor(200, 200, 200);
                            }
                        }
                        else
                        {
                            _currentColorIdx = idx;
                        }
                    }
                    continue; // i already advanced
                }
                else if (c == '!') // Repeat
                {
                    i++;
                    int start = i;
                    while (i < data.Length && char.IsDigit(data[i])) i++;
                    if (int.TryParse(data.Substring(start, i - start), out int count))
                    {
                        if (i < data.Length)
                        {
                            char target = data[i++];
                            for (int r = 0; r < count; r++) ProcessSixel(target);
                        }
                    }
                    continue;
                }
                else if (c == '$') // CR
                {
                    _cursorX = 0;
                    i++;
                }
                else if (c == '-') // LF
                {
                    _cursorX = 0;
                    _cursorY += 6;
                    i++;
                }
                else if (c == '"') // Grid size (skip)
                {
                    i++;
                    while (i < data.Length && (char.IsDigit(data[i]) || data[i] == ';')) i++;
                }
                else if (c >= '?' && c <= '~') // Sixel data
                {
                    ProcessSixel(c);
                    i++;
                }
                else
                {
                    i++; // Skip unknown
                }
            }

            if (_maxWidth == 0 || _maxHeight == 0) return null;

            // Render bit-planes to bitmap
            var bitmap = new SKBitmap(_maxWidth, _maxHeight);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
            }

            // This is a very simplified Sixel renderer. 
            // Proper Sixel requires bit-plane layering per color.
            // Our current 'bands' structure is too simple for multi-color overlays.
            // For now, let's just return a placeholder or implement a proper pixel buffer.

            return RenderToBitmap();
        }

        private void ProcessSixel(char c)
        {
            int sixel = c - 63;
            // In a better implementation, we'd store (x, y, color, sixel_bits)
            // But Sixel is often used per-color: #0 ... data ... #1 ... data (overlaid)
            // So we need a 2D buffer of pixels or a list of drawn sixels.

            // For MVP, keep track of pixels
            RecordPixels(_cursorX, _cursorY, _currentColorIdx, sixel);

            _cursorX++;
            if (_cursorX > _maxWidth) _maxWidth = _cursorX;
            if (_cursorY + 6 > _maxHeight) _maxHeight = _cursorY + 6;
        }

        private struct SixelPlacement
        {
            public int X, Y, ColorIdx;
            public byte Bits;
        }
        private readonly List<SixelPlacement> _placements = new();

        private void RecordPixels(int x, int y, int colorIdx, int bits)
        {
            _placements.Add(new SixelPlacement { X = x, Y = y, ColorIdx = colorIdx, Bits = (byte)bits });
        }

        private SKBitmap? RenderToBitmap()
        {
            if (_maxWidth <= 0 || _maxHeight <= 0) return null;

            var bitmap = new SKBitmap(_maxWidth, _maxHeight);

            // Clear bitmap
            for (int y = 0; y < _maxHeight; y++)
                for (int x = 0; x < _maxWidth; x++)
                    bitmap.SetPixel(x, y, SKColors.Transparent);

            foreach (var p in _placements)
            {
                if (!_palette.TryGetValue(p.ColorIdx, out var color)) continue;
                var skColor = new SKColor(color.R, color.G, color.B);

                for (int b = 0; b < 6; b++)
                {
                    if (((p.Bits >> b) & 1) != 0)
                    {
                        int py = p.Y + b;
                        if (py < _maxHeight)
                        {
                            bitmap.SetPixel(p.X, py, skColor);
                        }
                    }
                }
            }

            return bitmap;
        }
    }
}
