//! Incremental Sixel graphics decoder, fed one DCS payload byte at a time.
//!
//! Sixel data packs six vertical pixels into each byte's low six bits;
//! `#`, `!`, `$`, and `-` are control bytes for color, repeat, and cursor
//! movement, so the first row can render before the rest of the image
//! has arrived.

const SIXEL_BIAS: u8 = 0x3F;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum DecoderState {
    /// Waiting for a sixel byte, a color introducer, or a cursor command.
    Ground,
    /// Consuming digits after `#`: selects the active color register.
    ColorParams,
    /// Consuming digits after `!`: the repeat count for the next sixel byte.
    RepeatCount,
}

pub struct SixelDecoder {
    state: DecoderState,
    cursor_x: usize,
    cursor_y: usize,
    repeat_count: u32,
    param: u32,
    current_register: u8,
}

impl SixelDecoder {
    pub fn new() -> Self {
        Self {
            state: DecoderState::Ground,
            cursor_x: 0,
            cursor_y: 0,
            repeat_count: 1,
            param: 0,
            current_register: 0,
        }
    }

    /// Advances the decoder by one byte of DCS payload.
    pub fn feed(&mut self, byte: u8) {
        use DecoderState::{ColorParams, Ground, RepeatCount};
        match (self.state, byte) {
            (_, b'#') => (self.param, self.state) = (0, ColorParams),
            (_, b'!') => (self.param, self.state) = (0, RepeatCount),
            (_, b'$') => (self.cursor_x, self.state) = (0, Ground),
            (_, b'-') => (self.cursor_x, self.cursor_y, self.state) = (0, self.cursor_y + 6, Ground),
            (ColorParams | RepeatCount, b'0'..=b'9') => {
                self.param = self.param * 10 + u32::from(byte - b'0');
            }
            (ColorParams, _) => {
                self.current_register = self.param as u8;
                self.state = Ground;
            }
            (RepeatCount, _) => {
                self.repeat_count = self.param.max(1);
                self.state = Ground;
            }
            (Ground, 0x3F..=0x7E) => self.paint_sixel(byte - SIXEL_BIAS),
            (Ground, _) => {}
        }
    }

    /// Paints one column of up to six pixels in the active color register.
    fn paint_sixel(&mut self, bits: u8) {
        for _ in 0..self.repeat_count {
            for row in 0..6u8 {
                if bits & (1 << row) != 0 {
                    // A real backend forwards this pixel to the framebuffer.
                    let _ = (self.cursor_x, self.cursor_y + row as usize, self.current_register);
                }
            }
            self.cursor_x += 1;
        }
        self.repeat_count = 1;
    }
}
