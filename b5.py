import io

P = 'Server/Commands.cs'
s = io.open(P, encoding='utf-8').read()

R = [
# @lvl was the last hand-rolled parse left in the table itself.
('''        T("lvl",     (s, a) => { var i = ParseInts(a); s.RespecLevel(i.Length > 0 ? i[0] : s._char.Level); },
                                                   "<1-99>",              "rebuild as level n: accurate stats + the matching spellbook"),''',
 '''        T("lvl",     (s, a) => s.RespecLevel(a.Int(0, s._char.Level)),
                                                   "<1-99>",              "rebuild as level n: accurate stats + the matching spellbook (bare @lvl rebuilds at the level you are)"),'''),

# The detail that used to live in a hand-written usage line, moved to the column that renders it.
('''        T("coins|gold", (s, a) => s.GiveCoinsCmd(a), "[n]",               "add coins to the purse"),''',
 '''        T("coins|gold", (s, a) => s.GiveCoinsCmd(a), "[n]",               "add coins to the purse (bare = +10,000; a negative n removes, floored at 0)"),'''),

('''        T("item",     (s, a) => s.GiveItemCmd(a),   "<name|id> [amount]", "summon an item into the bag"),
        T("take",     (s, a) => s.TakeItemCmd(a),   "<name|id> [amount|all]", "remove an item from the bag (worn gear untouched)"),''',
 '''        T("item",     (s, a) => s.GiveItemCmd(a),   "<name|id> [amount]", "summon an item into the bag (browse the registry with @items)"),
        T("take",     (s, a) => s.TakeItemCmd(a),   "<name|id> [amount|all]", "remove an item from the bag (worn gear untouched; browse with @items)"),'''),

('''        T("totem",   (s, a) => s.SetTotemCmd(a),    "<id>",               "set your totem crest (persists)"),''',
 '''        T("totem",   (s, a) => s.SetTotemCmd(a),    "<0-3>",              "set your totem crest — 0 JuJak, 1 Baekho, 2 HyunMoo, 3 ChungRyong (persists)"),'''),

('''        T("legend",  (s, a) => s.LegendCmd(a),      "[key] [0 | <icon> <color> <text...>]", "list legend marks with their internal keys; remove one, or (re)create one by key"),''',
 '''        T("legend",  (s, a) => s.LegendCmd(a),      "[key] [0 | <icon> <color> <text...>]", "list legend marks with their internal keys; remove one, or (re)create one by key (colour 128 is the usual white; 0 renders invisible)"),'''),

('''        T("text",    (s, a) => s.TextChannelCmd(a), "[type] [message]",    "send yourself one 0x0A line on a channel; bare @text sweeps them to compare panes/colours"),''',
 '''        T("text",    (s, a) => s.TextChannelCmd(a), "[0-255] [message]",   "send yourself one 0x0A line on a channel; bare @text sweeps them to compare panes/colours"),'''),

('''                                                                          "set vitals and stats directly (overrides the curve)"),''',
 '''                                                                          "set vitals and stats directly, overriding the curve — e.g. @stats 50000 50000 130"),'''),

('''        G("snd",      (s, a) => s.SoundProbe(a),    "<id>",      "play a raw client sound id"),''',
 '''        G("snd",      (s, a) => s.SoundProbe(a),    "<id> [id2 ...]", "play raw client sound ids, up to 8 at once (NexusTK.snd holds 001..197.wav)"),'''),

('''        G("efx",      (s, a) => s.EffectProbe(a),   "<id>",      "play a raw Effect.tbl animation over self"),
        G("mtx",      (s, a) => s.MiniTextProbe(a), "<type>",    "audition a raw SendMiniText channel"),''',
 '''        G("efx",      (s, a) => s.EffectProbe(a),   "<id> [id2 ...]", "play raw Effect.tbl animations over yourself, ids 0-127, up to 8 at once"),
        G("mtx",      (s, a) => s.MiniTextProbe(a), "<type> [text...]", "audition a raw SendMiniText channel (0 wisp, 3 mini/status, 5 system, 11 group, 12 clan)"),'''),

('''        G("weather",  (s, a) => s.WeatherProbe(a),  "clear|rain|snow | raw <n>", "force this map's weather"),''',
 '''        G("weather",  (s, a) => s.WeatherProbe(a),  "clear|rain|snow | auto | raw <0-255>", "pin this map's zone weather (auto releases it back to the season)"),'''),

('''        G("setting",  (s, a) => s.SettingCmd(a),    "[name] [on|off]", "read/set any 0x1b Options toggle"),''',
 '''        G("setting",  (s, a) => s.SettingCmd(a),    "[name] [on|off]", "read/set any 0x1b Options toggle (omit on|off to toggle; bare @setting lists them all)"),'''),

('''        G("hit",      (s, a) => s.HitProbe(a),          "[dmg]",   "0x13 over-head HP bar on the faced mob"),''',
 '''        G("hit",      (s, a) => s.HitProbe(a),          "<pct 0-100> [crit 0-255]", "0x13 over-head HP bar + hit animation on the faced mob"),'''),

('''        G("mailflag", (s, a) => s.MailFlagProbe(a),     "<off> [valHex]", "sweep the 0x08 mail/parcel notify byte"),''',
 '''        G("mailflag", (s, a) => s.MailFlagProbe(a),     "<off 0-79> [valHex]", "sweep the 0x08 mail/parcel notify byte (val defaults to 0x11 = mail+parcel; try offsets 40-57)"),'''),

('''                                                    "play a music track, or pick the soundtrack (no argument lists them)"),''',
 '''                                                    "play a music track, or pick the soundtrack (vol 0-255, default 100; no argument lists them)"),'''),
]

for old, new in R:
    assert s.count(old) == 1, 'MISS: ' + old[:100]
    s = s.replace(old, new)

io.open(P, 'w', encoding='utf-8', newline='').write(s)
print('table columns updated')
