#!/usr/bin/env python
"""
spell_probe.py -- work out how the live client CASTS spells, so the bot can cast Gateway
(emergency escape), Might (buff), and Soothe (heal) via injected input.

Two unknowns to resolve empirically:
  1. WHERE the spell list lives (names + slot order + hotkeys) -> scan memory for the spell
     name strings ("Gateway", "Might", "Soothe") in ASCII and UTF-16.
  2. WHAT input sequence casts a spell -> inject a candidate key sequence and watch the
     OUTGOING wire; the new opcode that appears when a cast lands is the cast packet.

Usage:
  python re/spell_probe.py --scan                 # find the spell strings in memory
  python re/spell_probe.py --keys "?"             # open spell list, watch opcodes
  python re/spell_probe.py --keys "z" --hold 0.1  # try the cast key, watch opcodes
  python re/spell_probe.py --watch 8              # just log all outgoing opcodes for 8s
(Run with the client focused/logged in. Nothing here is destructive; it presses keys.)
"""
import sys, os, time, ctypes
import frida
import nexus_agent as NA
from bot_input_test import find_windows, post_key

user32 = ctypes.WinDLL("user32", use_last_error=True)
WM_KEYDOWN, WM_KEYUP, WM_CHAR = 0x0100, 0x0101, 0x0102

# VK codes for the keys we may need. Letters = ord(upper). '?' = shift+'/'.
def vk_of(ch):
    if ch.isalpha():
        return ord(ch.upper()), ch.isupper()
    special = {"?": (0xBF, True), "/": (0xBF, False), " ": (0x20, False),
               "\r": (0x0D, False), "\x1b": (0x1B, False)}
    if ch in special:
        return special[ch]
    if ch.isdigit():
        return ord(ch), False
    return None, False

VK_SHIFT = 0x10


def press_char(hwnd, ch, hold=0.08):
    """Press a character key (with shift if needed) via PostMessage, and also post WM_CHAR
    since some UI paths read the typed char rather than the raw VK."""
    vk, shift = vk_of(ch)
    if vk is None:
        print(f"  (no VK for {ch!r})")
        return
    scan = user32.MapVirtualKeyW(vk, 0) & 0xFF
    lp_down = 1 | (scan << 16)
    lp_up = lp_down | (1 << 30) | (1 << 31)
    if shift:
        user32.PostMessageW(hwnd, WM_KEYDOWN, VK_SHIFT, 1)
    user32.PostMessageW(hwnd, WM_KEYDOWN, vk, lp_down)
    user32.PostMessageW(hwnd, WM_CHAR, ord(ch), lp_down)
    time.sleep(hold)
    user32.PostMessageW(hwnd, WM_KEYUP, vk, lp_up)
    if shift:
        user32.PostMessageW(hwnd, WM_KEYUP, VK_SHIFT, 1 | (1 << 30) | (1 << 31))


# ---- SendInput (hardware-level, foreground) path -- for UI that reads raw input ----
KEYEVENTF_KEYUP, KEYEVENTF_SCANCODE, KEYEVENTF_EXTENDED = 0x0002, 0x0008, 0x0001
from ctypes import wintypes


class KBD(ctypes.Structure):
    _fields_ = [("wVk", wintypes.WORD), ("wScan", wintypes.WORD),
                ("dwFlags", wintypes.DWORD), ("time", wintypes.DWORD),
                ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong))]


class _U(ctypes.Union):
    _fields_ = [("ki", KBD)]


class INP(ctypes.Structure):
    _fields_ = [("type", wintypes.DWORD), ("u", _U)]


def _si_key(vk, up):
    scan = user32.MapVirtualKeyW(vk, 0) & 0xFF
    flags = KEYEVENTF_SCANCODE | (KEYEVENTF_KEYUP if up else 0)
    return INP(type=1, u=_U(ki=KBD(0, scan, flags, 0, None)))


def send_char_si(ch, hold=0.08):
    """Press a character via SendInput (hardware scancode). Requires the target window
    FOREGROUNDED. Handles shift for uppercase/'?'."""
    vk, shift = vk_of(ch)
    if vk is None:
        return
    seq = []
    if shift:
        seq.append(_si_key(VK_SHIFT, False))
    seq.append(_si_key(vk, False))
    arr = (INP * len(seq))(*seq)
    user32.SendInput(len(seq), arr, ctypes.sizeof(INP))
    time.sleep(hold)
    seq2 = [_si_key(vk, True)]
    if shift:
        seq2.append(_si_key(VK_SHIFT, True))
    arr2 = (INP * len(seq2))(*seq2)
    user32.SendInput(len(seq2), arr2, ctypes.sizeof(INP))


JS = r"""
'use strict';
const MAIN = (Process.findModuleByName && Process.findModuleByName('__MOD__')) || Process.enumerateModules()[0];
// recv: forward opcode + a short decrypted body of every packet (to identify cast effects)
Interceptor.attach(MAIN.base.add(__RVA__), {
  onEnter(args){ this.out = args[2]; },
  onLeave(ret){ try{ let n=ret.toInt32(); if(n<=0) return; if(n>32) n=32;
    const b=new Uint8Array(this.out.readByteArray(n));
    send({t:'recv', op:b[0], ts:Date.now(),
          hex:Array.from(b).map(x=>('0'+x.toString(16)).slice(-2)).join(' ')}); }catch(e){} }
});
// send: forward opcode of every outgoing frame (to find the cast packet)
function scanOut(ptr, n){ try{ let o=0;
  while(o+4<=n){ if(ptr.add(o).readU8()===0xAA){
    const len=(ptr.add(o+1).readU8()<<8)|ptr.add(o+2).readU8();
    if(len<4||len>4096){o++;continue;}
    send({t:'out', op:ptr.add(o+3).readU8(), ts:Date.now()}); o+=1+len;
  } else o++; } }catch(e){} }
function hook(mod,name){ let a=null; try{const m=Process.findModuleByName(mod); if(m)a=m.findExportByName(name);}catch(e){}
  if(!a){try{a=Module.findExportByName(mod,name);}catch(e){}} if(!a)return;
  if(name==='WSASend'){ Interceptor.attach(a,{onEnter(args){try{const bufs=args[1],cnt=args[2].toInt32();
    for(let i=0;i<cnt;i++){const wb=bufs.add(i*8); scanOut(wb.add(4).readPointer(), wb.readU32());}}catch(e){}}});}
  else Interceptor.attach(a,{onEnter(args){scanOut(args[1],args[2].toInt32());}});
}
hook('ws2_32.dll','WSASend'); hook('ws2_32.dll','send');

rpc.exports = {
  scanstr: function(hexpat, cap){
    const out=[]; let rs; try{rs=Process.enumerateRanges('r--').concat(Process.enumerateRanges('rw-'));}catch(e){return out;}
    for(const r of rs){ let ms; try{ms=Memory.scanSync(r.base,r.size,hexpat);}catch(e){continue;}
      for(const m of ms){ out.push(m.address.toString()); if(out.length>=cap) return out; } }
    return out;
  },
  readhex: function(a,n){ try{const b=new Uint8Array(ptr(a).readByteArray(n));
    return Array.from(b).map(x=>('0'+x.toString(16)).slice(-2)).join(' ');}catch(e){return '';} }
};
""".replace("__MOD__", NA.MOD).replace("__RVA__", hex(NA.DEC_RVA))


def robust_cast(pr, hwnd, letter, tries=4, log=lambda *a: None):
    """Cast a spell reliably: Esc (clean state / close any chat) -> Shift+Z (cast prompt)
    -> letter -> Enter, then VERIFY the outgoing 0x0f cast frame fired. If it didn't (the
    Shift+Z was dropped and Enter may have popped the chat box), Esc to recover and retry.
    Returns True once a cast is confirmed on the wire."""
    for attempt in range(tries):
        press_char(hwnd, "\x1b", 0.06)          # Esc: close chat/prompt -> known clean state
        time.sleep(0.18)
        n0 = len(pr.out)
        press_char(hwnd, "Z", 0.08)             # Shift+Z (uppercase adds shift) opens prompt
        time.sleep(0.28)
        press_char(hwnd, letter, 0.08)          # a=Soothe b=Gateway c=Might d=Feral
        time.sleep(0.28)
        press_char(hwnd, "\r", 0.08)            # Enter confirms
        time.sleep(0.6)
        fired = any(op == 0x0f for _, op in pr.out[n0:])
        log(f"  attempt {attempt+1}: outgoing={['0x%02x'%o for _,o in pr.out[n0:]]} "
            f"cast_fired={fired}")
        if fired:
            return True
        press_char(hwnd, "\x1b", 0.06)          # recover: close whatever opened (e.g. chat)
        time.sleep(0.2)
    return False


def _utf16_read(b, off, maxch=24):
    out = []
    i = off
    while i + 1 < len(b) and len(out) < maxch:
        c = b[i] | (b[i + 1] << 8)
        if c == 0:
            break
        if 32 <= c < 127:
            out.append(chr(c))
        else:
            return ""
        i += 2
    return "".join(out)


def ascii_pat(s):
    return " ".join("%02x" % ord(c) for c in s)


def utf16_pat(s):
    return " ".join("%02x 00" % ord(c) for c in s)


class Probe:
    def __init__(self):
        dev = frida.get_local_device()
        pids = [p.pid for p in dev.enumerate_processes() if p.name.lower() == NA.MOD.lower()]
        if not pids:
            raise RuntimeError("client not running")
        self.sess = dev.attach(pids[0])
        self.sc = self.sess.create_script(JS)
        self.out = []      # (ts, op) outgoing
        self.recv = []     # (ts, op) incoming
        self.sc.on("message", self._msg)
        self.sc.load()
        self.ex = self.sc.exports_sync

    def _msg(self, m, d):
        if m.get("type") != "send":
            return
        p = m["payload"]
        if p.get("t") == "out":
            self.out.append((p["ts"], p["op"]))
        elif p.get("t") == "recv":
            self.recv.append((p["ts"], p["op"], p.get("hex", "")))

    def enum_spellbook(self):
        """Find a known spell record and walk the stride-0x148 array to list every spell
        with its two leading u32 fields (slot/id/cost?) -- reveals order + any hotkey index."""
        STRIDE = 0x148
        hits = self.ex.scanstr(utf16_pat("Soothe"), 4) or self.ex.scanstr(utf16_pat("Gateway"), 4)
        if not hits:
            print("  no spell record found to anchor the array")
            return
        # record base = name address - 8 (two u32 precede the UTF-16 name)
        anchor = int(hits[0], 16) - 8
        print(f"  spell array anchored at record {hex(anchor)} (stride {hex(STRIDE)}):")
        for k in range(-8, 24):
            a = anchor + k * STRIDE
            win = self.ex.readhex(hex(a), 40)
            if not win:
                continue
            b = [int(t, 16) for t in win.split()]
            f0 = b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24)
            f1 = b[4] | (b[5] << 8) | (b[6] << 16) | (b[7] << 24)
            name = _utf16_read(b, 8)
            if name and all(32 <= ord(c) < 127 for c in name):
                print(f"    [{k:+d}] {hex(a)}: f0={f0} f1={f1}  name={name!r}")

    def scan_spells(self):
        for name in ("Gateway", "Might", "Soothe", "Return", "Spell"):
            a = self.ex.scanstr(ascii_pat(name), 6)
            u = self.ex.scanstr(utf16_pat(name), 6)
            print(f"  {name:8}: ascii@{a}  utf16@{u}")
            for addr in (a[:1] + u[:1]):
                print(f"      ctx {addr}: {self.ex.readhex(hex(int(addr,16)-8), 64)}")


def main():
    args = sys.argv[1:]
    def opt(n, d=None):
        return args[args.index(n) + 1] if n in args else d
    wins = find_windows()
    if not wins:
        print("client window not found"); return
    hwnd = wins[0][0]
    pr = Probe()
    print("attached.")

    if "--esc" in args:
        print("pressing Esc to close any open chat/prompt ...")
        press_char(hwnd, "\x1b", 0.08)
        return

    if "--cast" in args:
        letter = opt("--cast", "c")
        ok = robust_cast(pr, hwnd, letter, log=print)
        print(f"  => cast {'SUCCEEDED' if ok else 'FAILED after retries'}")
        return

    if "--scan" in args:
        print("scanning for spell name strings ...")
        pr.scan_spells()
        return

    if "--spells" in args:
        print("enumerating the spellbook array ...")
        pr.enum_spellbook()
        return

    if "--watch" in args:
        secs = float(opt("--watch", "6"))
        print(f"watching for {secs}s -- cast your spells now (Soothe, then Might, then "
              f"Gateway), a couple seconds apart. Logging outgoing packets + effects:")
        t_start = time.time()
        seen_out, seen_recv = 0, 0
        AMBIENT = {0x1a}          # background chatter to ignore in the timeline
        while time.time() - t_start < secs:
            time.sleep(0.15)
            for ts, op in pr.out[seen_out:]:
                print(f"  +{time.time()-t_start:4.1f}s  >>> OUT  0x{op:02x} <<<")
            seen_out = len(pr.out)
            for rec in pr.recv[seen_recv:]:
                ts, op = rec[0], rec[1]
                hx = rec[2] if len(rec) > 2 else ""
                if op not in AMBIENT:
                    print(f"  +{time.time()-t_start:4.1f}s  recv 0x{op:02x}  {hx}")
            seen_recv = len(pr.recv)
        from collections import Counter
        print("summary out:", dict(Counter(op for _, op in pr.out)))
        return

    if "--keys" in args:
        keys = opt("--keys", "")
        hold = float(opt("--hold", "0.09"))
        gap = float(opt("--gap", "0.35"))
        si = "--si" in args
        if si:
            user32.SetForegroundWindow(hwnd)
            time.sleep(0.4)
        n0_out, n0_recv = len(pr.out), len(pr.recv)
        print(f"injecting keys {keys!r} ({'SendInput/fg' if si else 'PostMessage'}) ...")
        time.sleep(0.5)
        for ch in keys:
            t0 = len(pr.out)
            send_char_si(ch, hold) if si else press_char(hwnd, ch, hold)
            time.sleep(gap)
            new = [op for _, op in pr.out[t0:]]
            print(f"  key {ch!r} -> outgoing opcodes: {['0x%02x'%o for o in new]}")
        time.sleep(0.6)
        from collections import Counter
        print("  ALL new outgoing:", dict(Counter(op for _, op in pr.out[n0_out:])))
        print("  ALL new recv:", dict(Counter(r[1] for r in pr.recv[n0_recv:])))
        return

    print("nothing to do; use --scan, --watch N, or --keys \"...\"")


if __name__ == "__main__":
    main()
