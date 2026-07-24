#!/usr/bin/env python
# Decode captured 6.x server->client packets (NexonInc cipher, same as 4.95) to recover the
# stats (0x08) packet layout. Input = raw wire: AA 00 LEN OP INC <encrypted body>.
KEY = bytes.fromhex('4E65786F6E496E632E')  # "NexonInc."

def crypt(body, inc):
    o = bytearray(body)
    for i in range(len(o)):
        o[i] ^= KEY[i % 9]
        o[i] ^= (i // 9)
        if (i // 9) != inc:
            o[i] ^= inc
    return bytes(o)

def show(label, hexstr):
    b = bytes.fromhex(hexstr.replace(' ', ''))
    op, inc, body = b[3], b[4], b[5:]
    dec = crypt(body, inc)
    print(f'{label}: op=0x{op:02x} inc=0x{inc:02x} len={b[2]} bodylen={len(body)}')
    print('  hex  :', dec.hex(' '))
    print('  dec  :', ' '.join(str(x) for x in dec))
    print('  ascii:', ''.join(chr(x) if 32 <= x < 127 else '.' for x in dec))
    print()

show('BIG 0x08 full-stats',
     'AA 00 3C 08 0A 3C 6F 72 65 64 42 64 69 24 76 6E 73 64 44 41 66 6B 26 45 6D 70 04 B9 DD 39 '
     '6B 26 47 6C 71 66 67 73 67 6A 27 61 6B 76 61 61 47 60 6D 20 41 6A 77 60 61 46 61 6D 21 42 DA 49 63')
show('0x08 update A', 'AA 00 17 08 10 46 75 68 7F 7F 59 7E 73 3E 5F 74 69 7E 7F 58 7F 73 3F 5C C4 57')
show('0x08 update B', 'AA 00 17 08 11 47 74 69 7E 7E 58 7F 72 3F 5E 75 68 7F 7E 59 7E 72 3E 5D C5 56')
show('0x08 update C', 'AA 00 17 08 13 45 76 6B 7C 7C 5A 7D 70 3D 5C 77 6A 7D 7C 5B 7C 70 3C 5F C7 54')
show('0x11 (8)',      'AA 00 08 11 12 5C 77 76 9E 7C 5B')
show('0x1F (3)',      'AA 00 03 1F 08 46')
show('0x63 (5)',      'AA 00 05 63 09 45 6C 71')
show('0x58 (3)',      'AA 00 03 58 0B 45')
