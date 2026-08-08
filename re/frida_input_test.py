"""Inject movement keys from INSIDE the game process via frida (calls PostMessageW/SendMessageW
on the game's own thread). Same-process messages bypass UIPI/elevation and work in background.
If this moves the character, we route ALL bot input through frida instead of external PostMessage."""
import sys, time, frida
sys.path.insert(0, ".")
from bot_input_test import find_windows
import selfptr

wins = find_windows(); hwnd = wins[0][0]
print(f"hwnd={hwnd} title={wins[0][1]!r}")

JS = r"""
const user32 = Process.getModuleByName('user32.dll');
const PostMessageW = new NativeFunction(user32.getExportByName('PostMessageW'),
                                        'int', ['pointer','uint','uint','pointer']);
const SendMessageW = new NativeFunction(user32.getExportByName('SendMessageW'),
                                        'pointer', ['pointer','uint','uint','pointer']);
const GetForegroundWindow = new NativeFunction(user32.getExportByName('GetForegroundWindow'),'pointer',[]);
function lparam(vk, up){
  // scancode via MapVirtualKey would be ideal; use repeat=1 + ext bit for arrows + up/transition
  let lp = 1;
  if (vk>=0x25 && vk<=0x28) lp |= (1<<24);   // arrows are extended keys
  if (up) lp |= (1<<30)|(1<<31);
  return ptr(lp>>>0);
}
rpc.exports = {
  fg: function(){ return GetForegroundWindow().toString(); },
  post: function(hwnd, vk){
    const h = ptr(hwnd);
    PostMessageW(h, 0x100, vk, lparam(vk,false));
    PostMessageW(h, 0x101, vk, lparam(vk,true));
    return true;
  },
  send: function(hwnd, vk){
    const h = ptr(hwnd);
    SendMessageW(h, 0x100, vk, lparam(vk,false));
    SendMessageW(h, 0x101, vk, lparam(vk,true));
    return true;
  }
};
"""
dev = frida.get_local_device()
pids = [p.pid for p in dev.enumerate_processes() if p.name.lower()=="nexustk.exe"]
s = dev.attach(pids[0]); sc = s.create_script(JS); sc.load(); ex = sc.exports_sync
# reuse the same script for reading pos too
base_mod = None
# separate selfptr attach for reading
rsc, rex = selfptr.attach(); rbase = int(rex.base(),16)
def pos():
    v = selfptr.read_self(rex, rbase); return (v["x"], v["y"]) if v else None

VK = {"left":0x25,"up":0x26,"right":0x27,"down":0x28,"esc":0x1B}
print("foreground (from inside proc):", ex.fg())
print("start pos:", pos())
print("\n--- pressing ESC 3x to close chat box, then move ---")
for _ in range(3):
    ex.post(str(hwnd), VK["esc"]); time.sleep(0.15)
print("\n--- frida PostMessageW (same-process) after ESC ---")
for d in ["down","up","left","right","down","down"]:
    p0=pos(); ex.post(str(hwnd), VK[d]); time.sleep(0.28); p1=pos()
    print(f"  {d}: {p0}->{p1} {'MOVED' if p0!=p1 else '.'}")
print("\n--- frida SendMessageW (same-process) ---")
for d in ["down","up","left","right"]:
    p0=pos(); ex.send(str(hwnd), VK[d]); time.sleep(0.28); p1=pos()
    print(f"  {d}: {p0}->{p1} {'MOVED' if p0!=p1 else '.'}")
print("final pos:", pos())
