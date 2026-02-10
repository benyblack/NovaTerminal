import base64
import sys


def send_sixel(image_path, wrapped=False):
    # This is a very minimal Sixel-ish blob for testing if we can see ANY DCS sequences
    # A real sixel starts with \eP...q
    # We'll just send a small red block if we could, but let's just send some dummy Sixel data
    # \ePq#0;2;100;0;0#0!100~-
    # #0: color 0, 2: RGB, 100;0;0: Red
    # #0: use color 0
    # !100~: 100 sixels of pattern ~ (all 6 dots)
    # -: carriage return
    sixel_data = "\x1bPq#0;2;100;0;0#0!100~-\x1b\\"

    if wrapped:
        # Wrap in OSC 1339 as we did for Kitty
        print(f"\x1b]1339;{sixel_data}\x07", end="", flush=True)
    else:
        print(sixel_data, end="", flush=True)


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else "unwrapped"
    if mode == "wrapped":
        print("Sending WRAPPED Sixel...")
        send_sixel(None, True)
    else:
        print("Sending UNWRAPPED Sixel...")
        send_sixel(None, False)
