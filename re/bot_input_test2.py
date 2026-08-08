"""Directly test whether injected movement input moves the character (memory position).
Reads self-pos via the static pointer, posts a key several times, re-reads."""
import sys, time, frida
sys.path.insert(0, ".")
from bot_input_test import find_windows, post_key, VK
import selfptr

wins = find_windows()
print("windows:", wins)
hwnd = wins[0][0]
pid = wins[0][2]
print(f"using hwnd={hwnd} pid={pid}")

sc, ex = selfptr.attach()
base = int(ex.base(), 16)

def pos():
    v = selfptr.read_self(ex, base)
    return (v["x"], v["y"]) if v else None

print("start pos:", pos())
for d in ["down", "down", "up", "up", "left", "left", "right", "right"]:
    p0 = pos()
    post_key(hwnd, VK[d], 0.09)
    time.sleep(0.25)
    p1 = pos()
    print(f"  {d}: {p0} -> {p1}  {'MOVED' if p0 != p1 else 'no-op'}")
print("end pos:", pos())
