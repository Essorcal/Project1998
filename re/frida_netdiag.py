#!/usr/bin/env python
"""One-shot: attach to every running NexusTK.exe, report arch + network modules +
whether socket exports resolve. Headless (no stdin wait). Diagnostic only."""
import sys, time, frida

MOD = "NexusTK.exe"
JS = r"""
'use strict';
const out = { pid: Process.id, arch: Process.arch, mods: [], exports: {} };
try {
  Process.enumerateModules().forEach(function (m) {
    if (/ws2|sock|mswsock|wininet|winhttp|ssl|crypt|net/i.test(m.name)) out.mods.push(m.name);
  });
} catch (e) { out.modErr = String(e); }
function res(dll, fn) {
  try {
    const m = Process.findModuleByName(dll);
    let a = m ? m.findExportByName(fn) : null;
    if (!a && typeof Module.findGlobalExportByName === 'function') a = Module.findGlobalExportByName(fn);
    return a ? a.toString() : null;
  } catch (e) { return 'ERR:' + e; }
}
[['ws2_32.dll','recv'],['ws2_32.dll','send'],['ws2_32.dll','WSARecv'],
 ['ws2_32.dll','WSASend'],['ws2_32.dll','connect'],['ws2_32.dll','closesocket'],
 ['mswsock.dll','WSARecvEx']].forEach(function (p) {
  out.exports[p[1]] = res(p[0], p[1]);
});
send(out);
"""

def main():
    dev = frida.get_local_device()
    procs = [p for p in dev.enumerate_processes() if p.name.lower() == MOD.lower()]
    if not procs:
        print("no NexusTK.exe running"); return
    print(f"found {len(procs)} process(es): {[p.pid for p in procs]}\n")
    for p in procs:
        got = {}
        try:
            s = dev.attach(p.pid)
            sc = s.create_script(JS)
            sc.on("message", lambda m, d: got.update(m.get("payload", {}) if m["type"] == "send" else {"error": m.get("description")}))
            sc.load(); time.sleep(0.6)
            print(f"=== pid {p.pid} ===")
            if "error" in got: print("  script error:", got["error"])
            print("  arch:", got.get("arch"))
            print("  net modules:", ", ".join(got.get("mods", [])) or "(none seen by frida)")
            for fn, addr in (got.get("exports") or {}).items():
                print(f"    {fn:12s} -> {addr}")
            s.detach()
        except Exception as e:
            print(f"=== pid {p.pid} === attach failed: {e}")
        print()

if __name__ == "__main__":
    main()
