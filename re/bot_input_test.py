#!/usr/bin/env python
"""
bot_input_test.py -- PROVE we can drive the live NexusTK client with injected input.

This is the single de-risking step for the whole auto-grinder: if we can't make the
character move/attack by injecting keystrokes, the "input injection" approach is dead
and we fall back to calling the client's own handlers via frida. So test this FIRST.

It does NOT need frida. It:
  1. Finds the NexusTK.exe top-level window (by walking every window and matching the
     owning process image name -- robust against title/localization changes).
  2. Sends a scripted burst of movement keys two ways:
       * PostMessage  (WM_KEYDOWN/WM_KEYUP) -- works even if the window is NOT focused,
         which is what we want for an unattended background grinder.
       * SendInput    (hardware-level) -- requires the window FOREGROUNDED, but some
         games only read input this way (GetAsyncKeyState/DirectInput ignore PostMessage).
     We try PostMessage first; if you see no movement, run with --sendinput.

HOW TO USE (with the client logged in and standing in an open area):
    # window 1: keep the tap running so you can SEE the wire react
    python re/nexus_agent.py
    # window 2: fire the test -- watch both the character and the tap's raw_packets
    python re/bot_input_test.py                 # PostMessage mode (background-safe)
    python re/bot_input_test.py --sendinput     # SendInput mode (foreground)
    python re/bot_input_test.py --attack        # also test the spacebar swing
    python re/bot_input_test.py --key up --hold 0.15 --repeat 8   # custom probe

WHAT TO WATCH FOR:
    * The character visibly steps / turns / swings.
    * In re/auto/raw_packets.jsonl, new 0x04 or 0x0c frames appear timed with the keys
      (self-movement echo) -- that is the server acknowledging OUR injected walk, and it
      simultaneously reveals our own entity id for the world model.
Report back which mode moved the character; that decides the controller design.
"""
import sys, os, time, json, ctypes
from ctypes import wintypes

RAW = os.path.join(os.path.dirname(os.path.abspath(__file__)), "auto", "raw_packets.jsonl")
# self-movement / turn echoes the server sends back when WE walk -- objective proof the
# injected key was accepted, even without watching the screen. (0x04 self-pace, 0x0c walk,
# 0x11 turn.) Needs nexus_agent.py running alongside so the frames are being logged.
SELF_ECHO_OPS = {0x04, 0x0c, 0x11}

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

# ---- Win32 constants ----
WM_KEYDOWN, WM_KEYUP, WM_CHAR = 0x0100, 0x0101, 0x0102
VK = {"left": 0x25, "up": 0x26, "right": 0x27, "down": 0x28, "space": 0x20,
      "return": 0x0D, "esc": 0x1B}
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
MAPVK_VK_TO_VSC = 0
KEYEVENTF_KEYUP = 0x0002
KEYEVENTF_SCANCODE = 0x0008
KEYEVENTF_EXTENDEDKEY = 0x0001
INPUT_KEYBOARD = 1
TARGET_IMAGE = "nexustk.exe"

# arrow keys are "extended" keys -- the extended bit matters for some input readers
EXTENDED = {0x25, 0x26, 0x27, 0x28}


# ---- find the client window by owning process image name ----
def proc_image(pid):
    h = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not h:
        return ""
    try:
        buf = ctypes.create_unicode_buffer(32768)
        size = wintypes.DWORD(len(buf))
        if kernel32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(size)):
            return buf.value
        return ""
    finally:
        kernel32.CloseHandle(h)


def find_windows():
    """Return [(hwnd, title, pid, image)] for every visible top-level window whose
    owning process is NexusTK.exe."""
    found = []
    WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def cb(hwnd, _):
        if not user32.IsWindowVisible(hwnd):
            return True
        pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        img = proc_image(pid.value)
        if img and img.lower().endswith(TARGET_IMAGE):
            n = user32.GetWindowTextLengthW(hwnd)
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, buf, n + 1)
            found.append((hwnd, buf.value, pid.value, img))
        return True

    user32.EnumWindows(WNDENUMPROC(cb), 0)
    return found


# ---- lparam builder for WM_KEYDOWN/UP (repeat=1, real scancode, ext + transition bits) ----
def key_lparam(vk, up):
    scan = user32.MapVirtualKeyW(vk, MAPVK_VK_TO_VSC) & 0xFF
    lp = 1                      # repeat count
    lp |= scan << 16           # scancode
    if vk in EXTENDED:
        lp |= 1 << 24          # extended-key flag
    if up:
        lp |= 1 << 30          # previous key-state (was down)
        lp |= 1 << 31          # transition (key being released)
    return lp


def post_key(hwnd, vk, hold=0.08):
    user32.PostMessageW(hwnd, WM_KEYDOWN, vk, key_lparam(vk, False))
    time.sleep(hold)
    user32.PostMessageW(hwnd, WM_KEYUP, vk, key_lparam(vk, True))


# ---- SendInput path (foreground, hardware-level) ----
class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wintypes.WORD), ("wScan", wintypes.WORD),
                ("dwFlags", wintypes.DWORD), ("time", wintypes.DWORD),
                ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong))]


class _INPUTunion(ctypes.Union):
    _fields_ = [("ki", KEYBDINPUT)]


class INPUT(ctypes.Structure):
    _fields_ = [("type", wintypes.DWORD), ("u", _INPUTunion)]


def send_key(vk, hold=0.08):
    scan = user32.MapVirtualKeyW(vk, MAPVK_VK_TO_VSC) & 0xFF
    ext = KEYEVENTF_EXTENDEDKEY if vk in EXTENDED else 0

    def make(up):
        flags = KEYEVENTF_SCANCODE | ext | (KEYEVENTF_KEYUP if up else 0)
        return INPUT(type=INPUT_KEYBOARD,
                     u=_INPUTunion(ki=KEYBDINPUT(0, scan, flags, 0, None)))

    down, up = make(False), make(True)
    user32.SendInput(1, ctypes.byref(down), ctypes.sizeof(INPUT))
    time.sleep(hold)
    user32.SendInput(1, ctypes.byref(up), ctypes.sizeof(INPUT))


def _tap_running():
    """Is nexus_agent.py actively logging? (raw_packets.jsonl written in last ~15s.)"""
    try:
        return time.time() - os.path.getmtime(RAW) < 15
    except OSError:
        return False


def _wire_reacted(since_ms):
    """Count self-movement/turn frames logged with ts > since_ms. Reads only the tail
    so it stays cheap on a multi-MB capture."""
    tally = {}
    try:
        with open(RAW, "rb") as f:
            f.seek(0, 2)
            back = min(f.tell(), 200_000)
            f.seek(-back, 2)
            chunk = f.read().decode("utf-8", "replace")
    except OSError:
        return tally
    for line in chunk.splitlines():
        line = line.strip()
        if not line.startswith("{"):
            continue
        try:
            p = json.loads(line)
        except ValueError:
            continue
        if p.get("t") == "atk":
            continue
        if p.get("ts", 0) > since_ms and p.get("op") in SELF_ECHO_OPS:
            tally[p["op"]] = tally.get(p["op"], 0) + 1
    return tally


def main():
    args = sys.argv[1:]
    use_send = "--sendinput" in args
    do_attack = "--attack" in args

    def opt(name, default):
        return args[args.index(name) + 1] if name in args else default

    custom_key = opt("--key", None)
    hold = float(opt("--hold", "0.08"))
    repeat = int(opt("--repeat", "2"))

    wins = find_windows()
    if not wins:
        print("No NexusTK.exe window found. Is the client running and logged in?")
        print("(This test needs the game up; it does not launch it.)")
        return
    hwnd, title, pid, img = wins[0]
    print(f"Found client: hwnd={hwnd}  pid={pid}  title={title!r}")
    print(f"  image: {img}")
    if len(wins) > 1:
        print(f"  (+{len(wins)-1} more NexusTK windows; using the first)")

    mode = "SendInput (foreground)" if use_send else "PostMessage (background-safe)"
    print(f"\nMode: {mode}")

    if use_send:
        user32.SetForegroundWindow(hwnd)
        time.sleep(0.4)
    fire = send_key if use_send else (lambda vk, h=hold: post_key(hwnd, vk, h))

    def burst(name, vk, n):
        print(f"  -> {name} x{n}")
        for _ in range(n):
            fire(vk, hold) if use_send else fire(vk)
            time.sleep(0.22)

    tap_live = _tap_running()
    if tap_live:
        print("Tap detected (raw_packets.jsonl is fresh) -- will auto-check the wire reaction.")
    else:
        print("Tap NOT running -- only visual confirmation available. Start nexus_agent.py")
        print("in another window for an objective wire check.")

    start_ms = time.time() * 1000
    print("\nStarting in 2s -- watch the character AND re/auto/raw_packets.jsonl ...")
    time.sleep(2)

    if custom_key:
        burst(custom_key, VK[custom_key], repeat)
    else:
        # a little box: each direction a couple times, so a step is unmistakable
        for name in ("right", "down", "left", "up"):
            burst(name, VK[name], repeat)
    if do_attack:
        burst("space (attack)", VK["space"], 4)

    if tap_live:
        time.sleep(1.0)   # let the echo frames land + get logged
        echoed = _wire_reacted(start_ms)
        print("\n--- WIRE CHECK ---")
        if echoed:
            tally = ", ".join(f"0x{op:02x}:{n}" for op, n in sorted(echoed.items()))
            print(f"Wire REACTED: {tally} frame(s) after injection. Input is ACCEPTED.")
            print("=> This mode works. (0x04/0x0c also reveal our own entity id.)")
        else:
            print("No self-echo (0x04/0x0c/0x11) after injection -- the client did NOT")
            print("accept these keys in this mode. Try the other mode / different keys.")

    print("\nDone. Did the character move/turn/swing?")
    print("If YES on PostMessage -> we can grind in the background. If only --sendinput")
    print("worked -> the grinder must keep the window foregrounded. If NEITHER moved it")
    print("-> input injection is out; we go the frida call-into-client route.")


if __name__ == "__main__":
    main()
