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

# ---- offsite (Cloudflare R2) --------------------------------------------------------------------------
# A backup that lives only on the machine being backed up is not a backup. It covers corruption and
# fat-fingers, and covers nothing at all when the host goes away — which on an Oracle Always Free tenancy is
# a routine event, not a disaster scenario: idle instances get reclaimed and accounts get suspended.
#
# R2 is deliberately a DIFFERENT vendor from the compute. Pushing to OCI Object Storage in the same tenancy
# would share the exact failure mode we are insuring against.
OFFSITE="${NEXUS_BACKUP_OFFSITE:-1}"
R2_REMOTE="${NEXUS_R2_REMOTE:-}"          # e.g. r2:nexus-backups
R2_KEEP_DAYS="${NEXUS_R2_KEEP_DAYS:-90}"  # kept longer than local: remote storage is cheap, and the case
                                          # it protects against is the one you notice late.

log() { printf '[backup] %s\n' "$*"; }

# Fail CLOSED if offsite is neither configured nor explicitly declined. Skipping silently when the variable
# happens to be unset is how a server ends up with local-only backups that everyone believes are offsite.
if [ "$OFFSITE" != "0" ] && [ -z "$R2_REMOTE" ]; then
  echo "[backup] !! NEXUS_R2_REMOTE is unset. Set it to the rclone remote (e.g. r2:nexus-backups)," >&2
  echo "[backup]    or set NEXUS_BACKUP_OFFSITE=0 to run local-only ON PURPOSE." >&2
  exit 1
fi
if [ "$OFFSITE" != "0" ] && ! command -v rclone >/dev/null 2>&1; then
  echo "[backup] !! rclone is not installed, but offsite backup is enabled." >&2
  exit 1
fi

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

# ---- push offsite -------------------------------------------------------------------------------------
# Before the local prune, so that a broken offsite path leaves MORE local copies rather than fewer. The
# database is a few hundred KB; there is no disk-pressure argument for pruning while the offsite copy is
# failing, and there is a very good argument against it.
#
# rclone verifies the hash after transfer by default, so a truncated or corrupted upload fails here rather
# than being discovered on the day it is needed.
if [ "$OFFSITE" != "0" ]; then
  GZ="$OUT.gz"
  log "pushing $(basename "$GZ") -> $R2_REMOTE"
  if ! rclone copyto "$GZ" "$R2_REMOTE/$(basename "$GZ")"; then
    echo "[backup] !! offsite push FAILED — local copy kept, NOT pruning this run" >&2
    exit 1
  fi
  log "offsite ok"

  # --include guards against a bucket that holds anything besides these snapshots: --min-age alone would
  # happily delete someone else's older objects if this remote is ever pointed at a shared bucket.
  rclone delete "$R2_REMOTE" --min-age "${R2_KEEP_DAYS}d" --include 'nexus-*.db.gz' \
    || log "!! offsite prune failed (the upload above still succeeded)"
else
  log "!! offsite push DISABLED by NEXUS_BACKUP_OFFSITE=0 — this host is the only copy"
fi

# Prune. Only runs after a VERIFIED backup this cycle, so a run of failures can never age out the last
# good copy — the exits above skip this entirely.
find "$DEST" -name 'nexus-*.db.gz' -mtime "+$KEEP_DAYS" -print -delete | while read -r old; do
  log "pruned $(basename "$old")"
done

log "done"
