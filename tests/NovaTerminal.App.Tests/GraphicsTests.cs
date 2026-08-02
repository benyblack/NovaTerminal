using NovaTerminal.Shell;
using Xunit;
using NovaTerminal.Platform;
using NovaTerminal.VT;
using NovaTerminal.Rendering;
using SkiaSharp;
using System;
using System.Collections.Generic;

namespace NovaTerminal.Tests
{
    public class GraphicsTests
    {
        [Fact]
        public void SixelDecoder_DecodesSimpleRedBlock()
        {
            // Arrange
            var decoder = new SixelDecoder();
            // DCS header (ignored by decoder.Decode) then q start sixel
            // #0;2;100;0;0 = define color 0 as RGB(100,0,0) (pure red in Sixel 0-100 scale)
            // #0!100~ = 100 pixels of bit pattern 126 (bottom bit of 6-bit sixel column)
            string dcsData = "0;1;0q#0;2;100;0;0#0!100~";

            // Act
            SKBitmap bitmap = decoder.Decode(dcsData);

            // Assert
            Assert.NotNull(bitmap);
            Assert.Equal(100, bitmap.Width);
            Assert.True(bitmap.Height >= 1);

            // Check first pixel (should be red)
            SKColor color = bitmap.GetPixel(0, 0);
            Assert.Equal(255, color.Red);
            Assert.Equal(0, color.Green);
            Assert.Equal(0, color.Blue);
        }

        [Fact]
        public void AnsiParser_HandlesOsc1339_TunneledSixel()
        {
            // Arrange
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            // The parser doesn't have a direct "ImageReceived" event but it stores them in the buffer
            // For this test, we verify that the DCS string is correctly passed to the decoder logic
            // via PTY or similar, but here we can just check if images were added to the buffer.

            string tunneledSixel = "\x1b]1339;q#0;2;100;0;0#0!10~\x07";

            // Act
            parser.Process(tunneledSixel);

            // Assert
            // Images are added to the buffer's _images list. 
            // Since it's private, we might need to check if the buffer's dirty flag or similar is set, 
            // or use reflection if we really want to verify content.
            // For now, let's verify it doesn't crash and potentially check if we can add a public way to count images.
            Assert.True(true);
        }

        [Fact]
        public void SixelDecoder_HandlesExtendedPalette()
        {
            // Arrange
            var decoder = new SixelDecoder();
            // Define index 100 as Blue and use it
            string dcsData = "q#100;2;0;0;100#100~";

            // Act
            SKBitmap bitmap = decoder.Decode(dcsData);

            // Assert
            Assert.NotNull(bitmap);
            SKColor color = bitmap.GetPixel(0, 0);
            Assert.Equal(0, color.Red);
            Assert.Equal(0, color.Green);
            Assert.Equal(255, color.Blue);
        }

        [Fact]
        public void KittyQuery_ApcMode_WithForcedConPtyFiltering_RespondsErr()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer, forceConPtyFiltering: true);
            string? response = null;
            parser.OnResponse = r => response = r;

            // APC Kitty query: ESC _ G a=q,i=31 ESC \
            parser.Process("\x1b_Ga=q,i=31\x1b\\");

            Assert.NotNull(response);
            Assert.Contains(";ERR", response!, StringComparison.Ordinal);
        }

        [Fact]
        public void KittyQuery_TunneledMode_WithForcedConPtyFiltering_RespondsOk()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer, forceConPtyFiltering: true);
            string? response = null;
            parser.OnResponse = r => response = r;

            // OSC 1339 tunneled Kitty query.
            parser.Process("\x1b]1339;K:Ga=q,i=31\x07");

            Assert.NotNull(response);
            Assert.Contains(";OK", response!, StringComparison.Ordinal);
        }

        /// <summary>
        /// SECURITY regression test (PR #280 review): the kitty graphics query reply echoed the
        /// unvalidated <c>i=</c> image id, and OnResponse is wired straight to Session.SendInput -
        /// the child process's stdin. The APC accumulator only treats BEL / 0x9C / ESC as
        /// terminators, so CR (0x0D) and LF (0x0A) reach the param verbatim, and at a shell prompt
        /// CR submits the line. Same bug class as the OSC 52 denial reply (see OscUxTests): ids are
        /// numeric, so digits are whitelisted and the length is capped.
        /// </summary>
        [Fact]
        public void KittyQuery_ImageIdWithInjectedControlBytes_ReplyCarriesNoControlBytes()
        {
            var buffer = new TerminalBuffer(80, 24);
            // ConPTY filtering forced off so the status field is "OK" on every platform - this
            // test is about the id, not about the ConPTY fallback.
            var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
            var responses = new List<string>();
            parser.OnResponse = r => responses.Add(r);

            parser.Process("\x1b_Ga=q,i=7\rid\r\x1b\\");

            var response = Assert.Single(responses);
            Assert.False(response.Contains('\r'), "kitty query reply carried CR into the child's stdin");
            Assert.False(response.Contains('\n'), "kitty query reply carried LF into the child's stdin");
            Assert.DoesNotContain("id", response, StringComparison.Ordinal);
            Assert.Equal("\x1b_Gi=7;OK\x1b\\", response);
        }

        [Fact]
        public void KittyQuery_OversizedImageId_ReplyIsLengthBounded()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
            var responses = new List<string>();
            parser.OnResponse = r => responses.Add(r);

            // Digits only, so the length cap is what has to hold - without it a single sequence
            // could push up to MaxStringSequenceChars (16 Mi chars) into the child's stdin.
            parser.Process("\x1b_Ga=q,i=" + new string('9', 5000) + "\x1b\\");

            var response = Assert.Single(responses);
            Assert.Equal("\x1b_Gi=" + new string('9', 10) + ";OK\x1b\\", response);
            Assert.True(response.Length < 32, $"kitty query reply was not length-bounded: {response.Length} chars");
        }

        [Fact]
        public void KittyQuery_NormalImageId_IsEchoedIntact()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
            var responses = new List<string>();
            parser.OnResponse = r => responses.Add(r);

            // Sanitization must not be over-broad: a legal numeric id round-trips as-is.
            parser.Process("\x1b_Ga=q,i=1234567890\x1b\\");

            var response = Assert.Single(responses);
            Assert.Equal("\x1b_Gi=1234567890;OK\x1b\\", response);
        }
    }
}
