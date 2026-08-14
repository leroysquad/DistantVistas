"""Scan a VintageHorizons LOD cache for sections whose palette colours are all zero.

Blob format v4: [1 byte version][deflate: uint16 paletteCount,
then per entry: 7-bit-length-prefixed UTF8 code, int32 colour, byte flags ...]
"""
import sqlite3, zlib, struct, sys, glob, collections

def read_7bit_string(buf, i):
    n = 0; shift = 0
    while True:
        b = buf[i]; i += 1
        n |= (b & 0x7F) << shift
        if not (b & 0x80):
            break
        shift += 7
    return buf[i:i + n].decode("utf8"), i + n

def palette_of(blob):
    if not blob or blob[0] != 4:
        return None
    raw = zlib.decompressobj(-zlib.MAX_WBITS).decompress(blob[1:])
    count = struct.unpack_from("<H", raw, 0)[0]
    i = 2
    entries = []
    for _ in range(count):
        code, i = read_7bit_string(raw, i)
        colour = struct.unpack_from("<i", raw, i)[0]; i += 4
        flags = raw[i]; i += 1
        entries.append((code, colour & 0xFFFFFFFF, flags))
    return entries

for path in sorted(glob.glob(sys.argv[1])):
    con = sqlite3.connect("file:" + path + "?mode=ro", uri=True)
    total = black = partial = 0
    zero_alpha = collections.Counter()
    for detail, sx, sz, data in con.execute("SELECT Detail,SX,SZ,Data FROM Section"):
        try:
            pal = palette_of(data)
        except Exception:
            continue
        if not pal:
            continue
        total += 1
        zeros = [e for e in pal if e[1] == 0]
        if zeros and len(zeros) == len(pal):
            black += 1
            if black <= 3:
                print(f"    all-black section L{detail} ({sx},{sz}): "
                      f"{[e[0] for e in pal][:4]}")
        elif zeros:
            partial += 1
            for e in zeros:
                zero_alpha[e[0]] += 1
    name = path.split("/")[-1]
    print(f"{name}: {total} parsed, {black} entirely black, {partial} partly")
    for code, n in zero_alpha.most_common(6):
        print(f"    zero-colour entry {code} x{n}")
