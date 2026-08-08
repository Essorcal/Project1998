"""Fetch the self-profile AUTONOMOUSLY and decode every string in it.

Learned from a live capture: the client requests its own profile with the plaintext
packet `2d 00 00`, and the server answers with 0x39. Viewing an item in that window sends
NOTHING further -- so whatever the client shows about an item (including a weapon's damage
range) it already holds locally, which means it must arrive in this packet (or at login).

So: call the client's own send fn with `2d 00 00` (it frames + encrypts for us, no cipher
work needed), then dump the reply's full string table.

Usage:  python fetch_profile.py
"""
import sys, time, re
sys.path.insert(0, ".")
import nexus_bot as NB
import frida

JS = r"""
'use strict';
var CONN = null;
const SENDFN = new NativeFunction(ptr('0x576660'), 'int',
                                  ['pointer','pointer','uint'], 'thiscall');
// the connection object is `this` on any real send; grab it from the first one we see
const L = Interceptor.attach(ptr('0x576660'), {
  onEnter(args){ if (!CONN) { CONN = this.context.ecx; } }
});
rpc.exports = {
  ready: function(){ return CONN !== null; },
  sendraw: function(bytes){
    if (!CONN) return false;
    const m = Memory.alloc(bytes.length);
    for (let i = 0; i < bytes.length; i++) m.add(i).writeU8(bytes[i]);
    try{
      Interceptor.detachAll();            // never call an address we are hooking
      SENDFN(CONN, m, bytes.length);
      return true;
    }catch(e){ return false; }
  },
};
"""


def strings_in(d, minlen=3):
    """Length-prefixed ASCII strings, as the profile encodes them."""
    out, i = [], 0
    while i < len(d):
        n = d[i]
        if minlen <= n <= 60 and i + 1 + n <= len(d):
            chunk = d[i + 1:i + 1 + n]
            if all(32 <= c < 127 for c in chunk):
                out.append((i, chunk.decode()))
                i += 1 + n
                continue
        i += 1
    return out


def main():
    got = {}

    def on_recv(msg, data):
        if msg.get("type") != "send":
            return
        p = msg["payload"]
        if p.get("op") == 0x39 and "hex" not in got:
            got["hex"] = p["hex"]
            got["n"] = p.get("n")

    s2, sc2 = NB.attach(on_recv)

    dev = frida.get_local_device()
    pid = [p.pid for p in dev.enumerate_processes()
           if p.name.lower() == "nexustk.exe"][0]
    sess = dev.attach(pid)
    sc = sess.create_script(JS)
    sc.load()
    ex = sc.exports_sync

    print("waiting for the client to send something (to capture the connection object)...")
    for _ in range(60):
        if ex.ready():
            break
        time.sleep(1)
    if not ex.ready():
        print("never saw a send -- move the character once and rerun")
        sess.detach(); s2.detach(); return

    print("requesting profile with `2d 00 00` (the format the client itself uses)")
    ok = ex.sendraw([0x2d, 0x00, 0x00])
    print(f"  sendraw -> {ok}")
    for _ in range(50):
        if "hex" in got:
            break
        time.sleep(0.2)

    if "hex" not in got:
        print("no 0x39 reply")
        sess.detach(); s2.detach(); return

    d = bytes(int(x, 16) for x in got["hex"].split())
    print(f"\n0x39 reply: {len(d)} bytes\n")
    ss = strings_in(d)
    print("strings:")
    for off, s in ss:
        print(f"  @{off:>4}  {s!r}")

    # a weapon damage range shows as e.g. '1m20' / '15m50'
    dmg = [s for _, s in ss if re.search(r"\d+\s*m\s*\d+", s, re.I)]
    print(f"\ndamage-range-looking strings: {dmg if dmg else 'NONE'}")
    if not dmg:
        print("=> item damage is NOT in the profile packet; it must come from the")
        print("   inventory/item stream (0x0f) at login or on pickup.")
    sess.detach(); s2.detach()


if __name__ == "__main__":
    main()
