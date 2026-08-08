"""Test whether FORCING keyboard focus (AttachThreadInput trick, which bypasses the Windows
SetForegroundWindow restriction) makes injected movement register. If yes, the bot must hold
focus while moving."""
import sys, time, ctypes
from ctypes import wintypes
sys.path.insert(0, ".")
from bot_input_test import find_windows, post_key, send_key, VK
import selfptr

u = ctypes.windll.user32
k = ctypes.windll.kernel32
wins = find_windows(); hwnd = wins[0][0]
print(f"hwnd={hwnd} title={wins[0][1]!r}")

def force_focus(h):
    fg = u.GetForegroundWindow()
    tgt_tid = u.GetWindowThreadProcessId(h, None)
    fg_tid = u.GetWindowThreadProcessId(fg, None)
    cur_tid = k.GetCurrentThreadId()
    for t in {fg_tid, cur_tid}:
        u.AttachThreadInput(tgt_tid, t, True)
    u.BringWindowToTop(h)
    u.ShowWindow(h, 5)          # SW_SHOW
    u.SetForegroundWindow(h)
    u.SetFocus(h)
    u.SetActiveWindow(h)
    for t in {fg_tid, cur_tid}:
        u.AttachThreadInput(tgt_tid, t, False)

sc, ex = selfptr.attach()
base = int(ex.base(), 16)
def pos():
    v = selfptr.read_self(ex, base); return (v["x"], v["y"]) if v else None

print("focusing game window (forced)...")
force_focus(hwnd)
time.sleep(0.5)
fg = u.GetForegroundWindow()
print(f"foreground now = {fg}  (target={hwnd})  match={fg==hwnd}")

print("\n--- PostMessage arrows after forced focus ---")
for d in ["down", "up", "left", "right"]:
    p0 = pos(); post_key(hwnd, VK[d], 0.09); time.sleep(0.28); p1 = pos()
    print(f"  {d}: {p0}->{p1} {'MOVED' if p0!=p1 else '.'}")

print("\n--- SendInput arrows after forced focus ---")
for d in ["down", "up", "left", "right"]:
    force_focus(hwnd); time.sleep(0.1)
    p0 = pos(); send_key(VK[d], 0.09); time.sleep(0.28); p1 = pos()
    print(f"  {d}: {p0}->{p1} {'MOVED' if p0!=p1 else '.'}")

print("final pos:", pos())
