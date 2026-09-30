#!/usr/bin/env python3
"""Prune AgentPlayground SFTP dumps using daily, weekly, and monthly retention.

Preview:
    python3 prune-sftp-database-backups.py /home/user/pa-backup

Apply (suitable for cron after the backup job):
    python3 prune-sftp-database-backups.py /home/user/pa-backup --apply

Only completed dumps named by PersonalAgent.Backup are considered. The newest
backup is always kept, even if it is older than the retention windows.
"""

from __future__ import annotations

import argparse
import os
import re
import stat
import sys
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path


BACKUP_NAME = re.compile(
    r"^agentplayground-(\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}Z)-[0-9a-f]{32}\.dump$"
)


@dataclass(frozen=True)
class Backup:
    path: Path
    timestamp: datetime
    identity: tuple[int, int, int, int]


def file_identity(info: os.stat_result) -> tuple[int, int, int, int]:
    return info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns


def find_backups(directory: Path, now: datetime) -> list[Backup]:
    backups = []
    with os.scandir(directory) as entries:
        for entry in entries:
            match = BACKUP_NAME.fullmatch(entry.name)
            if not match or not entry.is_file(follow_symlinks=False):
                continue
            try:
                timestamp = datetime.strptime(match.group(1), "%Y-%m-%dT%H-%M-%SZ").replace(tzinfo=timezone.utc)
                path = Path(entry.path)
                info = path.lstat()
            except (ValueError, FileNotFoundError):
                continue
            if not stat.S_ISREG(info.st_mode):
                continue
            # A clock error or manually named future file should not be pruned.
            if timestamp > now + timedelta(days=1):
                continue
            backups.append(Backup(path, timestamp, file_identity(info)))
    return sorted(backups, key=lambda backup: (backup.timestamp, backup.path.name))


def retained_backups(backups: list[Backup], now: datetime) -> set[Path]:
    if not backups:
        return set()

    keep = {backups[-1].path}
    seen_days = set()
    seen_weeks = set()
    seen_months = set()
    current_month = now.year * 12 + now.month

    for backup in reversed(backups):
        day_age = (now.date() - backup.timestamp.date()).days
        if 0 <= day_age < 7 and backup.timestamp.date() not in seen_days:
            seen_days.add(backup.timestamp.date())
            keep.add(backup.path)

        week = backup.timestamp.isocalendar()[:2]
        if now - timedelta(days=31) <= backup.timestamp <= now and week not in seen_weeks:
            seen_weeks.add(week)
            keep.add(backup.path)

        month_age = current_month - (backup.timestamp.year * 12 + backup.timestamp.month)
        month = backup.timestamp.year, backup.timestamp.month
        if 0 <= month_age < 12 and month not in seen_months:
            seen_months.add(month)
            keep.add(backup.path)

    return keep


def prune(directory: Path, now: datetime, apply: bool) -> tuple[int, int, int]:
    backups = find_backups(directory, now)
    keep = retained_backups(backups, now)
    removed = 0
    errors = 0

    for backup in backups:
        if backup.path in keep:
            continue
        if not apply:
            print(f"WOULD DELETE {backup.path.name}")
            continue
        try:
            info = backup.path.lstat()
            if not stat.S_ISREG(info.st_mode) or file_identity(info) != backup.identity:
                print(f"SKIPPED changed file: {backup.path.name}", file=sys.stderr)
                errors += 1
                continue
            backup.path.unlink()
            print(f"DELETED {backup.path.name}")
            removed += 1
        except OSError as error:
            print(f"FAILED {backup.path.name}: {error.strerror}", file=sys.stderr)
            errors += 1

    candidates = len(backups) - len(keep)
    print(f"Kept {len(keep)} backup(s); {'deleted' if apply else 'would delete'} {removed if apply else candidates}; errors {errors}.")
    return len(keep), removed if apply else candidates, errors


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Keep seven days of dailies, 31 days of weeklies, and twelve months of monthlies.",
        epilog="Preview: %(prog)s /home/user/pa-backup\nApply:   %(prog)s /home/user/pa-backup --apply",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("directory", type=Path, help="absolute directory containing uploaded database dumps")
    parser.add_argument("--apply", action="store_true", help="delete unretained backups (default: preview only)")
    args = parser.parse_args()
    if not args.directory.is_absolute() or not args.directory.is_dir():
        parser.error("directory must be an existing absolute path")
    try:
        _, _, errors = prune(args.directory, datetime.now(timezone.utc), args.apply)
    except OSError as error:
        print(f"Cannot scan backup directory: {error.strerror}", file=sys.stderr)
        return 1
    return 1 if errors else 0


if __name__ == "__main__":
    raise SystemExit(main())
