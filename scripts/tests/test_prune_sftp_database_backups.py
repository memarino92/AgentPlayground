import importlib.util
import sys
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "prune-sftp-database-backups.py"
SPEC = importlib.util.spec_from_file_location("prune_sftp_database_backups", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class BackupRetentionTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name)
        self.now = datetime(2026, 9, 29, 20, tzinfo=timezone.utc)
        self.sequence = 0

    def backup(self, timestamp):
        self.sequence += 1
        name = f"agentplayground-{timestamp:%Y-%m-%dT%H-%M-%SZ}-{self.sequence:032x}.dump"
        path = self.path / name
        path.write_bytes(b"synthetic dump")
        return path

    def test_keeps_daily_weekly_monthly_points_and_ignores_unrelated_files(self):
        daily_old = self.backup(self.now - timedelta(hours=2))
        daily_new = self.backup(self.now - timedelta(hours=1))
        yesterday = self.backup(self.now - timedelta(days=1))
        weekly_old = self.backup(self.now - timedelta(days=15, hours=2))
        weekly_new = self.backup(self.now - timedelta(days=15, hours=1))
        monthly = self.backup(self.now - timedelta(days=70))
        expired = self.backup(self.now - timedelta(days=400))
        partial = self.path / f"{expired.name}.partial"
        partial.write_bytes(b"incomplete")
        unrelated = self.path / "notes.txt"
        unrelated.write_text("keep")

        backups = MODULE.find_backups(self.path, self.now)
        kept = MODULE.retained_backups(backups, self.now)
        self.assertIn(daily_new, kept)
        self.assertIn(yesterday, kept)
        self.assertIn(weekly_new, kept)
        self.assertIn(monthly, kept)
        self.assertNotIn(daily_old, kept)
        self.assertNotIn(weekly_old, kept)
        self.assertNotIn(expired, kept)

        MODULE.prune(self.path, self.now, apply=False)
        self.assertTrue(daily_old.exists())
        self.assertTrue(expired.exists())
        MODULE.prune(self.path, self.now, apply=True)
        self.assertFalse(daily_old.exists())
        self.assertFalse(weekly_old.exists())
        self.assertFalse(expired.exists())
        self.assertTrue(partial.exists())
        self.assertTrue(unrelated.exists())

    def test_keeps_newest_backup_even_when_every_backup_is_old(self):
        oldest = self.backup(self.now - timedelta(days=500))
        newest = self.backup(self.now - timedelta(days=400))

        MODULE.prune(self.path, self.now, apply=True)

        self.assertFalse(oldest.exists())
        self.assertTrue(newest.exists())

    def test_monthly_window_keeps_previous_eleven_calendar_months(self):
        current = self.backup(self.now - timedelta(days=1))
        last_in_window = self.backup(datetime(2025, 10, 1, tzinfo=timezone.utc))
        outside_window = self.backup(datetime(2025, 9, 30, tzinfo=timezone.utc))

        MODULE.prune(self.path, self.now, apply=True)

        self.assertTrue(current.exists())
        self.assertTrue(last_in_window.exists())
        self.assertFalse(outside_window.exists())

    def test_ignores_future_dated_and_symlinked_files(self):
        valid = self.backup(self.now - timedelta(days=1))
        future = self.backup(self.now + timedelta(days=2))
        link = self.path / f"agentplayground-{self.now:%Y-%m-%dT%H-%M-%SZ}-{'f' * 32}.dump"
        try:
            link.symlink_to(valid)
        except OSError:
            link = None

        MODULE.prune(self.path, self.now, apply=True)

        self.assertTrue(valid.exists())
        self.assertTrue(future.exists())
        if link is not None:
            self.assertTrue(link.is_symlink())


if __name__ == "__main__":
    unittest.main()
