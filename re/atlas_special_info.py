"""Mine the Nexus Atlas's per-item "Special Info" field, and rebuild ItemDef.BondedItemIds from it.

Bonded / break-on-death / unrepairable are three separate properties, and only two of them are columns
in Items.csv (ItmBoD, ItmRepairable). Bonding is not a column at all -- in the original it is a property
of the GRANT, so the same registry row is bound in one bag and loose in another. The Atlas is the only
surviving source that states it per item, in the same three-flag vocabulary the columns use ("bonded,
break on death, unrepairable and much more", says its own armor index page).

Each Atlas item is one `<table width="98%">`: a header cell carrying the name in <b>, the stat block,
then "Special Info:" and "Detailed Information:" as free text. This walks every item page in the mirror,
pulls those two fields, matches them to Items.csv by DISPLAY NAME, and prints:

  * how far the Atlas and the CSV agree on the two flags that ARE columns -- the calibration that earns
    the field its casting vote on the one that is not (501/521 on BoD, 504/521 on repairable);
  * the id list for Server/Content.cs, with each id's evidence;
  * every disagreement, so era drift and extraction misses stay visible instead of silently voting.

Precedence, highest first: the Atlas says "Non-Bonded" -> not bonded. It says "Bonded" -> bonded. It
prints a Special Info field naming OTHER flags but not bonding -> not bonded (this is what keeps the
Frozen spear and Giasomo stick out: their rows are plain break-on-death drops and only the copies Laptev
sells arrive owned -- a per-instance bond, stamped at the grant, not a property of the id). It prints
nothing at all -> fall back to RTK's grant sites, `player:addItem(key, n, dura, player.ID)`.

    python re/atlas_special_info.py            # the report
    python re/atlas_special_info.py --ids      # just the id list, for pasting into Content.cs
"""
import collections
import csv
import io
import re
import sys

from _paths import ARCHIVE, DATA, require

MIRROR = ARCHIVE / "artifacts" / "nexus_atlas_site" / "mirror"
ITEM_DIRS = ["weapons", "weapons1", "armor", "armor1", "items", "15Clothes", "kruna"]

# RTK grant sites: every `player:addItem(..., player.ID)` in rtklua, resolved to a 4.95 item id. Only
# consulted where the Atlas has no page at all -- it is a 7.x fork, so it is the weaker witness.
RTK_GRANTS = {
    1004, 1005, 118, 119, 29011, 47002, 124,
    26048, 26049, 26050, 26051, 26052, 26053, 26054, 26055,
    41008, 41009, 41010, 41011, 41508, 41509, 41510, 41511,
    *range(49001, 49025),
    *range(51002, 51027),
}


def _text(html: str) -> str:
    html = re.sub(r"<(script|style).*?</\1>", " ", html, flags=re.S | re.I)
    html = re.sub(r"<br\s*/?>", " \n", html, flags=re.I)
    html = re.sub(r"<[^>]+>", " ", html)
    for a, b in (("&nbsp;", " "), ("&amp;", "&"), ("&quot;", '"'), ("&#039;", "'")):
        html = html.replace(a, b)
    return re.sub(r"[ \t]+", " ", html)


_TAIL = r"(?:Detailed Info|How to Obtain|Casts|NPC Sells|Market Pri|Merchant sugg|$)"


def scrape():
    """name (lowercased) -> {'name', 'special': set, 'detail': set, 'pages': set}."""
    require(MIRROR, "the Nexus Atlas mirror", "P1998_ARCHIVE")
    out, pages = {}, 0
    for d in ITEM_DIRS:
        for path in sorted((MIRROR / d).glob("*.php")) if (MIRROR / d).is_dir() else []:
            pages += 1
            html = io.open(path, encoding="utf-8", errors="replace").read()
            for blk in re.split(r'(?=<table width="98%")', html):
                m = re.search(r'bgcolor="#B1300D".*?<b>\s*(.*?)\s*</b>', blk, flags=re.S | re.I)
                if not m:
                    continue
                name = re.sub(r"\s+", " ", re.sub(r"<[^>]+>", "", m.group(1))).strip()
                if not name:
                    continue
                t = _text(blk)
                si = re.search(r"Special Info\s*[:\-]?\s*(.*?)" + _TAIL, t, flags=re.S | re.I)
                det = re.search(r"Detailed Info(?:rmation)?\s*[:\-]?\s*(.*?)" + _TAIL, t, flags=re.S | re.I)
                si = re.sub(r"\s+", " ", si.group(1)).strip()[:200] if si else ""
                det = re.sub(r"\s+", " ", det.group(1)).strip()[:300] if det else ""
                if not si and not det:
                    continue
                rec = out.setdefault(name.lower(), {"name": name, "special": set(), "detail": set(), "pages": set()})
                if si:
                    rec["special"].add(si)
                if det:
                    rec["detail"].add(det)
                rec["pages"].add(f"{d}/{path.name}")
    return out, pages


def norm(s: str) -> str:
    return re.sub(r"[^a-z0-9]+", " ", s.lower().replace("’", "'").replace("\\'", "'")).strip()


def main():
    atlas, pages = scrape()
    by_name = {}
    for v in atlas.values():
        by_name.setdefault(norm(v["name"]), []).append(v)

    rows = list(csv.DictReader(io.open(DATA / "Items.csv", encoding="utf-8-sig")))

    def I(r, k):
        try:
            return int(r.get(k, "").strip())
        except ValueError:
            return 0

    bonded, evidence, disagree = {}, {}, []
    agree = collections.Counter()
    for r in rows:
        iid, key = I(r, "ItmId"), r["ItmIdentifier"].strip()
        hits = by_name.get(norm(r["ItmDescription"]), [])
        special = " | ".join(sorted({s for v in hits for s in v["special"]}))
        low = special.lower()

        if hits:
            a_bod = bool(re.search(r"breaks? on death", low))
            a_unrep = bool(re.search(r"(un|non-)repairable|not repairable|cannot be repaired", low))
            c_bod = I(r, "ItmBoD") != 0
            c_unrep = 3 <= I(r, "ItmType") <= 16 and I(r, "ItmRepairable") == 0
            agree["bod_ok" if a_bod == c_bod else "bod_no"] += 1
            agree["rep_ok" if a_unrep == c_unrep else "rep_no"] += 1
            if a_bod != c_bod:
                disagree.append(("BoD", key, f"atlas={a_bod} csv={c_bod}", special[:70]))

        neg = bool(re.search(r"(non[- ]?bonded|unbonded|not bonded)", low))
        pos = bool(re.search(r"bond", low)) and not neg
        # A field that says something -- "0" and "None" are the Atlas's way of writing "no flags".
        stated = bool(re.sub(r"[\s:|0]|none", "", low))

        if pos:
            bonded[iid] = key
            evidence[iid] = ("atlas", special[:80])
        elif neg or stated:
            pass                                  # the Atlas answered, and the answer is no
        elif iid in RTK_GRANTS:
            bonded[iid] = key
            evidence[iid] = ("rtk", "no Atlas page; RTK passes player.ID")

    print(f"pages {pages}   Atlas items {len(atlas)}   registry rows matched {sum(agree.values()) // 2}")
    print(f"  agreement on ItmBoD        : {agree['bod_ok']} / {agree['bod_ok'] + agree['bod_no']}")
    print(f"  agreement on ItmRepairable : {agree['rep_ok']} / {agree['rep_ok'] + agree['rep_no']}")
    print(f"  bonded: {len(bonded)}  (atlas {sum(1 for v in evidence.values() if v[0] == 'atlas')}"
          f" / rtk-only {sum(1 for v in evidence.values() if v[0] == 'rtk')})")

    if "--ids" in sys.argv:
        print()
        print(", ".join(str(i) for i in sorted(bonded)))
        return

    csv_bod = {I(r, "ItmId") for r in rows if I(r, "ItmBoD")}
    print(f"\n--- bonded AND break-on-death ({len(set(bonded) & csv_bod)}) ---")
    for i in sorted(set(bonded) & csv_bod):
        print(f"  {i:>6} {bonded[i]:<28} {evidence[i][1]}")

    print("\n--- bonded ---")
    for i in sorted(bonded):
        print(f"  {i:>6} {bonded[i]:<30} [{evidence[i][0]}] {evidence[i][1]}")

    print(f"\n--- break-on-death disagreements ({len(disagree)}: era drift + extraction misses) ---")
    for d in disagree:
        print(f"  {d[1]:<26} {d[2]:<24} {d[3]}")


if __name__ == "__main__":
    main()
