#!/usr/bin/env bash
#
# Nightly backup of the one file that holds everything mutable: data/nexus.db — accounts, every character,
# board posts, mail, parcels, door state, the world clock, moderation. Losing it loses the server.
#
# Driven by nexus-backup.timer. Safe to run by hand at any time.

set -euo pipefail

ROOT="${NEXUS_ROOT:-/opt/nexus}"
DB="$ROOT/data/nexus.db"
DEST="${NEXUS_BACKUP_DIR:-/var/backups/nexus}"
KEEP_DAYS="${NEXUS_BACKUP_KEEP_DAYS:-30}"

log() { printf '[backup] %s\n' "$*"; }

[ -f "$DB" ] || { echo "[backup] no database at $DB" >&2; exit 1; }
mkdir -p "$DEST"

STAMP=$(date +%F-%H%M)
OUT="$DEST/nexus-$STAMP.db"

# sqlite3's OWN .backup, NOT cp. The database runs in WAL mode with both servers connected: `cp` can capture
# a main file whose committed data is still sitting in the -wal sidecar, producing a backup that restores to
# a torn or stale state. `.backup` uses the online backup API, which takes a consistent snapshot of a live
# database without stopping anything.
log "snapshotting $DB -> $OUT"
sqlite3 "$DB" ".backup '$OUT'"

# Verify before trusting it. A backup nobody has ever opened is a hope, not a backup — and a corrupt one is
# worse than none, because it stops you looking for another copy.
if ! sqlite3 "$OUT" "PRAGMA integrity_check;" | grep -q '^ok$'; then
  echo "[backup] !! integrity check FAILED on $OUT — keeping it for inspection, not pruning this run" >&2
  exit 1
fi

# Cheap sanity check on top of integrity_check: an intact but EMPTY database would pass the above.
CHARS=$(sqlite3 "$OUT" "SELECT COUNT(*) FROM characters;" 2>/dev/null || echo 0)
log "verified: $CHARS character(s)"

gzip -f "$OUT"
log "wrote $OUT.gz ($(du -h "$OUT.gz" | cut -f1))"

# Prune. Only runs after a VERIFIED backup this cycle, so a run of failures can never age out the last
# good copy — the exit above skips this entirely.
find "$DEST" -name 'nexus-*.db.gz' -mtime "+$KEEP_DAYS" -print -delete | while read -r old; do
  log "pruned $(basename "$old")"
done

log "done"
