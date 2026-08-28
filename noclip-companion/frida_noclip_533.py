#!/usr/bin/env python
r"""
NO-CLIP companion for the 5.33 client -- the client-side half of the server's @clip command.

WHY THIS EXISTS. The 5.33 client enforces object/wall collision LOCALLY: before it will send a walk it
calls its collision primitive sub_460f00(map, coord, dir) and refuses the step if that returns 0. The
server never sees a blocked attempt, so @clip alone (which waives the SERVER check and streams pass=0)
can open water/cliffs but not building walls. Reverse-engineering the client (execution-coverage diff of
a clear walk vs a wall-push, then disassembly) proved sub_460f00 is the sole gate and that NOTHING in it
or its callers consults a GM/self flag -- so there is no server-settable bypass. The lever is here: force
sub_460f00 to report "walkable", and the client stops refusing.

SEAMLESS BY DESIGN. Start this once and forget it:
  * it waits for NexusTK.exe, attaches, and installs the hook (and re-attaches if you relaunch the client);
  * it MIRRORS @clip automatically -- it watches for the server's "No-clip   :ON/OFF" status line and flips
    the override to match, so you just type @clip in game and both halves toggle together. No rubber-band
    when @clip is off (the override is off too), and nothing is written to disk (a live onLeave override,
    not a byte patch -- closing this restores the client instantly).

    Start-NoClip.bat            # double-click (recommended)
    python frida_noclip_533.py  # same thing from a terminal

Scope: this opens WALLS/objects. Other players are already passable under @clip (the client never blocked
on them -- that was server-side). Mobs/NPCs use a SEPARATE client check (not sub_460f00) and are a known
follow-up. Requires the 5.33 build these RVAs came from (image base 0x400000, non-ASLR).
"""
import sys, time, frida

MOD = "NexusTK.exe"
COLL_RVA = 0x60f00        # sub_460f00: the collision primitive. ret 1 = walkable, 0 = blocked.
# All four world/UI packet dispatchers (from frida_dispatch_533.py). Minitext (0x0A) rides one of them;
# we watch every one so we never miss the @clip confirmation regardless of which screen owns it.
DISPATCHERS = [0x63320, 0xD5F80, 0xE13D0, 0xE6B70]

JS = r"""
'use strict';
var m = (typeof Process.findModuleByName === 'function') ? Process.findModuleByName('__MOD__') : null;
if (!m && typeof Process.getModuleByName === 'function') { try { m = Process.getModuleByName('__MOD__'); } catch (e) {} }
var base = m ? m.base : ptr('0x400000');
var COLL = base.add(__COLL__);
var DISPATCHERS = __DISPATCHERS__;

var noclip = false;   // mirrors the server's @clip state

// The lever: whenever no-clip is on, force the client's collision primitive to report "walkable" (1).
// onLeave override -- no code is modified on disk or in memory, so toggling off (or detaching) is
// instant and total. When off, the client behaves exactly as stock.
Interceptor.attach(COLL, {
  onLeave: function (retval) { if (noclip) retval.replace(ptr(1)); }
});

function be16(p, o) { return (p.add(o).readU8() << 8) | p.add(o + 1).readU8(); }

// Watch every dispatcher for a 0x0A system-text packet and read its text. The server's @clip prints
// "No-clip          :ON" / ":OFF" via SendMiniText (opcode 0x0A: 0A type(u8) len(u16BE) text). Matching
// that line is what ties this companion to the chat command -- type @clip, this flips.
DISPATCHERS.forEach(function (rva) {
  Interceptor.attach(base.add(rva), {
    onEnter: function () {
      try {
        var a0 = this.context.esp.add(4).readPointer();
        var body = a0.add(0xc).readPointer();
        if (body.readU8() !== 0x0A) return;
        var len = be16(body, 2);
        if (len <= 0 || len > 200) return;
        var txt = body.add(4).readByteArray(Math.min(len, 96));
        var s = '';
        var u = new Uint8Array(txt);
        for (var i = 0; i < u.length; i++) s += String.fromCharCode(u[i]);
        if (!/no-?clip/i.test(s)) return;
        var on = /:\s*on/i.test(s);
        var off = /:\s*off/i.test(s);
        if (on === off) return;                 // need exactly one -- ignore anything ambiguous
        if (noclip !== on) { noclip = on; send({ t: 'state', on: on }); }
      } catch (e) {}
    }
  });
});

send({ t: 'info', m: 'hook armed @ sub_460f00 (' + COLL + '); mirroring @clip via 0x0A text' });
""".replace("__MOD__", MOD).replace("__COLL__", hex(COLL_RVA)).replace("__DISPATCHERS__", str(DISPATCHERS))


def on_message(msg, _data):
    if msg.get("type") == "error":
        print("  [frida-error]", msg.get("description")); return
    p = msg.get("payload", {})
    if p.get("t") == "info":
        print("  [ok]", p.get("m", ""))
    elif p.get("t") == "state":
        print("  >>> NO-CLIP " + ("ON  (walls open -- mirrored @clip)" if p["on"] else "OFF (stock collision restored)"))


def attach_once(dev):
    procs = [p for p in dev.enumerate_processes() if p.name.lower() == MOD.lower()]
    if not procs:
        return None
    pid = procs[0].pid
    session = dev.attach(pid)
    script = session.create_script(JS)
    script.on("message", on_message)
    script.load()
    print(f"  attached to {MOD} (pid {pid}). Type @clip in game to toggle.")
    return session


def main():
    dev = frida.get_local_device()
    print("[noclip] companion running -- keep this window open. Ctrl-C to stop.")
    print("[noclip] waiting for the 5.33 client ...")
    session = None
    detached = {"flag": False}
    try:
        while True:
            if session is None:
                session = attach_once(dev)
                if session is not None:
                    detached["flag"] = False
                    session.on("detached", lambda *a: detached.__setitem__("flag", True))
                else:
                    time.sleep(1.5)
                    continue
            if detached["flag"]:
                print("[noclip] client went away -- waiting for it to come back ...")
                session = None
                continue
            time.sleep(0.5)
    except KeyboardInterrupt:
        print("\n[noclip] stopped -- client collision restored.")


if __name__ == "__main__":
    main()
