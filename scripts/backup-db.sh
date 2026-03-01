#!/usr/bin/env bash
# Backup the SQLite database and Data Protection keys from the scheduler-data Docker volume.
#
# Usage:
#   ./scripts/backup-db.sh <backup-directory>
#
# Cron example (daily at 2:00 AM):
#   0 2 * * * /path/to/scripts/backup-db.sh /mnt/nas/backups/office-scheduler >> /var/log/office-scheduler-backup.log 2>&1

set -euo pipefail

BACKUP_DIR="${1:?Usage: $0 <backup-directory>}"
VOLUME_NAME="scheduler-data"
RETENTION_DAYS=30
DATE=$(date +%Y-%m-%d_%H%M%S)
BACKUP_FILE="officeScheduler-${DATE}.db"

# --- Backup database (safe online backup via sqlite3) ---
docker run --rm \
  -v "${VOLUME_NAME}":/data \
  -v "${BACKUP_DIR}":/backup \
  alpine:latest sh -c "
    apk add --no-cache sqlite > /dev/null 2>&1 &&
    sqlite3 /data/officeScheduler.db '.backup /backup/${BACKUP_FILE}'
  "

# --- Backup Data Protection keys ---
docker run --rm \
  -v "${VOLUME_NAME}":/data:ro \
  -v "${BACKUP_DIR}":/backup \
  alpine:latest sh -c "
    cp -r /data/keys /backup/keys-${DATE} 2>/dev/null || true
  "

# --- Rotate old backups ---
find "${BACKUP_DIR}" -name 'officeScheduler-*.db' -mtime +${RETENTION_DAYS} -delete
find "${BACKUP_DIR}" -name 'keys-*' -type d -mtime +${RETENTION_DAYS} -exec rm -rf {} + 2>/dev/null || true

echo "[$(date)] Backup complete: ${BACKUP_FILE}"
