"""Find an input method that actually moves the character. Tries: child windows via
PostMessage, foreground+PostMessage, and SendInput (foreground). Reports which moves pos."""
import sys, time, ctypes
from ctypes import wintypes
import frida
sys.path.insert(0, ".")
from bot_input_test import find_windows, post_key, VK
import selfptr

u = ctypes.windll.user32
wins = find_windows()
hwnd = wins[0][0]; pid = wins[0][2]
print(f"top window hwnd={hwnd} pid={pid} title={wins[0][1]!r}")

# enumerate child windows of the game
children = []
@ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
def enum_cb(h, l):
    buf = ctypes.create_unicode_buffer(128)
    u.GetClassNameW(h, buf, 128)
    r = wintypes.RECT(); u.GetWindowRect(h, ctypes.byref(r))
    children.append((h, buf.value, (r.right-r.left, r.bottom-r.top)))
    return True
u.EnumChildWindows(hwnd, enum_cb, 0)
print("child windows (hwnd, class, size):")
for c in children:
    print("  ", c)

sc, ex = selfptr.attach()
base = int(ex.base(), 16)
def pos():
    v = selfptr.read_self(ex, base); return (v["x"], v["y"]) if v else None

def try_moves(label, poster):
    print(f"\n--- {label} ---")
    moved_any = False
    for d in ["down", "up", "left", "right", "down", "up"]:
        p0 = pos(); poster(d); time.sleep(0.25); p1 = pos()
        m = p0 != p1
        moved_any = moved_any or m
        print(f"  {d}: {p0}->{p1} {'MOVED' if m else '.'}")
    return moved_any

WM_KEYDOWN, WM_KEYUP = 0x100, 0x101
def post_to(h, d):
    vk = VK[d]
    u.PostMessageW(h, WM_KEYDOWN, vk, 1)
    time.sleep(0.06)
    u.PostMessageW(h, WM_KEYUP, vk, 0xC0000001)

# 1) each child window via PostMessage
for h, cls, sz in children:
    if try_moves(f"child {h} ({cls})", lambda d, hh=h: post_to(hh, d)):
        print(f"  >>> CHILD {h} ({cls}) MOVES THE CHARACTER"); break

# 2) foreground the window, then PostMessage to top
print("\nforegrounding window...")
u.SetForegroundWindow(hwnd); time.sleep(0.4)
try_moves("foreground + PostMessage(top)", lambda d: post_to(hwnd, d))

# 3) SendInput (foreground)
def sendinput_key(d):
    vk = VK[d]
    # scan-code based key event
    class KEYBDINPUT(ctypes.Structure):
        _fields_ = [("wVk", wintypes.WORD), ("wScan", wintypes.WORD),
                    ("dwFlags", wintypes.DWORD), ("time", wintypes.DWORD),
                    ("dwExtraInfo", ctypes.POINTER(wintypes.ULONG))]
    class INPUT(ctypes.Structure):
        class _U(ctypes.Union):
            _fields_ = [("ki", KEYBDINPUT)]
        _anonymous_ = ("u",); _fields_ = [("type", wintypes.DWORD), ("u", _U)]
    def mk(flags):
        i = INPUT(); i.type = 1; i.ki = KEYBDINPUT(vk, 0, flags, 0, None); return i
    u.SendInput(1, ctypes.byref(mk(0)), ctypes.sizeof(INPUT))
    time.sleep(0.06)
    u.SendInput(1, ctypes.byref(mk(2)), ctypes.sizeof(INPUT))

u.SetForegroundWindow(hwnd); time.sleep(0.3)
try_moves("SendInput (foreground)", sendinput_key)
print("\nfinal pos:", pos())
