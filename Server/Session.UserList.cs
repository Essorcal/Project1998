using System.Text;
using Shared;

namespace Server;

// ─────────────────────────────────────────────────────────────────────────────────────────────────
//  The user list window (0x36) and the town/nation table (0x59 sub-kind 1) it depends on.
//
//  DECODED 2026-08-08 from the 4.95 client + RTK. The protocol doc used to say "0x36 — no RTK builder
//  exists, still undecoded"; that was wrong on both counts. RTK builds it in intif_parse_userlist
//  (map/intif.c:560) off a char-server query in mapif_parse_userlist (char/mapif.c:458), and the 4.95
//  client's parser 0x48a0c0 confirms every field. 4.95 DOES differ from RTK 7.x — see "row" below.
//
//  The full round trip, all three legs verified in the client:
//
//    1. player opens the window (modifier + 'W' — client 0x48e5cf: `cmp al,0x77` under modifier bit 2,
//       and menu-action 3 in the 8-entry table at 0x430914)
//    2. client 0x449ec0: IF its town table is empty, send `0x66 01 00 01 01 00 01 01 00`  -> we answer
//       0x59 sub-1 with the nation names. The window labels its columns from that table AND resolves
//       our own nation through it, so an empty table means an unlabelled, empty window.
//    3. client sends `0x18` with an EMPTY body                                            -> we answer 0x36.
//
//  0x36 body (client parse 0x48a0c0; the pointer it walks is packet-1, so its [+1] is our body[0]):
//
//      [0..1] u16BE  headline total   — drawn as (total - hiddenRows), NOT the row count
//      [2..3] u16BE  row count        — the loop bound
//      [4]    u8     initial sort     — 0 = by rank (tier asc, then rank u32 desc, comparator 0x48b340)
//                                       1 = by name (wcscmp, comparator 0x48b370).  RTK sends 1.
//                                       Only the INITIAL order: the window's own toggle re-sorts the
//                                       rows it already has (0x48ae85/0x48aec5) without asking us again,
//                                       which is why the 0x18 request carries no mode.
//      rows x count:
//      +0 u8   (nation << 4) | (path & 7)
//      +1 u8   (hidden << 4) | (classIcon & 0x0F)
//      +2 u8   name text colour (palette index — see UserListRowColor)
//      +3 u32BE rank value          <-- 4.95 ONLY. RTK 7.x has no such field; do not port its row verbatim.
//      +7 u8   (tier << 4) | (nameLen & 0x0F)
//      +8 name[nameLen]  ASCII, widened by MultiByteToWideChar
//
//  5.33 DIVERGES IN THE ROW — the header is byte-identical, the row is FOUR bytes + name, not eight
//  (parser 0x4d0020, row loop 0x4d041c; DECODED 2026-08-22 against 4.95's 0x48a48c line for line):
//
//      +0 u8   (nation << 4) | (path & 7)          — same byte, same meaning
//      +1 u8   (mark << 4) | (subpathIcon & 7)     — 4.95 has hidden<<4 | icon&0x0F here
//      +2 u8   name text colour                    — same byte, same meaning
//      +3 u8   nameLen                             — a WHOLE byte; no tier nibble sharing it
//      +4 name[nameLen]
//
//  Three fields moved or vanished, and each one is visible in the disassembly:
//
//    * the `hidden` nibble is GONE. 5.33 writes the struct's hidden slot as a literal 0 (0x4d0477
//      `mov byte [ebp-0x27e], 0`) and never reads it off the wire, so the "hidden rows" subtrahend on
//      the headline count can no longer be driven from the server. We always sent 0, so nothing is lost.
//    * the `rank` u32 is GONE. 5.33 SYNTHESISES it as `100000 - rowIndex` (0x4d046c) — so on 5.33
//      **wire order IS rank order**, and sort mode 0 means "mark ascending, then whatever order the
//      server sent" (comparator 0x4d1430: `(mark & 0xf)` asc, then the synthetic rank desc).
//      SendUserList therefore sorts by rank descending before building the body.
//    * the `mark` nibble moved from the LAST byte's high nibble into byte +1's high nibble, taking the
//      slot `hidden` used to occupy — both land on struct+0xC, which is what row-draw 0x4d11a0 indexes
//      the four "S" sprites with (valid 1..4).
//
//  Also note 5.33 masks the icon nibble with **7**, not 0x0F (0x4d044e `and al, 7`), and its row draw
//  only paints a badge for icon 1..4 AND path 1..4 — the sprite it picks is `I[(path-1)*4 + (icon-1)]`
//  out of the sixteen "I" sprites at listbox+0x1bc, i.e. exactly 4.95's relative-to-the-column banding.
//
//  How the break read on screen: sending the 4.95 row to 5.33 left the FIRST row's nation, column and
//  subpath badge correct (bytes +0/+1 are common), then its colour byte landed on +2 (also common), and
//  the rank u32's first byte — always 0 — became nameLen, so every name came out EMPTY and every later
//  row was parsed out of the previous row's tail. "Only the path/subpath icon works" is the exact
//  signature of a row that is right for two bytes and shears on the third.
//
//  Two client-side filters decide whether a row appears at all:
//    * hidden nibble != 0  -> the row is dropped entirely (and counted into the "hidden" subtrahend)
//      unless the local player has state byte [0x4fd400+0x1dc] == 1. We always send 0.
//    * nation nibble must equal the viewer's own nation for the row to land in one of the five path
//      columns. Every visible row still goes into the window's master list at +0x290, so cross-nation
//      players are carried but only your own nation fills the columns — which is exactly the
//      "compare across nations" behaviour, driven entirely by this one nibble.
//
//  Columns are keyed off the path bits: 1->col0, 2->col1, 3->col2, 4->col3, 0->col4 (five 0x6e-wide
//  columns starting at x=0x13). That is Warrior/Rogue/Mage/Poet + Peasant, i.e. exactly our PathId.
// ─────────────────────────────────────────────────────────────────────────────────────────────────
public sealed partial class Session
{
    // Client-imposed field widths. On 4.95 the name length shares a byte with the tier nibble, so 15
    // chars is a hard wire limit, not a style choice — a 16-char name would overflow into the tier.
    // 5.33 gives the length a whole byte, but its row record is a fixed-size list element with the wide
    // name inline at +0xE, so the cap stays: one number, and the shorter of the two is always safe.
    private const int UserListMaxName = 15;

    /// <summary>One user-list row, client-agnostic. <see cref="UserListBody"/> is what knows that 4.95
    /// spends eight bytes on this and 5.33 spends four.</summary>
    /// <param name="Nation">Must equal the VIEWER's nation or the client files the row nowhere.</param>
    /// <param name="Path">Column: 1..4 -> Warrior/Rogue/Mage/Poet, 0 -> Peasant (column 4).</param>
    /// <param name="Icon">Subpath badge, drawn relative to the column. 5.33 masks this to 3 bits.</param>
    /// <param name="Colour">Name text colour, a palette index — see <see cref="UserListRowColor"/>.</param>
    /// <param name="Rank">4.95 sorts on this u32; 5.33 has no such field and uses wire order instead.</param>
    /// <param name="Mark">Il-San/Ee-San/Sam-San/Sa-San badge, 1..4 (0 = none).</param>
    public readonly record struct UserListRow(byte Nation, byte Path, byte Icon, byte Colour,
                                              uint Rank, byte Mark, string Name);

    /// <summary>
    /// The 0x36 body for one client. Pure and static so Tests/ClientVersionWireTests can pin both shapes:
    /// the five-byte header is common, the ROW is not — see the file header for the disassembly this
    /// came from. <paramref name="headlineTotal"/> is the number the window prints above the columns;
    /// pass -1 for "same as the row count".
    /// </summary>
    public static byte[] UserListBody(ClientVersion ver, byte sortMode,
                                      IReadOnlyList<UserListRow> rows, int headlineTotal = -1)
    {
        bool v533 = ver == ClientVersion.V533;
        var d = new List<byte>();
        d.AddRange(Be((ushort)(headlineTotal < 0 ? rows.Count : headlineTotal)));
        d.AddRange(Be((ushort)rows.Count));
        d.Add(sortMode);
        foreach (var r in rows)
        {
            var name = Encoding.ASCII.GetBytes(r.Name);
            if (name.Length > UserListMaxName) name = name[..UserListMaxName];

            d.Add((byte)(((r.Nation & 0x0F) << 4) | (r.Path & 0x07)));
            if (v533)
            {
                // Mark took over the nibble 4.95 spends on `hidden`; the icon is masked to 3 bits there.
                d.Add((byte)(((r.Mark & 0x0F) << 4) | (r.Icon & 0x07)));
                d.Add(r.Colour);
                d.Add((byte)name.Length);        // whole byte — no tier nibble to share it with
            }
            else
            {
                d.Add((byte)(r.Icon & 0x0F));    // hidden nibble 0 = always visible (see header)
                d.Add(r.Colour);                 // NAME TEXT COLOUR — 0 paints black on black
                d.AddRange(Be32(r.Rank));        // 4.95 only; 5.33 synthesises this from wire order
                d.Add((byte)(((r.Mark & 0x0F) << 4) | name.Length));
            }
            d.AddRange(name);
        }
        return d.ToArray();
    }

    /// <summary>Client asked for the user list (recv <c>0x18</c>, empty body — RTK clif_parse case 0x18
    /// -> clif_user_list). Answers with the town table first so the window has nation names to label
    /// its columns with, then the roster.</summary>
    private void HandleUserListRequest()
    {
        SendTownList();
        SendUserList();
    }

    /// <summary>Client asked for the town/nation table (recv <c>0x66</c>, fixed body
    /// <c>01 00 01 01 00 01 01 00</c> — client 0x449ed0). Sent once per session: 0x449ec0 skips the
    /// request when its table already has entries.</summary>
    private void HandleTownListRequest(byte[] dec)
    {
        Log.Info($"   -> town-list request (0x66) body={Log.Hex(dec)}");
        SendTownList();
    }

    /// <summary>0x59 sub-kind 1 — the nation/town table (client parse 0x449f60, table [0x501d54],
    /// stride 0x48). The same 0x59 opcode whose sub-kind 0 is the item tooltip (§11c.1).</summary>
    private void SendTownList()
    {
        // Only the nations this server actually plays (Content.UserListNations, default Neutral/Koguryo/
        // Buya). Character.Nations stays the full 8 — that table is the HUD crest id space (0x08 stats,
        // calibrated with @nat) and trimming it would break unrelated things. This is just which nations
        // the user-list window gets columns and a name for.
        var ids = Content.UserListNations;
        var d = new List<byte> { 1, 0 };
        d.AddRange(Be((ushort)ids.Count));      // guard: the handler bails on <= 0 (signed test)
        d.Add((byte)ids.Count);                 // the count it actually loops on
        foreach (var id in ids)
        {
            var n = Encoding.ASCII.GetBytes(Character.NationName(id));
            d.Add(id);                          // nation id — matched against the row's nation nibble
            d.Add((byte)n.Length);
            d.AddRange(n);
        }
        SendMap(0x59, _gameInc++, d.ToArray(),
                $"town-list(0x59/1) {ids.Count} nations [{string.Join(",", ids.Select(i => $"{i}={Character.NationName(i)}"))}]");
    }

    /// <summary>0x36 — the user list window.</summary>
    private void SendUserList(byte sortMode = 1)
    {
        var players = _world.AllPlayers();
        var rows = new List<UserListRow>(players.Count);

        foreach (var p in players)
        {
            var c = p._char;
            // ONE path id decides both cells: PthType picks the column, PthIcon picks the subpath badge.
            // Using the raw PthId for either is wrong — Barbarian is id 10, which as column bits (&7 = 2)
            // would file a warrior under ROGUE and draw badge 10.
            int pid    = Math.Max(0, Content.PathIdForClass(c.ClassName));
            byte nation = (byte)(c.Nation & 0x0F);
            byte path   = (byte)(Content.PathBaseOf(pid) & 0x07);
            byte icon   = (byte)(Content.PathIconOf(pid) & 0x0F);
            byte tier   = (byte)(Math.Clamp((int)c.Mark, 0, 15) & 0x0F);

            rows.Add(new UserListRow(nation, path, icon, UserListRowColor(p), UserListRank(c), tier, c.Name));

            // Per-row, because "the packet looks right but the window is empty" is this window's whole
            // failure mode and only the DECODED fields say why. nation must equal the VIEWER's nation or
            // the row never reaches a column; path picks which column (0 lands in the 5th).
            Log.Info($"      row '{c.Name}' nation={nation}({Character.NationName(nation)}) path={path} " +
                     $"col={(path == 0 ? 4 : path - 1)} icon={icon} tier={tier} rank={UserListRank(c)} " +
                     $"class='{c.ClassName}'");
        }

        // 5.33 has no rank field: it numbers the rows itself (100000 - index) and the "by rank" sort is
        // mark ascending, then WIRE ORDER. So the ordering has to happen here or that sort means nothing
        // there. 4.95 re-sorts off its own rank u32 and does not care what order it arrived in, so this
        // is free for the client that already worked. OrderByDescending is stable, so equal levels keep
        // world order.
        var ordered = rows.OrderByDescending(r => r.Rank).ToList();

        Log.Info($"   -> viewer '{_char.Name}' nation={_char.Nation} — rows whose nation nibble differs " +
                 $"from {_char.Nation} are dropped from every column");
        SendMap(0x36, _gameInc++, UserListBody(_ver, sortMode, ordered),
                $"user-list(0x36) {players.Count} users sort={sortMode} " +
                $"{(IsV533 ? "5.33 4-byte rows" : "4.95 8-byte rows")}");
    }

    /// <summary>Build a 0x36 out of hand-made rows — the probe modes below all funnel through here so the
    /// prefix can never drift from <see cref="SendUserList"/>. Rows go out in the order given: on 5.33
    /// that IS the rank order, and a sweep wants its own order kept anyway.</summary>
    private void SendUserListRows(byte sortMode, IReadOnlyList<(byte nation, byte path, byte icon, byte hunter, uint rank, byte tier, string name)> rows, string label)
    {
        var body = UserListBody(_ver, sortMode,
            rows.Select(r => new UserListRow(r.nation, r.path, r.icon, r.hunter, r.rank, r.tier, r.name)).ToList());
        SendMap(0x36, _gameInc++, body, $"user-list(0x36) {label} {rows.Count} rows sort={sortMode}");
    }

    // The icon nibble indexes sixteen sprites each column listbox loads at construction (0x48af92: sixteen
    // 0x24-byte slots at listbox+0x1dc, from sprite category "I"), and the sweep across all five columns
    // showed the badge is drawn RELATIVE TO THE COLUMN — index 1 is Barbarian in the warrior column and
    // Diviner in the mage column. That is exactly Paths.csv's PthIcon, so nothing here needs a table of its
    // own: see Content.PathIconOf. (The window also loads four sprites from category "S" at +0x14c — the
    // column headers.)

    /// <summary>Row byte +2 — the NAME's text colour, MEASURED 2026-08-08 by sweeping it 0..15
    /// (`@users hunters`). It is a palette index and 0..15 is the standard 16-colour palette:
    /// <code>
    ///  0 black (invisible)   4 dark red    8 dark gray    12 red
    ///  1 dark blue           5 magenta     9 light blue   13 pink
    ///  2 dark green          6 brown      10 light green  14 yellow
    ///  3 teal                7 light gray 11 light cyan   15 white
    /// </code>
    /// Values above 15 index further into the client's 256-entry palette and are sparse (32 tan, 176 pale
    /// green, 192 off-white, 208 light orange, 224 blue-green), which is the space RTK's own numbers live
    /// in — its 143 white / 63 same-clan green / 47 GM red.
    /// <para><b>This byte is why names were invisible.</b> The doc called it "hunter" after RTK's
    /// <c>ChaHunter</c> column, we sent 0, and the client painted every name black on black — rows, badges
    /// and marks all drew, so it read as a broken name field rather than a colour. RTK's row is NOT aligned
    /// with 4.95's here: RTK has hunter at +2 and colour at +3, 4.95 has the colour at +2.</para>
    /// Same intent as RTK's Color Flag, in the palette we actually measured.</summary>
    private byte UserListRowColor(Session subject)
    {
        // Highest match wins. Each optional rule is DISABLED by its own tuning key being 0 — which is safe
        // to overload as "off" precisely because 0 is the invisible colour and can never be a real choice.
        // Only UserListColorDefault has no off switch.
        int self = Content.UserListColorSelf;
        if (self != 0 && ReferenceEquals(subject, this)) return (byte)self;

        // RTK reddens on the CLASS (classdb_path == 5, its Dreamweaver/Archon GM branch), not on an account
        // flag. We key on the account instead, which is the closer analogue for how this server works — the
        // visible difference is that a GM playing an ordinary Warrior is red here and wouldn't be in RTK.
        int gm = Content.UserListColorGm;
        if (gm != 0 && StaffAccounts.IsGm(subject._char.Name)) return (byte)gm;

        int clanInk = Content.UserListColorClan;
        string clan = subject._char.ClanName;
        if (clanInk != 0 && clan.Length > 0 && string.Equals(clan, _char.ClanName, StringComparison.OrdinalIgnoreCase))
            return (byte)clanInk;                                    // same clan as the VIEWER, per RTK

        return (byte)Content.UserListColorDefault;
    }

    /// <summary>"@users [sort]" — send the live roster (sort 0 = by rank, 1 = by name), and "@users sweep"
    /// to replace it with a synthetic roster that labels every cell with the value that produced it. The
    /// sweep is how the three unpinned fields (icon nibble, hunter byte, rank u32) get read off the screen
    /// instead of guessed — same method as @nat for the nation crest.</summary>
    private void UserListCmd(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string mode = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        byte sort = parts.Length > 1 && byte.TryParse(parts[1], out var m) ? m : (byte)1;

        SendTownList();     // always first — the window resolves your nation through this table

        switch (mode)
        {
            // One row per NATION id, all sharing our own path so the column can't be the variable.
            // Whichever rows appear tell us the viewer's EFFECTIVE nation — which is not necessarily
            // Character.Nation: the client looks our nation up in the town table and, when it doesn't
            // find it, silently falls back to table entry 0. That fallback is the single most likely
            // reason for an empty window.
            case "nations":
            {
                byte path = (byte)Math.Max(0, Content.PathIdForClass(_char.ClassName));
                var rows = Enumerable.Range(0, 8)
                    .Select(i => ((byte)i, path, (byte)path, ProbeInk, (uint)i, (byte)0, $"nation{i}"))
                    .ToList();
                SendUserListRows(sort, rows, "NATION-sweep");
                SendLog($"sent 8 rows, one per nation id, all path {path}. Your Character.Nation is " +
                        $"{_char.Nation} ({Character.NationName(_char.Nation)}) — whichever row(s) show up " +
                        $"are the nation the CLIENT thinks you are.");
                return;
            }

            // One row per PATH, all on our own nation, so we can see which column each path lands in and
            // whether column 4 (path 0) renders differently from the rest.
            case "paths":
            {
                byte nation = (byte)(_char.Nation & 0x0F);
                var rows = Enumerable.Range(0, 5)
                    .Select(i => (nation, (byte)i, (byte)i, ProbeInk, (uint)i, (byte)0, $"path{i}"))
                    .ToList();
                SendUserListRows(sort, rows, "PATH-sweep");
                SendLog($"sent 5 rows, one per path 0-4, all nation {nation}. path 1-4 -> columns 0-3, " +
                        $"path 0 -> column 4.");
                return;
            }

            // One row per MARK value, in every column so the badge can be compared across paths.
            case "tiers":
            case "marks":
            {
                byte nation = (byte)(_char.Nation & 0x0F);
                var rows = EveryColumn((p, letter) => Enumerable.Range(0, 16)
                    .Select(i => (nation, p, (byte)1, ProbeInk, (uint)(15 - i), (byte)i, $"{letter}mark{i}")));
                SendUserListRows(sort, rows, "MARK-sweep");
                SendLog("mark 0-15 in every column. Known: 0 none, 1 Il-San, 2 Ee-San, 3 Sam-San, 4 Sa-San." +
                        (IsV533 ? " 5.33 draws a badge only for 1-4 (row draw 0x4d11a0)." : ""));
                return;
            }

            // The colour byte itself (row +2). Kept because 0..15 is only the low end of a 256-entry
            // palette — `@users colors 16` walks the sparse upper range RTK's own 143/63/47 live in.
            case "hunters":
            case "colors":
            case "colours":
            {
                byte nation = (byte)(_char.Nation & 0x0F);
                int step = parts.Length > 1 && int.TryParse(parts[1], out var st) && st > 0 ? st : 1;
                var rows = EveryColumn((p, letter) => Enumerable.Range(0, 16)
                    .Select(i => (nation, p, (byte)1, (byte)Math.Min(255, i * step), (uint)(15 - i), (byte)0,
                                  $"{letter}c{Math.Min(255, i * step):D3}")));
                SendUserListRows(1, rows, $"COLOUR-sweep step={step}");
                SendLog($"colour = 0,{step},{step * 2}..{Math.Min(255, 15 * step)} in every column. " +
                        "0 is BLACK (invisible) — that's what hid every name until it was measured.");
                return;
            }

            case "sweep":
            case "icons":
                SendUserListSweep(sort);
                return;

            default:
                SendUserList(parts.Length > 0 && byte.TryParse(parts[0], out var s) ? s : (byte)1);
                return;
        }
    }

    /// <summary>A readable text colour for every probe row. The colour byte is row +2 and <b>0 is black</b>
    /// — the probes all shipped 0 before it was measured, which is why they showed badges but no labels.</summary>
    private static byte ProbeInk => (byte)Content.UserListColorDefault;

    /// <summary>The five columns in wire order, with the letter each probe prefixes its row names with.
    /// path 1..4 are columns 0..3 (Warrior/Rogue/Mage/Poet) and path 0 is column 4 (Peasant).</summary>
    private static readonly (byte Path, char Letter)[] Columns =
        { ((byte)1, 'W'), ((byte)2, 'R'), ((byte)3, 'M'), ((byte)4, 'P'), ((byte)0, 'E') };

    /// <summary>Run a probe's row generator once per column, so a sweep fills the whole window instead of
    /// stacking into whichever single path we happened to pick. Rows are name-prefixed with the column
    /// letter, which also keeps them grouped under the name sort.</summary>
    private static List<(byte, byte, byte, byte, uint, byte, string)> EveryColumn(
        Func<byte, char, IEnumerable<(byte, byte, byte, byte, uint, byte, string)>> gen) =>
        Columns.SelectMany(c => gen(c.Path, c.Letter)).ToList();

    /// <summary>The icon sweep: all sixteen badge indices, <b>in every column</b>. The bank is loaded per
    /// column listbox (sixteen sprites from category "I" at +0x1dc), so running it in one column only ever
    /// showed one column's worth — and since the badges that came back were warrior subpaths, it looked
    /// like the bank itself was warrior-only. Filling all five settles whether index N draws the same
    /// sprite everywhere or is interpreted relative to the column's path.</summary>
    private void SendUserListSweep(byte sortMode)
    {
        byte nation = (byte)(_char.Nation & 0x0F);
        var rows = EveryColumn((p, letter) => Enumerable.Range(0, 16)
            .Select(i => (nation, p, (byte)i, ProbeInk, (uint)(15 - i), (byte)0, $"{letter}i{i:D2}")));
        SendUserListRows(sortMode, rows, "ICON-sweep all columns");
        SendLog("icon 0-15 in ALL five columns, names '<W|R|M|P|E>i00'..'i15'. If W i05 and M i05 draw the " +
                "same sprite the bank is global; if they differ it is relative to the column's path." +
                (IsV533 ? " On 5.33 the icon nibble is masked to 3 bits and only 1-4 draw, so i08-i15 " +
                          "repeat i00-i07 and the peasant column never badges at all." : ""));
    }

    // The u32 the "by rank" comparator sorts DESCENDING. Level is the honest value: it is what RTK's
    // ORDER BY leads with and the only reading that stays correct if the client draws this cell as a
    // number. RTK's post-99 tiebreak (SUM(mana*2 + vita)) would have to be folded into this same u32 to
    // survive the client-side re-sort, which would wreck it as a display value — so that stays out
    // until we know whether the cell is drawn. See @users.
    //
    // (An earlier guess that RTK's colour byte was folded into this u32's top byte was WRONG — the colour
    // sweep changed nothing, and the colour turned out to be row byte +2. This is just the level.)
    private static uint UserListRank(Character c) => c.Level;
}
