#!/usr/bin/env python
"""
Combat-mechanics socket tap for the LIVE NexusTK client (7.5.2.0).

Captures the raw encrypted wire stream (both directions) off the live client with
ZERO client-side reverse engineering, so we can decrypt it offline
(decode_capture.py) and mine combat data: your stats, damage pop-ups, mob HP
bars, hit/miss, exp gains.

IMPORTANT — NexusTK.exe is a LAUNCHER that spawns the real game as a CHILD
process. Plain `frida.spawn` hooks the launcher, which then launches the game
un-hooked (= zero capture). This script handles that three ways:
  * default (spawn): child-gating follows launcher -> game child automatically.
  * --attach       : instruments EVERY running NexusTK.exe (launcher + game).
  * --pid <N>      : attach to one specific pid (e.g. the one owning the socket).

It also runs a SOCKET-API CENSUS: counts calls to every candidate ws2_32 / IOCP
function every few seconds. If recv/send capture nothing but WSARecv or
GetQueuedCompletionStatus is ticking, the client uses overlapped/IOCP I/O and we
adjust. This tap is PASSIVE: read-only, no injection, no automation.

Output: appends one JSON object per socket chunk to re/combat_capture.jsonl
  {"ts": <ms>, "dir": "r"|"s", "fd": <sock>, "peer": "ip:port", "hex": "aa 00 .."}

Usage:
    python re/frida_combat_tap.py --attach          # game already running (recommended)
    python re/frida_combat_tap.py --pid 16612       # a specific process
    python re/frida_combat_tap.py                   # spawn launcher, follow child
Ctrl-C to stop, then:  python re/decode_capture.py --name "YourCharName"
"""
import sys, os, time, json, frida

EXE = r"C:\Program Files (x86)\KRU\NexusTK\NexusTK.exe"
MOD = "NexusTK.exe"
CAP = os.path.join(os.path.dirname(os.path.abspath(__file__)), "combat_capture.jsonl")

JS = r"""
'use strict';

function moduleByName(name) {
  if (typeof Process.findModuleByName === 'function') return Process.findModuleByName(name);
  return null;
}
function ensureModule(name) {
  let m = moduleByName(name);
  if (!m) { try { m = Module.load(name); } catch (e) {} }
  return m;
}
function findExport(moduleName, exportName) {
  const m = ensureModule(moduleName);
  if (m && typeof m.findExportByName === 'function') {
    const a = m.findExportByName(exportName);
    if (a) return a;
  }
  if (typeof Module.findGlobalExportByName === 'function') return Module.findGlobalExportByName(exportName);
  if (typeof Module.findExportByName === 'function') return Module.findExportByName(moduleName, exportName);
  return null;
}
function hex(ptr, n) {
  if (n <= 0) return '';
  if (n > 65535) n = 65535;
  try {
    return Array.from(new Uint8Array(ptr.readByteArray(n)))
      .map(b => ('0' + (b & 0xff).toString(16)).slice(-2)).join(' ');
  } catch (e) { return ''; }
}

const PID = Process.id;
const fdInfo = {};
const census = {};                       // fn name -> call count
function bump(name) { census[name] = (census[name] || 0) + 1; }

// ---- census: count calls to every candidate network function (diagnostic) ----
const CENSUS_FNS = [
  ['ws2_32.dll', 'recv'], ['ws2_32.dll', 'recvfrom'], ['ws2_32.dll', 'WSARecv'],
  ['ws2_32.dll', 'WSARecvFrom'], ['ws2_32.dll', 'send'], ['ws2_32.dll', 'sendto'],
  ['ws2_32.dll', 'WSASend'], ['ws2_32.dll', 'WSASendTo'], ['ws2_32.dll', 'connect'],
  ['ws2_32.dll', 'WSAConnect'], ['ws2_32.dll', 'closesocket'], ['ws2_32.dll', 'select'],
  ['mswsock.dll', 'WSARecvEx'], ['kernel32.dll', 'GetQueuedCompletionStatus'],
  ['kernel32.dll', 'GetQueuedCompletionStatusEx'],
];
CENSUS_FNS.forEach(function (pair) {
  const a = findExport(pair[0], pair[1]);
  if (!a) return;
  const label = pair[1];
  Interceptor.attach(a, { onEnter() { bump(label); } });
});
setInterval(function () {
  const nz = Object.keys(census).filter(k => census[k] > 0).map(k => k + '=' + census[k]);
  send({t: 'census', pid: PID, m: nz.length ? nz.join('  ') : '(no socket calls seen yet)'});
}, 3000);

// ---- connect: learn ip:port per fd ----
const cA = findExport('ws2_32.dll', 'connect');
if (cA) Interceptor.attach(cA, {
  onEnter(args) {
    try {
      const fd = args[0].toInt32();
      const sa = args[1];
      const port = (sa.add(2).readU8() << 8) | sa.add(3).readU8();
      const ip = [sa.add(4).readU8(), sa.add(5).readU8(), sa.add(6).readU8(), sa.add(7).readU8()].join('.');
      fdInfo[fd] = ip + ':' + port;
      send({t: 'info', m: 'pid ' + PID + ' connect fd=' + fd + ' -> ' + fdInfo[fd]});
    } catch (e) {}
  }
});

function emit(dir, fd, ptr, n) {
  if (n <= 0) return;
  send({t: 'pkt', dir: dir, fd: fd, peer: fdInfo[fd] || '?', ts: Date.now(), m: hex(ptr, n)});
}

// ---- blocking recv/send ----
const rA = findExport('ws2_32.dll', 'recv');
if (rA) Interceptor.attach(rA, {
  onEnter(args) { this.fd = args[0].toInt32(); this.buf = args[1]; },
  onLeave(ret) { emit('r', this.fd, this.buf, ret.toInt32()); }
});
const sA = findExport('ws2_32.dll', 'send');
if (sA) Interceptor.attach(sA, {
  onEnter(args) { emit('s', args[0].toInt32(), args[1], args[2].toInt32()); }
});

// ---- overlapped IOCP receive path (the live 7.x client's REAL recv path) -----
// The client posts WSARecv with a big empty buffer that completes LATER via
// GetQueuedCompletionStatus. Reading the buffer at WSARecv time gives garbage
// (empty 60000B blobs — the earlier bug). Instead: remember each WSARecv's buffer
// keyed by its OVERLAPPED pointer, and read the ACTUAL bytes at completion when the
// true transferred count is known. WSABUF (32-bit) = { u_long len; char* buf }.
function firstWsabuf(lpBuffers) {
  return { len: lpBuffers.readU32(), buf: lpBuffers.add(4).readPointer() };
}
const pendingRecv = {};                 // overlapped-ptr string -> {fd, buf, len, peer}

// WSARecv(s, lpBuffers, dwBufferCount, lpNumberOfBytesRecvd, lpFlags, lpOverlapped, cr)
const wrA = findExport('ws2_32.dll', 'WSARecv');
if (wrA) Interceptor.attach(wrA, {
  onEnter(args) {
    try {
      const fd = args[0].toInt32();
      const wb = firstWsabuf(args[1]);
      const ov = args[5];               // LPWSAOVERLAPPED
      if (ov.isNull()) {                // synchronous completion (rare): valid on leave
        this.syncFd = fd; this.syncBuf = wb.buf; this.syncLen = wb.len; this.lpN = args[3];
        return;
      }
      pendingRecv[ov.toString()] = { fd: fd, buf: wb.buf, len: wb.len, peer: fdInfo[fd] || '?' };
    } catch (e) {}
  },
  onLeave(ret) {
    try {
      if (this.syncBuf === undefined) return;   // async — handled at completion below
      // Only a return of 0 means it truly completed synchronously with data. A
      // SOCKET_ERROR (-1, would-block) leaves lpN stale (the empty 60000B blob bug).
      if (ret.toInt32() !== 0) return;
      const n = (this.lpN && !this.lpN.isNull()) ? this.lpN.readU32() : 0;
      if (n > 0 && n < this.syncLen) emit('r', this.syncFd, this.syncBuf, n);
    } catch (e) {}
  }
});

// GetQueuedCompletionStatusEx(port, lpEntries, ulCount, ulNumEntriesRemoved, ms, alertable):
// batched IOCP dequeue. Each OVERLAPPED_ENTRY (16 bytes on x86) =
//   { ULONG_PTR key; LPOVERLAPPED ov; ULONG_PTR Internal; DWORD bytesTransferred }.
const gqcsEx = findExport('kernel32.dll', 'GetQueuedCompletionStatusEx');
if (gqcsEx) Interceptor.attach(gqcsEx, {
  onEnter(args) { this.entries = args[1]; this.pRemoved = args[3]; },
  onLeave(ret) {
    try {
      if (ret.toInt32() === 0 || this.pRemoved.isNull()) return;
      const count = this.pRemoved.readU32();
      for (let i = 0; i < count && i < 256; i++) {
        const e = this.entries.add(i * 16);
        const ov = e.add(4).readPointer();
        const rec = pendingRecv[ov.toString()];
        if (!rec) continue;
        delete pendingRecv[ov.toString()];
        const n = e.add(12).readU32();
        if (n > 0) send({t: 'pkt', dir: 'r', fd: rec.fd, peer: rec.peer, ts: Date.now(),
                         m: hex(rec.buf, Math.min(n, rec.len))});
      }
    } catch (e) {}
  }
});

// GetQueuedCompletionStatus(port, lpNumBytesTransferred, lpKey, lpOverlapped, ms):
// the TRUE completion point. *lpNumBytesTransferred = real byte count, *lpOverlapped
// identifies which posted op finished — match it to a recorded WSARecv buffer.
const gqcs = findExport('kernel32.dll', 'GetQueuedCompletionStatus');
if (gqcs) Interceptor.attach(gqcs, {
  onEnter(args) { this.pN = args[1]; this.ppOv = args[3]; },
  onLeave(ret) {
    try {
      if (!this.ppOv || this.ppOv.isNull()) return;
      const ov = this.ppOv.readPointer();
      const rec = pendingRecv[ov.toString()];
      if (!rec) return;                 // a send/file completion, not our recv
      delete pendingRecv[ov.toString()];
      const n = this.pN.isNull() ? 0 : this.pN.readU32();
      if (n > 0) send({t: 'pkt', dir: 'r', fd: rec.fd, peer: rec.peer, ts: Date.now(),
                       m: hex(rec.buf, Math.min(n, rec.len))});
    } catch (e) {}
  }
});

// WSASend — the few sends that go through here (data valid at call time).
const wsA = findExport('ws2_32.dll', 'WSASend');
if (wsA) Interceptor.attach(wsA, {
  onEnter(args) {
    try { const wb = firstWsabuf(args[1]); emit('s', args[0].toInt32(), wb.buf, wb.len); } catch (e) {}
  }
});

send({t: 'info', m: 'tap installed in pid ' + PID});
"""


def main():
    dev = frida.get_local_device()
    capf = open(CAP, "w", encoding="utf-8", buffering=1)   # truncate each run for a clean decode
    counts = {"r": 0, "s": 0}
    sessions = []

    def on_message(msg, data):
        if msg["type"] == "error":
            print("[frida-error]", msg.get("description", str(msg)))
            return
        p = msg["payload"]
        t = p.get("t")
        if t == "info":
            print("[i]", p["m"])
        elif t == "census":
            print(f"[census pid {p['pid']}] {p['m']}")
        elif t == "pkt":
            capf.write(json.dumps({"ts": p["ts"], "dir": p["dir"], "fd": p["fd"],
                                   "peer": p["peer"], "hex": p["m"]}) + "\n")
            counts[p["dir"]] += 1
            total = counts["r"] + counts["s"]
            if total <= 10 or total % 20 == 0:
                arrow = "recv" if p["dir"] == "r" else "send"
                nbytes = len(p["m"].split())
                print(f"  [{arrow}] {nbytes}B peer={p['peer']}  (recv={counts['r']} send={counts['s']})")

    def instrument(pid, label=""):
        session = dev.attach(pid)
        try:
            session.enable_child_gating()          # follow any further children this proc spawns
        except Exception:
            pass
        script = session.create_script(JS)
        script.on("message", on_message)
        script.load()
        sessions.append(session)
        print(f"[+] instrumented pid {pid} {label}")
        return session

    def on_child(child):
        print(f"[child-added] pid {child.pid} (parent {child.parent_pid}) — following")
        try:
            instrument(child.pid, "(child)")
        except Exception as e:
            print("  child instrument failed:", e)
        finally:
            try: dev.resume(child.pid)
            except Exception: pass

    mode_pid = None
    for a in sys.argv[1:]:
        if a.startswith("--pid"):
            mode_pid = int(sys.argv[sys.argv.index(a) + 1]) if "=" not in a else int(a.split("=")[1])

    if mode_pid:
        instrument(mode_pid, "(--pid)")
    elif "--attach" in sys.argv:
        procs = [p for p in dev.enumerate_processes() if p.name.lower() == MOD.lower()]
        if not procs:
            print(f"no running {MOD} found — launch the game first, or omit --attach to spawn it")
            return
        print(f"found {len(procs)} {MOD} process(es): {[p.pid for p in procs]} — instrumenting all")
        for p in procs:
            try: instrument(p.pid, f"({p.name})")
            except Exception as e: print(f"  attach {p.pid} failed:", e)
    else:
        dev.on("child-added", on_child)
        pid = dev.spawn([EXE])
        print(f"[spawn] launcher pid {pid}")
        instrument(pid, "(launcher)")
        dev.resume(pid)

    print(f"\nappending capture to {CAP}")
    print("Play the game (attack one mob at a time). Ctrl-C when done.")
    print("Watch the [census] lines: they show which socket API the client actually uses.\n")
    try:
        sys.stdin.read()
    except KeyboardInterrupt:
        pass
    print(f"\nstopped. recv={counts['r']} send={counts['s']} frames captured -> {CAP}")
    capf.close()


if __name__ == "__main__":
    main()
