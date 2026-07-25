"""Extract one named entry from a Nexon-PAK .dat archive to stdout (bytes) or as hexdump/text preview."""
import sys
from pak_list import parse

if __name__ == "__main__":
    path, name = sys.argv[1], sys.argv[2]
    data, entries = parse(path)
    for off, ename, size in entries:
        if ename.lower() == name.lower():
            blob = data[off:off+size]
            out = sys.argv[3] if len(sys.argv) > 3 else None
            if out:
                open(out, "wb").write(blob)
                print(f"wrote {len(blob)} bytes to {out}")
            else:
                sys.stdout.buffer.write(blob)
            break
    else:
        print("not found:", name, file=sys.stderr)
        sys.exit(1)
