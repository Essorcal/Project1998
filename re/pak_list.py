"""List (and optionally extract) entries from a Nexon-PAK-format .dat archive.
Format (confirmed via RE, see docs/4.x/Protocol.md #11a):
  u32 count
  count * { u32 offset; char name[13] }   (17 bytes/entry)
  first offset == header size (count*17 + 4)
Entry data spans [offset[i], offset[i+1]) ; last entry runs to EOF.
"""
import sys, struct, os

def parse(path):
    data = open(path, "rb").read()
    (count,) = struct.unpack_from("<I", data, 0)
    entries = []
    pos = 4
    for i in range(count):
        off, name = struct.unpack_from("<I13s", data, pos)
        name = name.split(b"\x00", 1)[0].decode("latin1", "replace")
        entries.append([off, name])
        pos += 17
    for i in range(count):
        start = entries[i][0]
        end = entries[i + 1][0] if i + 1 < count else len(data)
        entries[i].append(end - start)
    return data, entries

if __name__ == "__main__":
    path = sys.argv[1]
    data, entries = parse(path)
    print(f"{path}: {len(entries)} entries, file size {len(data)}")
    for off, name, size in entries:
        print(f"{off:10d} {size:10d}  {name}")
