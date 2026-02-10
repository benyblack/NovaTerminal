import os
import sys
import time


def ping_da():
    print("Pinging for Device Attributes (DA1)...")
    # Using byte literal to ensure no encoding issues
    sys.stdout.buffer.write(b"\x1b[c")
    sys.stdout.buffer.flush()
    time.sleep(1.0)


def send_sixel(mode="raw"):
    # A small red block in Sixel
    # q = start sixel
    # #0;2;100;0;0 = define color 0 as RGB 100,0,0
    # #0 = use color 0
    # !100~ = 100 pixels of bit pattern 126
    # - = newline
    sixel_data = b"q#0;2;100;0;0#0!100~-"

    # Wrap in DCS for standard VT
    dcs_sixel = b"\x1bP" + sixel_data + b"\x1b\\"

    if mode == "wrapped":
        print("Sending TUNNELED Sixel (OSC 1339)...")
        sys.stdout.buffer.write(b"\x1b]1339;" + sixel_data + b"\x07")
    elif mode == "da":
        ping_da()
    elif mode == "passthrough":
        print("Manually enabling ConPTY Passthrough and sending Sixel...")
        sys.stdout.buffer.write(b"\x1b[?9001h")
        sys.stdout.buffer.flush()
        time.sleep(0.1)
        sys.stdout.buffer.write(dcs_sixel)
    elif mode == "raw":
        print("Sending RAW Sixel (DCS wrapped)...")
        sys.stdout.buffer.write(dcs_sixel)
    else:
        print(f"Unknown mode: {mode}")
        print("Available modes: raw, wrapped, da, passthrough")

    sys.stdout.buffer.write(b"\n")
    sys.stdout.buffer.flush()


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else "raw"
    send_sixel(mode)
