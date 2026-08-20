"""
Frida probe for the 5.33 client's TERRAIN pipeline — answers "does our 0x06 map-data reach the
renderer intact?" by hooking the client's own handlers (addresses from reversing NexusTK.exe,
image base 0x400000):

  sub_469060  (VA 0x469060)  0x06 map-data handler. Arg0 (stack) = body ptr; layout after the
                             client's decrypt is: [0]=flag [1..2]=x0(BE) [3..4]=y0(BE) [5]=w [6]=h
                             [7..]= {tile(BE) pass(BE) obj(BE)}*. We dump x0/y0/w/h + first 3 cells.
  sub_465b50  (VA 0x465b50)  per-cell "redraw" — counts how often the client actually repaints a
                             cell (i.e. is the draw path running at all?).
  sub_468bb0  (VA 0x468bb0)  0x15 map-info parse — dump mapid/w/h so we confirm dims decode.

Run (spawns the client itself, recommended so we catch the login-time map-info):
      python re\\frida_probe_533_map.py
 or attach to an already-running client:
      python re\\frida_probe_533_map.py attach
"""
import frida, sys, time
from _paths import CLIENT5

JS = r"""
var _mod = (typeof Process.findModuleByName === 'function') ? Process.findModuleByName('NexusTK.exe') : null;
if (!_mod && typeof Process.getModuleByName === 'function') { try { _mod = Process.getModuleByName('NexusTK.exe'); } catch (e) {} }
var base = _mod ? _mod.base : ptr('0x400000');   // exe is non-ASLR (base 0x400000) so this is safe
send('[probe] NexusTK.exe base = ' + base);

function be16(p, off) { return (p.add(off).readU8() << 8) | p.add(off + 1).readU8(); }

// --- 0x06 map-data handler: dump the client's decrypted view of our packet ---
Interceptor.attach(base.add(0x69060), {
    onEnter: function (args) {
        // __thiscall with one stack arg: body ptr at [esp+4] on entry
        var body = this.context.esp.add(4).readPointer();
        try {
            var flag = body.readU8();
            var x0 = be16(body, 1), y0 = be16(body, 3);
            var w = body.add(5).readU8(), h = body.add(6).readU8();
            var cells = [];
            for (var i = 0; i < 3 && i < w * h; i++) {
                var o = 7 + i * 6;
                cells.push('[t=' + be16(body, o) + ' p=' + be16(body, o + 2) + ' o=' + be16(body, o + 4) + ']');
            }
            send('MAPDATA 0x06  flag=' + flag + '  rect(' + x0 + ',' + y0 + ') ' + w + 'x' + h +
                 '  first=' + cells.join(' '));
        } catch (e) { send('MAPDATA 0x06  <read error: ' + e + '>'); }
    }
});

// --- per-cell redraw: how many times does the client actually repaint a cell? ---
var redraws = 0, lastReport = 0;
Interceptor.attach(base.add(0x65b50), {
    onEnter: function () {
        redraws++;
        if (redraws - lastReport >= 100) { lastReport = redraws; send('REDRAW cell count = ' + redraws); }
    }
});

// --- 0x15 map-info parse: confirm dims decode (arg0 = body ptr; mapid@1 w@3 h@5, all BE) ---
Interceptor.attach(base.add(0x68bb0), {
    onEnter: function () {
        var body = this.context.esp.add(4).readPointer();
        try {
            send('MAPINFO 0x15  mapid=' + be16(body, 1) + '  w=' + be16(body, 3) + '  h=' + be16(body, 5));
        } catch (e) { send('MAPINFO 0x15  <read error: ' + e + '>'); }
    }
});

send('[probe] hooks armed: 0x06 map-data, cell redraw, 0x15 map-info');
"""


def on_message(msg, data):
    if msg.get("type") == "send":
        print(time.strftime("[%H:%M:%S] ") + str(msg.get("payload")))
    else:
        print("[!]", msg)


def main():
    install = CLIENT5
    exe = install + r"\NexusTK.exe"
    attach_mode = len(sys.argv) > 1 and sys.argv[1] == "attach"
    if attach_mode:
        session = frida.attach("NexusTK.exe")
        pid = None
    else:
        pid = frida.spawn([exe], cwd=install)   # cwd so the client finds its .DAT files
        session = frida.attach(pid)
    script = session.create_script(JS)
    script.on("message", on_message)
    script.load()
    if pid is not None:
        frida.resume(pid)
    print("[probe] running — log in and watch. Ctrl-C to stop.")
    sys.stdin.read()


if __name__ == "__main__":
    main()
