#!/usr/bin/env python
r"""Trace the 5.33 client's MUSIC path end to end -- why a playlist stops after one song.

The 5.33 mp3 channel is a small state machine and every step of it is hookable:

    0x46a420  0x19 handler, type-1 arm. Reads id/fallback/vol, gates on the audio mode
              ([0x55bfc8]+0x1564 must be 0), then calls the resolver on the ONE music
              object at [0x55c000].
    0x4a6360  resolver(id, fallback, vol, loop). sprintf's "%08d.LST" / ".LSR" / ".MP3"
              and takes the first that exists in Mus000.dat. A hit on LST/LSR parses the
              file into a vector of "%08d.MP3" names at +0x1098..+0x109c, sets
              playlistMode(+0x10a5)=1, random(+0x10a4)=1 for .LSR, and enters at index 1
              (.LST) or rand()%count+1 (.LSR).
    0x4a5f80  play(index, vol, loop). For a playlist `index` is 1-based INTO THE VECTOR,
              not a track id. Early-outs to a no-op when index == the currently playing
              one (+0x28).
    0x4a7d90  end-of-stream notifier. Posts WM_USER+8 (0x408) -- but ONLY while the audio
              mode is 0 and playlistMode is set.
    0x4a7b40  WM_USER+8 handler = the ADVANCE. next = random ? rand()%count : cur; next++;
              play(next > count ? 1 : next, 100, 1).

So "one song then silence" has exactly four candidate causes, and this tells them apart:
  (a) the resolver never saw a playlist   -> RESOLVE prints kind=MP3
  (b) the stream never reports ending     -> no END line after the song
  (c) the advance is gated off            -> END prints mode!=0 or playlist=0, no ADVANCE
  (d) the advance fires but no-ops        -> ADVANCE prints next == cur (the +0x28 early-out)

Usage (the client is already running -- this only reads, it patches nothing):
    python re/frida_music_533.py                  # attach to NexusTK and stream events
    python re/frida_music_533.py --state          # one-shot dump of the music object
"""
import argparse
import sys
import time

import frida

RVA = {
    "handler": 0x6A420,   # 0x19 type-1 arm
    "resolve": 0xA6360,
    "play": 0xA5F80,
    "stop": 0xA6690,
    "notify": 0xA7D90,
    "advance": 0xA7B40,
}
G_APP = 0x55BFC8      # +0x1564 = audio mode
G_MUSIC = 0x55C000    # the music object the handler and WM_USER+8 both use

JS = r"""
const _m = (typeof Process.findModuleByName === 'function') ? Process.findModuleByName('NexusTK.exe') : null;
const BASE = _m ? _m.base : ptr('0x400000');   // non-ASLR, so the fallback is safe
const RVA = %(rva)s;
const G_APP = %(g_app)d, G_MUSIC = %(g_music)d;
const at = (n) => BASE.add(RVA[n]);

function app()   { return ptr(G_APP).readPointer(); }
function music() { return ptr(G_MUSIC).readPointer(); }
function mode()  { try { return app().add(0x1564).readU32(); } catch (e) { return -1; } }

function state() {
  try {
    const m = music();
    const begin = m.add(0x1098).readPointer(), end = m.add(0x109c).readPointer();
    return {
      playlist: m.add(0x10a5).readU8(),
      random:   m.add(0x10a4).readU8(),
      cur:      m.add(0x28).readS32(),
      id:       m.add(0x1094).readU32(),
      count:    end.sub(begin).toInt32() / 4,
      handle:   m.add(4).readU32(),
      stream:   m.add(0x10).readU32(),
      mode:     mode(),
    };
  } catch (e) { return { err: String(e) }; }
}
rpc.exports.state = state;

Interceptor.attach(at('resolve'), {
  onEnter(a) {
    this.info = { id: a[0].toInt32(), fallback: a[1].toInt32(),
                  vol: a[2].toInt32(), loop: a[3].toInt32(), before: state() };
  },
  onLeave() {
    const s = state();
    const kind = s.playlist ? (s.random ? 'LSR (random)' : 'LST (ordered)') : 'MP3 (single)';
    send({ t: 'RESOLVE', a: this.info, kind: kind, after: s });
  }
});

Interceptor.attach(at('play'), {
  onEnter(a) {
    const s = state();
    send({ t: 'PLAY', index: a[0].toInt32(), vol: a[1].toInt32(), loop: a[2].toInt32(),
           cur: s.cur, noop: (s.cur === a[0].toInt32()), st: s });
  }
});

Interceptor.attach(at('stop'), {
  onEnter(a) { send({ t: 'STOP', arg: a[0].toInt32(), st: state() }); }
});

Interceptor.attach(at('notify'), {
  onEnter(a) {
    const s = state();
    send({ t: 'END', mode: s.mode, playlist: s.playlist,
           posts: (s.mode === 0 && s.playlist) ? 'WM_USER+8' : 'NOTHING', st: s });
  }
});

Interceptor.attach(at('advance'), {
  onEnter(a) { send({ t: 'ADVANCE', st: state() }); }
});
"""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--state", action="store_true", help="dump the music object once and exit")
    ap.add_argument("--pid", type=int, help="which NexusTK.exe (needed once a second client is running)")
    args = ap.parse_args()

    try:
        session = frida.attach(args.pid or "NexusTK.exe")
    except frida.ProcessNotFoundError as e:
        # More than one client open: frida refuses the ambiguous name rather than picking one.
        pids = [p.pid for p in frida.get_local_device().enumerate_processes()
                if p.name.lower() == "nexustk.exe"]
        raise SystemExit(f"{e}\n  -> re-run with --pid <one of {pids}>" if pids else str(e))
    script = session.create_script(JS % {
        "rva": "{" + ",".join(f"'{k}':{v}" for k, v in RVA.items()) + "}",
        "g_app": G_APP, "g_music": G_MUSIC,
    })

    def on_message(msg, data):
        if msg["type"] != "send":
            print("!!", msg, file=sys.stderr)
            return
        p = msg["payload"]
        t = p["t"]
        ts = time.strftime("%H:%M:%S")
        if t == "RESOLVE":
            a = p["a"]
            print(f"[{ts}] RESOLVE id={a['id']} fallback={a['fallback']} vol={a['vol']} "
                  f"loop={a['loop']}  ->  {p['kind']}  count={p['after'].get('count')} "
                  f"cur={p['after'].get('cur')}")
        elif t == "PLAY":
            print(f"[{ts}] PLAY  index={p['index']} vol={p['vol']} loop={p['loop']} "
                  f"(cur was {p['cur']})" + ("   <-- NO-OP, index==cur" if p["noop"] else ""))
        elif t == "STOP":
            print(f"[{ts}] STOP  arg={p['arg']}")
        elif t == "END":
            print(f"[{ts}] END   audio mode={p['mode']} playlistMode={p['playlist']} "
                  f"-> posts {p['posts']}")
        elif t == "ADVANCE":
            s = p["st"]
            print(f"[{ts}] ADVANCE playlist={s.get('playlist')} random={s.get('random')} "
                  f"cur={s.get('cur')} count={s.get('count')}")

    script.on("message", on_message)
    script.load()

    if args.state:
        print(script.exports_sync.state())
        return

    print("hooked. play with @music new / @music <id> and wait for a track to end. ^C to stop.")
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
