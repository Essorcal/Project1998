"""Pin the self-view (0x39) vs other-view (0x34) profile widget parsers.

Both handlers call parser 0x424820(mode, body); it picks widget = window[mode*4+4]
and calls widget.vtable[0x5c](body). We hook the call site 0x4248c7 to capture, per
mode: the widget vtable, the concrete parser target [vtable+0x5c], and the raw body.

Run, then in-game press 's' (self, mode 0) and click yourself (other, mode 1).
"""
import frida, sys

PID = 2720
SITE = 0x004248c7  # call dword ptr [edx+0x5c]

js = """
var SITE = ptr(%d);
Interceptor.attach(SITE, {
    onEnter: function (args) {
        var ctx = this.context;
        var ebp = ctx.ebp;
        var mode = ebp.add(8).readU32();
        var body = ebp.add(0xc).readPointer();
        var vtable = ctx.edx;
        var target = vtable.add(0x5c).readPointer();
        // read up to 64 bytes of body
        var n = 64, hex = "";
        try { for (var i=0;i<n;i++){var b=body.add(i).readU8(); hex+=("0"+b.toString(16)).slice(-2)+" ";} } catch(e){}
        send({mode: mode, vtable: vtable.toString(), parser: target.toString(), body: hex});
    }
});
"""

def on_message(msg, data):
    if msg["type"] == "send":
        p = msg["payload"]
        print(f"\n=== mode={p['mode']} ({'SELF/0x39' if p['mode']==0 else 'OTHER/0x34'}) ===")
        print(f"  widget vtable = {p['vtable']}")
        print(f"  parser [+0x5c] = {p['parser']}")
        print(f"  body[0..64]   = {p['body']}")
    else:
        print(msg)

session = frida.attach(PID)
script = session.create_script(js % SITE)
script.on("message", on_message)
script.load()
print(f"Hooked {hex(SITE)} in PID {PID}. Press 's' (self) and click yourself (other). Ctrl+C to stop.")
sys.stdin.read()
