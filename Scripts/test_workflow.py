"""Offline failure-path checks for assignment ownership and review freshness."""
from concurrent.futures import ThreadPoolExecutor
import copy
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import workflow as w


class WorkflowTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='p1998-workflow-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.repo = self.root / 'server-a'
        self.repo.mkdir()
        w.run('git', 'init', '-b', 'main', str(self.repo))
        w.git(self.repo, 'config', 'user.name', 'Workflow test')
        w.git(self.repo, 'config', 'user.email', 'workflow@example.invalid')
        (self.repo / 'Project1998.sln').write_text('fixture\n')
        w.git(self.repo, 'add', 'Project1998.sln')
        w.git(self.repo, 'commit', '-m', 'Fixture')
        self.head = w.git(self.repo, 'rev-parse', 'HEAD')
        w.git(self.repo, 'remote', 'add', 'origin', 'https://github.com/example/server.git')
        w.git(self.repo, 'update-ref', 'refs/remotes/origin/main', self.head)
        self.reg = self.root / 'registry.json'
        self.packet = self.root / 'packet.md'
        self.packet.write_text('A bounded test assignment.\n')
        self.report = self.root / 'report.md'
        self.report.write_text('Test evidence.\n')
        self.data = dict(schema=1, resources=[dict(name='server-a', path=str(self.repo),
            kind='server', team='server', reserved=False,
            origin='https://github.com/example/server.git', upstream=None)], assignments=[])
        w.write_json(self.reg, self.data)

    def claim(self, ident='a', owner='coordinator', base=None):
        w.claim(self.reg, ident, ['server-a'], owner, 'server', self.packet, base)

    def test_duplicate_claim_preserves_first_owner(self):
        self.claim()
        before = self.reg.read_bytes()
        with self.assertRaises(ValueError):
            self.claim('b', 'another-coordinator')
        self.assertEqual(before, self.reg.read_bytes())

    def test_concurrent_claim_has_one_winner(self):
        def attempt(n):
            try:
                self.claim(str(n))
                return True
            except ValueError:
                return False
        with ThreadPoolExecutor(max_workers=2) as pool:
            self.assertEqual(sum(pool.map(attempt, range(2))), 1)
        self.assertEqual(len(w.read_registry(self.reg)['assignments']), 1)

    def test_multi_resource_claim_is_all_or_nothing(self):
        with self.assertRaises(ValueError):
            w.claim(self.reg, 'a', ['server-a', 'missing'], 'owner', 'server', self.packet, None)
        self.assertEqual(w.read_registry(self.reg)['assignments'], [])

    def test_wrong_team_and_live_resource_refused(self):
        with self.assertRaises(ValueError):
            w.claim(self.reg, 'a', ['server-a'], 'owner', 'testclient', self.packet, None)
        self.data['resources'][0]['reserved'] = True
        w.write_json(self.reg, self.data)
        with self.assertRaises(ValueError):
            self.claim()

    def test_live_and_overlapping_port_plans_refused(self):
        with self.assertRaises(ValueError):
            self.claim(base=1995)  # base+5 collides with the live login port.
        self.data['assignments'].append(dict(id='other', owner='someone', status='active',
                                             resources=[], portBase=3005))
        w.write_json(self.reg, self.data)
        with self.assertRaises(ValueError):
            self.claim(base=3000)

    def test_occupied_port_refused(self):
        with socket.socket() as sock:
            sock.bind(('0.0.0.0', 0))
            port = sock.getsockname()[1]
            with self.assertRaises(ValueError):
                self.claim(base=port)

    def test_lock_never_auto_expires(self):
        with w.locked(self.reg):
            with self.assertRaises(ValueError):
                self.claim()
        self.assertFalse(Path(str(self.reg) + '.lock').exists())

    def test_owner_release_and_history(self):
        self.claim()
        with self.assertRaises(ValueError):
            w.release(self.reg, 'a', 'wrong', self.report, 'completed')
        w.release(self.reg, 'a', 'coordinator', self.report, 'completed')
        self.assertEqual(w.read_registry(self.reg)['assignments'][0]['status'], 'released')
        with self.assertRaises(ValueError):
            self.claim()  # IDs remain unique after release.

    def test_release_refuses_running_pair(self):
        self.claim()
        with patch.object(w, 'port_errors', return_value=['Port in use']):
            with self.assertRaises(ValueError):
                w.release(self.reg, 'a', 'coordinator', self.report, 'completed')
        self.assertEqual(w.read_registry(self.reg)['assignments'][0]['status'], 'active')

    def test_preflight_checks_owner_branch_and_exact_root(self):
        self.claim()
        w.preflight(self.reg, 'server-a', 'a', 'coordinator', 'main')
        for owner, branch, path in [('wrong', 'main', self.repo),
                                    ('coordinator', 'other', self.repo),
                                    ('coordinator', 'main', self.root)]:
            with self.assertRaises(ValueError):
                w.preflight(self.reg, 'server-a', 'a', owner, branch, checkout=path)

    def test_preflight_rejects_dirty_and_changed_remote(self):
        self.claim()
        (self.repo / 'unsaved.txt').write_text('keep this')
        with self.assertRaisesRegex(ValueError, 'uncommitted'):
            w.preflight(self.reg, 'server-a', 'a', 'coordinator', 'main')
        (self.repo / 'unsaved.txt').unlink()
        w.git(self.repo, 'remote', 'set-url', 'origin', 'https://github.com/wrong/repo')
        with self.assertRaisesRegex(ValueError, 'origin differs'):
            w.preflight(self.reg, 'server-a', 'a', 'coordinator', 'main')

    def review_record(self):
        return dict(author='author', head=self.head, base=self.head, risk='normal',
                    reviewers=[dict(identity='reviewer', verdict='approved', head=self.head,
                                    base=self.head, evidence=str(self.report))],
                    checks=[dict(command='fixture check', result='passed', head=self.head,
                                 base=self.head, evidence=str(self.report))],
                    blockingFindings=[], untestedAcceptance=[])

    def check_record(self, record):
        path = self.root / 'review.json'
        w.write_json(path, record)
        return w.review_check(path, self.repo, 'HEAD', 'origin/main')

    def test_review_requires_current_head_and_base(self):
        record = self.review_record()
        self.assertEqual(self.check_record(record)['status'], 'current')
        for key in ['head', 'base']:
            changed = copy.deepcopy(record)
            changed[key] = '0' * 40
            with self.assertRaisesRegex(ValueError, 'stale'):
                self.check_record(changed)

    def test_high_risk_needs_two_distinct_non_author_reviews(self):
        r = self.review_record()
        r['risk'] = 'high'
        r['reviewers'].append(copy.deepcopy(r['reviewers'][0]))
        with self.assertRaises(ValueError):
            self.check_record(r)
        r['reviewers'][1]['identity'] = 'author'
        with self.assertRaises(ValueError):
            self.check_record(r)
        r['reviewers'][1]['identity'] = 'second-reviewer'
        self.assertEqual(self.check_record(r)['status'], 'current')

    def test_evidence_and_acceptance_cannot_be_omitted(self):
        cases = [('checks', []), ('blockingFindings', ['unresolved']),
                 ('untestedAcceptance', ['human check pending'])]
        for key, value in cases:
            r = self.review_record()
            r[key] = value
            with self.assertRaises(ValueError):
                self.check_record(r)
        for key, value in [('head', '0' * 40), ('result', 'failed'), ('evidence', 'missing.md')]:
            r = self.review_record()
            r['checks'][0][key] = value
            with self.assertRaises(ValueError):
                self.check_record(r)

    def test_metrics_exclude_unmerged_work(self):
        for index, (risk, rounds, outcome, defects) in enumerate([
                ('low', 1, 'merged', []), ('low', 3, 'merged', ['repo#12']),
                ('high', 7, 'in-review', [])]):
            w.write_json(self.root / f'{index}.review.json', dict(
                risk=risk, round=rounds, outcome=outcome, escapedDefects=defects))
        self.assertEqual(w.metrics(self.root), {
            'low': dict(merged=2, medianReviewRounds=2, escapedDefects=1)})

    @unittest.skipUnless(shutil.which('pwsh') or shutil.which('powershell'), 'PowerShell unavailable')
    def test_prepare_creates_and_pushes_only_to_local_fixture(self):
        bare = self.root / 'origin.git'
        w.run('git', 'init', '--bare', str(bare))
        w.git(self.repo, 'remote', 'set-url', 'origin', str(bare))
        w.git(self.repo, 'push', '-u', 'origin', 'main')
        self.data['resources'][0]['origin'] = str(bare)
        w.write_json(self.reg, self.data)
        self.claim()
        shell = shutil.which('pwsh') or shutil.which('powershell')
        args = [shell, '-NoProfile', '-File', str(Path(__file__).with_name('Prep-WorkerClone.ps1')),
                '-Clone', str(self.repo), '-Registry', str(self.reg), '-AssignmentId', 'a',
                '-Owner', 'coordinator', '-Base', 'origin/main', '-GuardProfile', 'None',
                '-Branch', 'pr/prepared']
        result = subprocess.run(args, env=dict(os.environ, P1998_PYTHON=sys.executable),
                                capture_output=True, text=True, timeout=90)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(w.git(self.repo, 'branch', '--show-current'), 'pr/prepared')
        self.assertEqual(w.git(bare, 'rev-parse', 'refs/heads/pr/prepared'), self.head)
        self.assertEqual((self.repo / '.git' / 'guard-mode').read_text().strip(), 'worker')
        self.assertEqual(w.git(self.repo, 'status', '--porcelain'), '')
        self.assertEqual(w.read_registry(self.reg)['assignments'][0]['status'], 'active')

    @unittest.skipUnless(shutil.which('pwsh') or shutil.which('powershell'), 'PowerShell unavailable')
    def test_prepare_dry_run_is_read_only_and_existing_branch_is_refused(self):
        self.claim()
        shell = shutil.which('pwsh') or shutil.which('powershell')
        script = Path(__file__).with_name('Prep-WorkerClone.ps1')
        args = [shell, '-NoProfile', '-File', str(script), '-Clone', str(self.repo),
                '-Registry', str(self.reg), '-AssignmentId', 'a', '-Owner', 'coordinator',
                '-Base', 'origin/main', '-GuardProfile', 'None', '-DryRun']
        before = {str(p.relative_to(self.repo)): p.read_bytes()
                  for p in self.repo.rglob('*') if p.is_file()}
        env = dict(os.environ, P1998_PYTHON=sys.executable)
        result = subprocess.run(args + ['-Branch', 'pr/new'], env=env, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        after = {str(p.relative_to(self.repo)): p.read_bytes()
                 for p in self.repo.rglob('*') if p.is_file()}
        self.assertEqual(before, after)
        w.git(self.repo, 'branch', 'pr/existing')
        result = subprocess.run(args + ['-Branch', 'pr/existing'], env=env, capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('already exists', result.stdout)
        self.assertEqual(w.git(self.repo, 'rev-parse', 'pr/existing'), self.head)


if __name__ == '__main__':
    unittest.main(verbosity=2)
