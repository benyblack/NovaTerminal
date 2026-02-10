#!/usr/bin/env python3
"""
iterm2_img.py — minimal iTerm2 inline image (OSC 1337) emitter.

Usage:
  python iterm2_img.py path/to/image.png
  python iterm2_img.py path/to/image.png --name demo.png --width 40 --height auto
  python iterm2_img.py path/to/image.png --inline 1 --preserve-aspect 1
  python iterm2_img.py path/to/image.png --width 200px
  python iterm2_img.py path/to/image.png --width 40 --height 20

Notes:
- This emits iTerm2's OSC 1337;File=...;base64payload BEL
- Many non-iTerm2 terminals ignore it; WezTerm supports it on Windows.
- Width/height values: number, 'auto', '<n>px', or '<n>%'
"""

from __future__ import annotations

import argparse
import base64
import os
import sys
from typing import Optional

ESC = "\x1b"
BEL = "\x07"


def b64(data: bytes) -> str:
    return base64.b64encode(data).decode("ascii")


def sanitize_filename(name: str) -> str:
    # iTerm2 expects 'name' to be base64 of the filename (not the path).
    # We'll pass only the basename to avoid leaking paths.
    return os.path.basename(name)


def emit_iterm2_image(
    img_bytes: bytes,
    *,
    name: str,
    width: Optional[str] = None,
    height: Optional[str] = None,
    preserve_aspect: Optional[int] = None,
    inline: int = 1,
    do_not_move_cursor: Optional[int] = None,
    size: Optional[int] = None,
    attach_newline: bool = True,
) -> None:
    """
    Emit iTerm2 inline image escape sequence.

    iTerm2 format:
      OSC 1337 ; File=key=value;key=value : base64(data) BEL

    Common keys:
      name=<base64(filename)>
      size=<bytes>
      width=<n|auto|npx|n%>
      height=<n|auto|npx|n%>
      preserveAspectRatio=0|1
      inline=0|1
      doNotMoveCursor=0|1
    """

    params = []

    # Required-ish metadata
    params.append(f"name={b64(name.encode('utf-8'))}")

    if size is None:
        size = len(img_bytes)
    params.append(f"size={int(size)}")

    if width:
        params.append(f"width={width}")
    if height:
        params.append(f"height={height}")
    if preserve_aspect is not None:
        params.append(f"preserveAspectRatio={int(preserve_aspect)}")

    params.append(f"inline={int(inline)}")

    if do_not_move_cursor is not None:
        params.append(f"doNotMoveCursor={int(do_not_move_cursor)}")

    header = ";".join(params)
    payload = b64(img_bytes)

    seq = f"{ESC}]1337;File={header}:{payload}{BEL}"
    sys.stdout.write(seq)
    if attach_newline:
        sys.stdout.write("\n")
    sys.stdout.flush()


def main() -> int:
    ap = argparse.ArgumentParser(
        description="Emit iTerm2 inline image escape sequence (OSC 1337)."
    )
    ap.add_argument("image", help="Path to an image file (png/jpg/gif/etc).")
    ap.add_argument(
        "--name",
        default=None,
        help="Displayed filename metadata (default: basename of input).",
    )
    ap.add_argument(
        "--width", default=None, help="Width: number, auto, 200px, 50%%, etc."
    )
    ap.add_argument(
        "--height", default=None, help="Height: number, auto, 200px, 50%%, etc."
    )
    ap.add_argument(
        "--preserve-aspect",
        type=int,
        choices=[0, 1],
        default=1,
        help="preserveAspectRatio (default: 1).",
    )
    ap.add_argument(
        "--inline", type=int, choices=[0, 1], default=1, help="inline (default: 1)."
    )
    ap.add_argument(
        "--do-not-move-cursor",
        type=int,
        choices=[0, 1],
        default=None,
        help="doNotMoveCursor (optional).",
    )
    ap.add_argument(
        "--no-newline",
        action="store_true",
        help="Do not print trailing newline after the image.",
    )
    args = ap.parse_args()

    img_path = args.image
    try:
        with open(img_path, "rb") as f:
            data = f.read()
    except OSError as e:
        print(f"ERROR: cannot read file: {img_path}\n{e}", file=sys.stderr)
        return 2

    name = sanitize_filename(args.name if args.name else img_path)

    emit_iterm2_image(
        data,
        name=name,
        width=args.width,
        height=args.height,
        preserve_aspect=args.preserve_aspect,
        inline=args.inline,
        do_not_move_cursor=args.do_not_move_cursor,
        attach_newline=not args.no_newline,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
