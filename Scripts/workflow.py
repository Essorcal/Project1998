#!/usr/bin/env python3
"""Local assignment coordination. Standard library only; never launches or stops servers.

Use Workflow.ps1 on Windows. State is local, while this tool and its templates are versioned.
"""
import argparse
from contextlib import contextmanager
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import socket
import statistics
import subprocess
import sys
import tempfile

SCHEMA = 1


def now():
    return datetime.now(timezone.utc).isoformat()


def run(*args):
    result = subprocess.run(args, text=True, encoding='utf-8', errors='replace',
                            capture_output=True, timeout=90)
    if result.returncode:
        raise ValueError((result.stderr or result.stdout).strip())
    return result.stdout.strip()


def git(path, *args):
    # An explicit, inventoried path, not a global safe.directory wildcard.
    return run('git', '--no-optional-locks', '-c', 'safe.directory=' + str(Path(path).resolve()),
               '-C', str(path), *args)


def repo_name(url):
    return url.strip().removesuffix('.git').replace('git@github.com:',
               'https://github.com/').rstrip('/').lower()


def read_registry(path):
    data = json.loads(Path(path).read_text(encoding='utf-8-sig'))
    if data.get('schema') != SCHEMA:
        raise ValueError('Unsupported registry schema')
    return data


def write_json(path, data):
    path = Path(path)
    # Same-directory replace makes each read see a complete old or new document.
    fd, tmp = tempfile.mkstemp(prefix=path.name + '.', suffix='.tmp', dir=path.parent)
    try:
        with os.fdopen(fd, 'w', encoding='utf-8', newline='\n') as stream:
            json.dump(data, stream, indent=2)
            stream.write('\n')
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(tmp, path)
    finally:
        if os.path.exists(tmp):
            os.unlink(tmp)


@contextmanager
def locked(path):
    lock = Path(str(path) + '.lock')
    try:
        fd = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    except FileExistsError:
        raise ValueError(f'Registry locked: {lock}. Retry after the writer finishes; '
                         'never remove a lock without checking its recorded process.')
    try:
        with os.fdopen(fd, 'w', encoding='utf-8') as stream:
            json.dump({'pid': os.getpid(), 'host': socket.gethostname(), 'since': now()}, stream)
        yield
    finally:
        lock.unlink()


def resource(data, name):
    matches = [r for r in data['resources'] if r['name'] == name]
    if len(matches) != 1:
        raise ValueError(f'Unknown or duplicate resource: {name}')
    return matches[0]


def assignment(data, ident):
    matches = [a for a in data['assignments'] if a['id'] == ident and a['status'] == 'active']
    if len(matches) != 1:
        raise ValueError(f'No active assignment: {ident}')
    return matches[0]


def ports(base):
    if base is None:
        return set()
    if not 1024 <= base <= 65000:
        raise ValueError('Port base must be 1024..65000')
    return {base, base + 1, base + 5, base + 6}


def port_errors(base):
    errors = []
    for port in sorted(ports(base)):
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
            try:
                # Windows' default bind can share ports; require exclusive access when available.
                if hasattr(socket, 'SO_EXCLUSIVEADDRUSE'):
                    sock.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
                sock.bind(('0.0.0.0', port))
            except OSError:
                errors.append(f'Port {port} is occupied or unavailable')
    return errors


def checkout_errors(r, expected_branch=None):
    errors = []
    path = Path(r['path'])
    try:
        if Path(git(path, 'rev-parse', '--show-toplevel')).resolve() != path.resolve():
            errors.append('Resource must name the checkout root')
        if git(path, 'status', '--porcelain'):
            errors.append('Checkout has uncommitted changes (including untracked files)')
        branch = git(path, 'branch', '--show-current')
        if expected_branch and branch != expected_branch:
            errors.append(f'Expected branch {expected_branch}, found {branch or "detached HEAD"}')
        for remote in ('origin', 'upstream'):
            if r.get(remote):
                actual = git(path, 'remote', 'get-url', remote)
                if repo_name(actual) != repo_name(r[remote]):
                    errors.append(f'{remote} differs from the inventory')
        if r['kind'] == 'testclient':
            project = path / 'TestClient' / 'TestClient.csproj'
            resolved = run('dotnet', 'msbuild', str(project), '-getProperty:P1998Repo')
            if not (Path(resolved) / 'Protocol.Tk495' / 'Protocol.Tk495.csproj').is_file():
                errors.append(f'Broken P1998Repo: {resolved}')
        if r['kind'] == 'server':
            if not (path / 'Project1998.sln').is_file():
                errors.append('Server solution missing')
    except (ValueError, OSError, subprocess.TimeoutExpired) as exc:
        errors.append(str(exc))
    return [f'{r["name"]}: {e}' for e in errors]


def init_registry(path, workspace, server):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with locked(path):
        if path.exists():
            raise ValueError('Registry already exists; refusing to replace assignments')
        resources = []
        for p in [Path(server), *sorted(Path(workspace).iterdir())]:
            if not (p / '.git').exists():
                continue
            name = p.name
            kind = 'server' if (p / 'Project1998.sln').exists() else (
                   'testclient' if (p / 'TestClient').exists() else 'support')
            team = 'testclient' if kind == 'testclient' or name in ('NexusTK-tc1', 'NexusTK-tc2') else 'server'
            remotes = git(p, 'remote').splitlines()
            resources.append(dict(name=name, path=str(p.resolve()), kind=kind, team=team,
                reserved=p.resolve() == Path(server).resolve(),
                origin=git(p, 'remote', 'get-url', 'origin'),
                upstream=git(p, 'remote', 'get-url', 'upstream') if 'upstream' in remotes else None))
        write_json(path, dict(schema=SCHEMA, created=now(), resources=resources, assignments=[]))


def claim(path, ident, names, owner, team, packet, base):
    packet = Path(packet).resolve()
    if not packet.is_file():
        raise ValueError('An existing assignment packet is required')
    if not names or len(set(names)) != len(names):
        raise ValueError('Specify each resource exactly once')
    with locked(path):
        data = read_registry(path)
        if any(a['id'] == ident for a in data['assignments']):
            raise ValueError('Assignment IDs cannot be reused, even after release')
        wanted = ports(base)
        if wanted & ports(2000):
            raise ValueError('Ports for Caleb\'s live pair are reserved')
        selected = [resource(data, n) for n in names]
        if base is not None and sum(r['kind'] == 'server' for r in selected) != 1:
            raise ValueError('A port allocation must include exactly one server checkout')
        for r in selected:
            if r['reserved'] or r['team'] != team:
                raise ValueError(f'Resource reserved or belongs to another team: {r["name"]}')
            if not Path(r['path']).is_dir():
                raise ValueError(f'Missing checkout: {r["path"]}')
        for a in data['assignments']:
            if a['status'] == 'active' and (set(names) & set(a['resources']) or wanted & ports(a['portBase'])):
                raise ValueError(f'Resources or ports already claimed by {a["id"]} ({a["owner"]})')
        errors = port_errors(base)
        if errors:
            raise ValueError('; '.join(errors))
        data['assignments'].append(dict(id=ident, resources=names, owner=owner, team=team,
            packet=str(packet), portBase=base, status='active', claimedAt=now(), releasedAt=None,
            report=None, outcome=None))
        write_json(path, data)


def release(path, ident, owner, report, outcome):
    report = Path(report).resolve()
    if not report.is_file():
        raise ValueError('An existing final/cancellation report is required')
    with locked(path):
        data = read_registry(path)
        a = assignment(data, ident)
        if a['owner'] != owner:
            raise ValueError('Owner does not match the assignment')
        errors = port_errors(a['portBase'])
        if errors:
            raise ValueError('Stop this assignment\'s server pair before release: ' + '; '.join(errors))
        a.update(status='released', releasedAt=now(), report=str(report), outcome=outcome)
        write_json(path, data)


def preflight(path, name, ident, owner, branch, check_ports=True, checkout=None):
    data = read_registry(path)
    r = resource(data, name)
    if checkout and Path(checkout).resolve() != Path(r['path']).resolve():
        raise ValueError('Requested checkout does not match the inventoried path')
    a = assignment(data, ident)
    if a['owner'] != owner or name not in a['resources']:
        raise ValueError('Assignment does not own this checkout')
    errors = checkout_errors(r, branch)
    if check_ports:
        errors += port_errors(a['portBase'])
    if errors:
        raise ValueError('\n'.join(errors))
    return r


def review_check(record, path, head, base):
    record = json.loads(Path(record).read_text(encoding='utf-8-sig'))
    actual_head = git(path, 'rev-parse', head + '^{commit}')
    actual_base = git(path, 'rev-parse', base + '^{commit}')
    if record['head'] != actual_head or record['base'] != actual_base:
        raise ValueError('Review is stale: head or base changed; record a new validation/review round')
    if git(path, 'status', '--porcelain'):
        raise ValueError('Checkout is dirty; evidence must refer to committed code')
    required = {'low': 1, 'normal': 1, 'high': 2}[record['risk']]
    reviewers = record['reviewers']
    if len({r['identity'] for r in reviewers if r['verdict'] == 'approved'
            and r['identity'] != record['author']}) < required:
        raise ValueError(f'{required} independent approval(s) required')
    if any(r['verdict'] != 'approved' for r in reviewers):
        raise ValueError('A reviewer has not approved this round')
    for item in reviewers + record['checks']:
        if item.get('head') != actual_head or item.get('base') != actual_base:
            raise ValueError('An individual review/check refers to different commits')
        evidence = Path(item['evidence'])
        # Evidence paths are deliberately absolute, like assignment packet paths.
        if not evidence.is_absolute() or not evidence.is_file():
            raise ValueError(f'Evidence file missing or not absolute: {evidence}')
    if not record['checks'] or any(c['result'] != 'passed' for c in record['checks']):
        raise ValueError('Every selected validation check must pass')
    if record['blockingFindings']:
        raise ValueError('Blocking findings remain')
    if record.get('untestedAcceptance'):
        raise ValueError('Acceptance items remain untested; resolve or explicitly rescope them before approval')
    return dict(head=actual_head, base=actual_base, risk=record['risk'], status='current')


def metrics(directory):
    """One final *.review.json per PR. Historical rounds belong in that record, not duplicate files."""
    groups = {}
    for path in sorted(Path(directory).glob('*.review.json')):
        r = json.loads(path.read_text(encoding='utf-8-sig'))
        if r.get('outcome') != 'merged':
            continue
        group = groups.setdefault(r['risk'], [])
        group.append(r)
    return {risk: dict(merged=len(items),
                      medianReviewRounds=statistics.median(r['round'] for r in items),
                      escapedDefects=sum(len(r.get('escapedDefects', [])) for r in items))
            for risk, items in groups.items()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--registry', default=str(Path(__file__).resolve().parents[2] /
                        'Project1998' / 'workflow' / 'registry.json'))
    sub = parser.add_subparsers(dest='command', required=True)
    p = sub.add_parser('init')
    p.add_argument('--workspace', required=True)
    p.add_argument('--server', required=True)
    sub.add_parser('status')
    p = sub.add_parser('claim')
    p.add_argument('--id', required=True)
    p.add_argument('--resource', action='append', required=True)
    p.add_argument('--owner', required=True)
    p.add_argument('--team', choices=['server', 'testclient'], required=True)
    p.add_argument('--packet', required=True)
    p.add_argument('--port-base', type=int)
    p = sub.add_parser('release')
    p.add_argument('--id', required=True)
    p.add_argument('--owner', required=True)
    p.add_argument('--report', required=True)
    p.add_argument('--outcome', choices=['completed', 'cancelled'], required=True)
    p = sub.add_parser('preflight')
    p.add_argument('--resource', required=True)
    p.add_argument('--id', required=True)
    p.add_argument('--owner', required=True)
    p.add_argument('--branch')
    p.add_argument('--checkout')
    p = sub.add_parser('review-check')
    p.add_argument('--record', required=True)
    p.add_argument('--checkout', required=True)
    p.add_argument('--head', default='HEAD')
    p.add_argument('--base', required=True)
    p = sub.add_parser('metrics')
    p.add_argument('--records-dir', required=True)
    args = parser.parse_args()
    try:
        if args.command == 'init':
            init_registry(args.registry, args.workspace, args.server)
        elif args.command == 'status':
            print(json.dumps(read_registry(args.registry), indent=2))
        elif args.command == 'claim':
            claim(args.registry, args.id, args.resource, args.owner, args.team, args.packet, args.port_base)
        elif args.command == 'release':
            release(args.registry, args.id, args.owner, args.report, args.outcome)
        elif args.command == 'preflight':
            r = preflight(args.registry, args.resource, args.id, args.owner, args.branch,
                          checkout=args.checkout)
            print(f'Ready: {r["path"]}')
        elif args.command == 'review-check':
            print(json.dumps(review_check(args.record, args.checkout, args.head, args.base), indent=2))
        elif args.command == 'metrics':
            print(json.dumps(metrics(args.records_dir), indent=2))
        return 0
    except (ValueError, KeyError, OSError, subprocess.TimeoutExpired) as exc:
        print(f'ERROR: {exc}', file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
