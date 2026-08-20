#!/usr/bin/env python
"""
Live instrumentation for the "abandoned nameplate triangle" litter bug (2026-07-27).

Reproduces LOCALLY on a single client (per user report) -- no peer needed. Hooks the
entity create/destroy/decoration functions found via static RE (re/disx.py) of the 0x33
self-entity factory (0x44d7d0) and its self-vs-peer dispatch, PLUS the allocator (filtered
to the two known entity sizes) so we can directly see whether appearance-refresh calls
(equip/unequip, mount/dismount) actually allocate a fresh entity object for OUR OWN id
(contradicting the earlier static-only theory that self never reallocates), and whether
the decoration-cleanup call (0x425350) fires or gets skipped.

Usage:
    python re/frida_nametag.py --attach
Then reproduce in-game (equip/unequip gear, mount/dismount a few times) and watch for:
  - ENTCREATE lines with the SAME entityId appearing more than once without a matching
    DESTROY in between -> confirms a real duplicate/leak, not just static theory.
  - Which branch fires: "SELF-PATH" (0x44d8c8) vs "PEER-PATH" (0x44d944), and whether
    the SELF-PATH's own lookup (eax at entry) is finding the prior entity or not.
  - ALLOC lines for size 0x1ac/0x134 (real entity allocs) vs how many DESTROY/DECOR-CLEANUP
    lines follow.
"""
import sys, os, time, frida
from _paths import CLIENT

EXE = str(CLIENT / "NexusTK_local.exe")
LOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "nametag_log.txt")

RVA = {
    "h33":        0x04fef0,  # 0x44fef0 the 0x33 handler (entry)
    "entcreate":  0x04d7d0,  # 0x44d7d0 create/find entity dispatcher
    "selfpath":   0x04d8c8,  # 0x44d8c8 mid-function label: self-vs-peer id compare + self camera-refresh
    "peerpath":   0x04d944,  # 0x44d944 mid-function label: peer destroy-if-found + realloc
    "destroyA":   0x04da40,  # 0x44da40 full destroy (peer/mob path): spatial-unlink + hashmap-unlink + decor-cleanup + dtor
    "destroyB":   0x04d9f0,  # 0x44d9f0 despawn-by-id destroy (real 0x0E path)
    "hmremove":   0x05c8f0,  # 0x45c8f0 id-hashmap remove (called both by self-path AND destroyA/B)
    "hmlookup":   0x05cb80,  # 0x45cb80 id-hashmap lookup
    "hmregister": 0x05c830,  # 0x45c830 id-hashmap register (called after every construct, self+peer)
    "decorclean": 0x025350,  # 0x425350 nameplate/decoration detach (conditionally called from destroyA)
    "selfcam":    0x04c660,  # 0x44c660 self camera/viewport refresh, called from the self-path
    "opnew":      0x03fd80,  # 0x43fd80 operator new
    "h0e":        0x050440,  # 0x450440 0x0E despawn handler (server->client)
    "monctor":    0x061a50,  # 0x461a50 entity ctor (peer/mob path)
    "selfctor":   0x061e30,  # 0x461e30 self look-descriptor writer (called from peer-path branch when id==self? kept for reference)
    # ---- h33-caller-level self-check + decoration/sprite build (0x44fef0, AFTER entcreate returns) ----
    "selfcmp":    0x050080,  # 0x450080 `cmp [world+0x40c],edi` -- SHOULD bail (->0x450159) when edi==self entity
    "bail":       0x050159,  # 0x450159 the shared bail target for h33
    "spritector": 0x063380,  # 0x463380 the renderKind 1/2/3 sprite ctor (per protocol doc) -- should NEVER fire for self
    "attachdeco": 0x062050,  # 0x462050 attach-sprite-to-entity call right before the mystery hmregister
    "apparse":    0x036120,  # 0x436120 7-byte appearance-form parser: reads body[esi..esi+7) BEFORE h33 continues
}

JS = r"""
'use strict';
const MOD = 'NexusTK_local.exe';
const RVA = __RVA_JSON__;
function moduleByName(name) { return (typeof Process.findModuleByName === 'function') ? Process.findModuleByName(name) : null; }
let base = null;
{ const m = moduleByName(MOD); base = m ? m.base : Process.enumerateModules()[0].base; }
send({t:'info', m:'module base = ' + base});
function at(name) { return base.add(ptr(RVA[name])); }

let selfId = null;   // learned from the first 0x33 body where X/Y/id look self-shaped isn't reliable;
                      // instead we read it live from [world+0x40c]->+0x108 the first time we see selfpath fire.
let world = null;

function fmtId(id) {
    if (selfId !== null && id === selfId) return '0x' + (id>>>0).toString(16) + '(SELF)';
    return '0x' + (id>>>0).toString(16);
}

// ---- 0x33 handler entry: decode entityId from the packet body so we know what's being (re)drawn ----
let in33 = 0, cur33Id = null;
Interceptor.attach(at('h33'), {
  onEnter(args) {
    in33++;
    const body = args[0];
    try {
      const x = (body.add(1).readU8()<<8) | body.add(2).readU8();
      const y = (body.add(3).readU8()<<8) | body.add(4).readU8();
      const dir = body.add(5).readU8();
      const id = (body.add(6).readU8()*0x1000000 + body.add(7).readU8()*0x10000 + body.add(8).readU8()*0x100 + body.add(9).readU8()) >>> 0;
      const type = body.add(10).readU8();
      cur33Id = id;
      send({t:'H33', m:'ENTER id=' + fmtId(id) + ' xy=(' + x + ',' + y + ') dir=' + dir + ' type=' + type});
    } catch (e) { send({t:'H33', m:'ENTER <decode failed: ' + e + '>'}); }
  },
  onLeave(ret) { in33--; }
});

// ---- entity create/find dispatcher: entry args + eax (lookup result) is set by the time selfpath/peerpath run ----
Interceptor.attach(at('entcreate'), {
  onEnter(args) {
    this.id = args[0].toInt32() >>> 0;
    send({t:'ENTCREATE', m:'ENTER id=' + fmtId(this.id)});
  },
  onLeave(ret) {
    send({t:'ENTCREATE', m:'LEAVE id=' + fmtId(this.id) + ' -> entity=' + ret + (ret.isNull() ? '  <NULL>' : '')});
  }
});

// mid-function labels -- valid jump targets, safe to hook directly. this.context.eax = lookup result
// at that point (0=not found -> destroy skipped on self-path; peer-path destroys iff eax!=0 too).
Interceptor.attach(at('selfpath'), {
  onEnter(args) {
    const eax = this.context.eax.toInt32() >>> 0;
    send({t:'BRANCH', m:'SELF-PATH taken, prior-lookup eax=0x' + eax.toString(16) + (eax===0 ? '  (nothing found to destroy)' : '  (FOUND existing -> should destroy)')});
  }
});
Interceptor.attach(at('peerpath'), {
  onEnter(args) {
    const eax = this.context.eax.toInt32() >>> 0;
    send({t:'BRANCH', m:'PEER-PATH taken, prior-lookup eax=0x' + eax.toString(16) + (eax===0 ? '  (nothing found to destroy)' : '  (FOUND existing -> should destroy)')});
  }
});

Interceptor.attach(at('destroyA'), {
  onEnter(args) { send({t:'DESTROY', m:'destroyA(0x44da40) ENTER entity=' + args[0]}); }
});
Interceptor.attach(at('destroyB'), {
  onEnter(args) { send({t:'DESTROY', m:'destroyB(0x44d9f0/0x0E-path) ENTER id=' + fmtId(args[0].toInt32()>>>0)}); }
});
Interceptor.attach(at('decorclean'), {
  onEnter(args) {
    const entity = this.context.ecx;
    let deco = '<err>';
    try { deco = entity.add(4).readPointer(); } catch (e) {}
    send({t:'DECOR', m:'decorclean(0x425350) ENTER entity=' + entity + ' deco[+4]=' + deco + (deco.isNull ? (deco.isNull()?' <already null, NOOP>':'') : '')});
  }
});
Interceptor.attach(at('hmremove'), {
  onEnter(args) { send({t:'HASHMAP', m:'remove entity=' + args[0]}); }
});
Interceptor.attach(at('hmlookup'), {
  onEnter(args) { this.id = args[0].toInt32()>>>0; },
  onLeave(ret) { send({t:'HASHMAP', m:'lookup id=' + fmtId(this.id) + ' -> ' + ret + (ret.isNull()?' <not found>':' <found>')}); }
});
Interceptor.attach(at('hmregister'), {
  onEnter(args) { send({t:'HASHMAP', m:'register entity=' + args[0]}); }
});
Interceptor.attach(at('selfcam'), {
  onEnter(args) {
    send({t:'SELFCAM', m:'0x44c660 (self camera/viewport refresh) ENTER this=' + this.context.ecx});
  },
  onLeave(ret) { send({t:'SELFCAM', m:'0x44c660 LEAVE'}); }
});
Interceptor.attach(at('h0e'), {
  onEnter(args) {
    try {
      const cnt = args[0].add(1).readU8();
      send({t:'H0E', m:'*** real 0x0E despawn packet, count=' + cnt + ' ***'});
    } catch (e) { send({t:'H0E', m:'*** real 0x0E despawn packet ***'}); }
  }
});
let allocCount = {};
Interceptor.attach(at('opnew'), {
  onEnter(args) {
    const sz = args[0].toInt32();
    if (sz === 0x1ac || sz === 0x134) {
      allocCount[sz] = (allocCount[sz]||0) + 1;
      send({t:'ALLOC', m:'operator new(0x' + sz.toString(16) + ') call #' + allocCount[sz] + (sz===0x1ac?' [peer/mob entity]':' [self look-descriptor buffer]')});
    }
  }
});

Interceptor.attach(at('selfcmp'), {
  onEnter(args) {
    const world = this.context.ecx;
    const edi = this.context.edi;
    let selfPtr = '<err>';
    try { selfPtr = world.add(0x40c).readPointer(); } catch (e) {}
    send({t:'SELFCMP', m:'world=' + world + ' self[+0x40c]=' + selfPtr + ' edi(new entity)=' + edi +
      (String(selfPtr) === String(edi) ? '  MATCH -> should BAIL' : '  *** MISMATCH -> will NOT bail, proceeds to build sprite/decoration! ***')});
  }
});
Interceptor.attach(at('bail'), {
  onEnter(args) { send({t:'BAIL', m:'h33 reached the shared bail target (0x450159)'}); }
});
// NOP_DECO: force the decoration/marker-sprite ctor to appear to fail (return NULL), so the caller's
// "test eax,eax; je bail" skips attach(0x462050)+register(0x45c830)+activate([+0x78]) entirely. The real
// ctor still runs (its own allocation leaks harmlessly for this short live test -- reversed by detaching),
// but NOTHING gets attached to the entity or shown. If characters go fully invisible, this object is the
// core body sprite, not just the nameplate; if bodies stay visible and only the triangle vanishes, this
// object IS purely the marker and is a real binary-patch target.
const NOP_DECO = '__NOP_DECO__';
Interceptor.attach(at('spritector'), {
  onEnter(args) {
    send({t:'SPRITECTOR', m:'0x463380 ENTER this(entity)=' + this.context.ecx + ' args=[' + args[0] + ',' + args[1] + ',' + args[2] + ',' + args[3] + ']'});
  },
  onLeave(retval) {
    send({t:'SPRITECTOR', m:'0x463380 LEAVE -> ' + retval + (NOP_DECO === 'true' ? '  (forcing NULL)' : '')});
    if (NOP_DECO === 'true') retval.replace(ptr(0));
  }
});
Interceptor.attach(at('attachdeco'), {
  onEnter(args) {
    send({t:'ATTACHDECO', m:'0x462050 ENTER this(entity)=' + this.context.ecx + ' sprite=' + args[0]});
  }
});

// ---- appearance byte[4] sweep: this offset is documented as "unknown, no visible sprite change 0..8" --
// test whether it actually controls the always-on nameplate/triangle marker instead. Overwrites the LIVE
// incoming packet bytes (before the parser reads them) with an escalating test value, once per 0x33 call,
// so each equip/mount toggle in-game advances to the next value. Watch the log for which value is active
// when the triangle disappears (if any).
let sweepN = 0;
const SWEEP_VALUES = [0,1,2,3,4,5,6,7,8,16,32,64,128,255];
Interceptor.attach(at('apparse'), {
  onEnter(args) {
    const val = SWEEP_VALUES[sweepN % SWEEP_VALUES.length];
    try {
      const before = args[0].add(4).readU8();
      args[0].add(4).writeU8(val);
      send({t:'SWEEP', m:'id=' + fmtId(cur33Id) + ' appearance[4]: ' + before + ' -> ' + val + '  (call #' + sweepN + ')'});
    } catch (e) { send({t:'SWEEP', m:'write failed: ' + e}); }
    sweepN++;
  }
});

send({t:'info', m:'nametag-litter hooks installed'});
""".replace("__RVA_JSON__", __import__("json").dumps(RVA)) \
   .replace("'__NOP_DECO__'", "'true'" if "--nop-deco" in sys.argv else "'false'")


def main():
    attach = "--attach" in sys.argv
    logf = open(LOG, "w", encoding="utf-8", buffering=1)

    def out(line):
        stamp = time.strftime("%H:%M:%S.") + f"{int(time.time()*1000)%1000:03d}"
        s = f"[{stamp}] {line}"
        print(s)
        logf.write(s + "\n")

    def on_message(msg, data):
        if msg["type"] == "error":
            out("JS-ERROR " + msg.get("description", str(msg)))
            return
        p = msg["payload"]
        out(f"{p.get('t','?'):<10} {p.get('m','')}")

    if attach:
        out("attaching to running client...")
        session = frida.attach("NexusTK_local.exe")
        pid = None
    else:
        out(f"spawning {EXE}")
        pid = frida.spawn([EXE])
        session = frida.attach(pid)

    script = session.create_script(JS)
    script.on("message", on_message)
    script.load()
    if pid is not None:
        frida.resume(pid)
    duration = 120
    for a in sys.argv:
        if a.startswith("--duration="):
            duration = int(a.split("=", 1)[1])
    out(f"logging to {LOG}  (running for {duration}s)")
    try:
        time.sleep(duration)
    except KeyboardInterrupt:
        pass
    out("detaching")


if __name__ == "__main__":
    main()
