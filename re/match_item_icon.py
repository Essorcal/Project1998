"""Match an Atlas weapon .gif against the 4.95 client's Item.epf frames.

Same idea as re/match_npc_look.py: render every candidate frame, score it against the reference
image, print the ranked shortlist. Validate on KNOWN pairs in the same run (e.g. staffofdefense.gif
must rank staff_of_power's icon 60 first) so a good score means something.
"""
import struct, sys, os
from PIL import Image

RE = r'C:\Users\brian\Desktop\NexusServer\re'

def load_pal_blocks(path):
    d = open(path, 'rb').read()
    offs, i = [], 0
    while True:
        j = d.find(b'DLPalette', i)
        if j < 0:
            break
        offs.append(j)
        i = j + 1
    blocks = []
    for k, off in enumerate(offs):
        end = offs[k + 1] if k + 1 < len(offs) else len(d)
        blk = d[off:end]
        blocks.append([tuple(blk[38 + c * 4:38 + c * 4 + 3]) if 38 + c * 4 + 3 <= len(blk) else (0, 0, 0)
                       for c in range(256)])
    return blocks

def load_tbl_palettes(path):
    pal = {}
    for line in open(path, encoding='latin1'):
        if line.startswith('ID '):
            parts = line.strip().rstrip(',').split(', ')
            idn = int(parts[0].split(' ')[1])
            for p in parts[1:]:
                k, v = p.rsplit(' ', 1)
                if k.strip() == 'Palette':
                    pal[idn] = int(v)
    return pal

EPF = open(os.path.join(RE, 'Item.epf'), 'rb').read()
BLOCKS = load_pal_blocks(os.path.join(RE, 'Item.pal'))
TBL = load_tbl_palettes(os.path.join(RE, 'item.tbl'))

def frame_img(fi):
    """Item.epf frame -> RGBA image (index 0 = transparent), using render_items.py's corrected box rule."""
    fc, = struct.unpack_from('<H', EPF, 0)
    toc, = struct.unpack_from('<I', EPF, 8)
    if fi < 1 or fi >= fc:
        return None
    top, left, pix, sten, _, _ = struct.unpack_from('<hhIIhh', EPF, toc + fi * 16)
    _, _, _, _, pbot, pright = struct.unpack_from('<hhIIhh', EPF, toc + (fi - 1) * 16)
    w, h = left - pright, top - pbot
    if w <= 0 or h <= 0 or w * h != sten - pix:
        return None
    raw = EPF[12 + pix: 12 + pix + w * h]
    pal = BLOCKS[TBL.get(fi, 0) % len(BLOCKS)]
    im = Image.new('RGBA', (w, h), (0, 0, 0, 0))
    px = im.load()
    for i in range(min(len(raw), w * h)):
        k = raw[i]
        if k:
            px[i % w, i // w] = pal[k] + (255,)
    return im

def crop_content(im):
    bb = im.getbbox()
    return im.crop(bb) if bb else im

def prep(im, n=24):
    """Normalise for comparison: crop to content, scale to n x n, white-on-black alpha mask + RGB."""
    im = crop_content(im.convert('RGBA'))
    im = im.resize((n, n), Image.LANCZOS)
    rgb = Image.new('RGB', (n, n), (0, 0, 0))
    rgb.paste(im, (0, 0), im)
    return rgb, im.split()[3]

def score(ref, cand):
    """Lower is better: mean abs difference over RGB plus alpha-shape difference."""
    (r_rgb, r_a), (c_rgb, c_a) = ref, cand
    rp, cp = list(r_rgb.getdata()), list(c_rgb.getdata())
    d = sum(abs(a - b) for pa, pb in zip(rp, cp) for a, b in zip(pa, pb)) / (len(rp) * 3)
    ra, ca = list(r_a.getdata()), list(c_a.getdata())
    da = sum(abs(a - b) for a, b in zip(ra, ca)) / len(ra)
    return d + da

def best(giffile, topn=8):
    ref = prep(Image.open(giffile).convert('RGBA'))
    out = []
    for fi in range(1, 1310):
        im = frame_img(fi)
        if im is None or im.getbbox() is None:
            continue
        out.append((score(ref, prep(im)), fi))
    out.sort()
    return out[:topn]

if __name__ == '__main__':
    for g in sys.argv[1:]:
        path = os.path.join('atlasgif', g + '.gif')
        ranked = best(path)
        print(f'\n{g}.gif  ->  ' + ', '.join(f'{fi}({s:.1f})' for s, fi in ranked))
