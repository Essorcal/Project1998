# Claude / Codex development workflow

Effective 2026-09-05. This is the current operating procedure for Caleb's development
checkouts. Older handoffs and agent memories are historical context. They do not allocate
clones, ports, or authority. Game architecture and provenance rules remain in AGENTS.md.

## Where current state lives

| Information | Authority |
|---|---|
| Procedure and review policy | This versioned document |
| Clone inventory and active assignments | `C:\Repo\Project1998\workflow\registry.json` |
| Issue scope and upstream status | The issue/PR and project board, checked before assignment |
| Work to perform and authority granted | Assignment packet under `C:\Repo\Project1998\briefs\` |
| Review evidence and final verdict | One `*.review.json` plus linked reports per PR under `reviews\` |
| Old sprint history | Existing handoffs and `briefs\clones-history-2026-09-05.md` |

The registry is local operational state, not a file to push to upstream. Back it up with
the briefs and reviews. All coordinators on this machine use the SAME registry. It is
not a distributed lock for multiple computers and is not an authentication boundary.
Owner values identify coordinator sessions; the assigned worker session is in its packet.
Neither a free-looking Git branch nor a quiet session is permission to take a claimed clone.

`C:\Repo\NexusTK` and ports 2000/2001/2005/2006 are reserved for Caleb's live checkout.
All other projects and clones live under `C:\Repo\Project1998`. The test-client coordinator
owns its client clones AND `NexusTK-tc1`/`NexusTK-tc2`. The server coordinator owns the
other server clones. The registry records this explicitly; team ownership is not inferred
from an agent's model name. Additions/ownership changes require a coordinated inventory
edit while no registry command is running; never recreate a registry to clear claims.

## Assign, prepare, dispatch

Always invoke the canonical scripts with absolute paths, even when working in an older
clone. This distributes fixes to operational tooling immediately without copying scripts
across worker branches. Python 3.9+ is required; `Workflow.ps1` finds `python` or the bundled
Codex runtime, or accepts an executable in `P1998_PYTHON`.

1. Check the board, open PRs, and file/behavior overlap with active work. Existing
   `Board-Claim.ps1` and `issue_overlap.py` remain useful; a text-based overlap report is
   evidence to inspect, not proof of independence. Preserve the existing authorization
   requirements for external board writes.
2. Write a packet using [the assignment template](../workflow/assignment-template.md).
   Use a unique ID such as `server-101-implementation-1`. Include exact clone, branch,
   base, worker session, allowed writes/pushes, acceptance criteria and report path.
3. Claim resources atomically. For test-client work needing a server, claim both clones
   in ONE command. Claims allow a dirty checkout so it can be reserved for remediation;
   preparation and dispatch still require a successful preflight. Never launch from a
   claim alone. If another coordinator owns the resource, choose a free one or wait.
4. Prepare the branch using `Prep-WorkerClone.ps1` below. It verifies the claim and refuses
   dirty work, wrong remotes, broken test-client dependencies and occupied assigned ports.
   New branch names cannot replace an existing local or origin branch. Dry runs neither
   fetch nor write guards; remote checks in a dry run use cached refs.
5. Re-run preflight with `--branch` after preparation. Confirm the prior agent/session is
   finished (the registry cannot discover every terminal's current directory). Dispatch
   the packet once. A fix round sent to an existing session still needs its active claim.
6. The worker reports to the packet's report path. The coordinator verifies the result,
   stops ONLY that assignment's server pair through the existing visible-console workflow,
   then releases with the report. Claims have no automatic expiry or forced takeover.
   Cancelled work also needs a brief cancellation report naming preserved branches/files.

```powershell
$wf = 'C:\Repo\NexusTK\Scripts\Workflow.ps1'
& $wf status
& $wf claim --id server-101-implementation-1 --resource NexusTK-codex --owner sprint7 --team server --packet C:\Repo\Project1998\briefs\101-codex.md --port-base 7000
& C:\Repo\NexusTK\Scripts\Prep-WorkerClone.ps1 -Clone C:\Repo\Project1998\NexusTK-codex -AssignmentId server-101-implementation-1 -Owner sprint7 -Branch pr/outbound-drain -GuardProfile None -DryRun
# After inspecting the dry run, repeat without -DryRun within the packet's push authorization.
& $wf preflight --resource NexusTK-codex --id server-101-implementation-1 --owner sprint7 --branch pr/outbound-drain
& $wf release --id server-101-implementation-1 --owner sprint7 --report C:\Repo\Project1998\briefs\reports\101-codex.md --outcome completed
```

For test-client branches pass `-Base origin/main`; server defaults to `upstream/master`.
Use `-Switch pr/existing` for fix rounds, and `-SetMode review` for Claude reviewers. `-SetMode`
alone (review, worker, off) flips the guard file and nothing else; it needs no claim or preflight,
which is what the xreview skill and the worker_guard hook's own advice rely on.
`-GuardProfile Claude` installs the existing local Claude hook; `None` is explicit for
Codex. A Claude hook is not a Codex enforcement mechanism. All agents follow the packet
and preflight rules. Preflight treats untracked files as dirty; preserve and classify them,
never reset or delete them just to satisfy a check.

Port claims reserve base, base+1, base+5, base+6 and reject overlap with other claims or live
listeners. The bind check releases the ports immediately; the launcher must still check
at start time. A stale registry lock reports its PID/host. Inspect that process and recover
the lock manually only when the writer is gone; no timed expiry can evict a live assignment.

## Review and merge evidence

| Risk | Examples | Required review and validation |
|---|---|---|
| Low | Paths, prose, simple tooling defaults | One independent focused review; relevant path/syntax checks |
| Normal | Ordinary logic/content changes | One independent review; behavior tests and relevant integration checks |
| High | Locks, shared state, persistence, wire framing, process lifecycle | Two independent sessions; failure-path/concurrency evidence and merged-tree build/tests |

Use capable reviewers selected for the change and prior results; do not infer coverage
from a model's reputation. Two sessions should form their initial findings independently.
The author never counts as a reviewer. Disagreements go into the verdict with evidence
and a resolution, rather than being settled by majority vote. Keep fixes with the author.

For important regression guards, demonstrate a negative control: the test fails when the
specific defect is reintroduced in a disposable checkout. Do not require this for ordinary
documentation or path edits. Constructor/signature changes and dependent/stacked work must
build the trial merge against current upstream even if Git reports no textual conflict.

Copy [review-template.json](../workflow/review-template.json) to `reviews\<repo>-<PR>.review.json`.
Record FULL head and base SHAs, author/session identities, individual reviewer verdicts,
selected validation commands/results, evidence paths, remaining blockers, round number,
and explicitly untested acceptance items. Every reviewer/check is stamped separately.

Immediately before merge, fetch upstream, verify PR head, and run:

```powershell
& C:\Repo\NexusTK\Scripts\Workflow.ps1 review-check --record C:\Repo\Project1998\reviews\server-101.review.json --checkout C:\Repo\Project1998\NexusTK-review --head origin/pr/outbound-drain --base upstream/master
```

The command refuses changed commits, dirty code, absent evidence, failed checks or missing
approvals. It validates a human/agent-attested record, not the truth of an arbitrary log.
It does not fetch, inspect GitHub checks, merge, or replace CI. A missing CI run is not green.
Check the current PR's checks and bind any merge action to its expected head; if upstream
changes after validation, repeat integration checks and have reviewers record whether the
new base affects their findings. Do not simply replace SHAs in an old approval.

For a stacked PR, record the parent relationship in the packet. Fix the parent with its
author, integrate it into the child, and rebuild the resulting merge tree. A textual merge
is insufficient evidence. Prefer merging a parent before beginning another dependent slice
when the work does not benefit enough from overlapping implementation.

After merge, set outcome to `merged` and keep the final record. Track escaped defects as
issue references (an empty list means none recorded, not a guarantee). At sprint close:

```powershell
& C:\Repo\NexusTK\Scripts\Workflow.ps1 metrics --records-dir C:\Repo\Project1998\reviews
```

This reports merged PR count, median review rounds and recorded escaped defects by risk.
Use those results to adjust review effort. Existing historical reports need not be backfilled.

## Communication and authority

Use [the handoff template](../workflow/handoff-template.md) for assignment, fix, review and
release handoffs. Reports go to shared files first; transport is secondary. A coordinator
may use an available task/agent messaging tool when Caleb has authorized that destination
and action. If no transport is available/authorized, provide ONE complete paste packet
to Caleb. Never claim a packet was delivered merely because its file exists.

Carry existing authorization forward verbatim. Ask only for missing authority or a decision
that changes scope; do not make Caleb reapprove already authorized branch pushes or local
checks. Record separate authority for fork pushes, PR publication, upstream merge and live
deployment. An assignment does not grant permission to stop Caleb's server or write another
worker's branch. Current-session user instructions take precedence over this procedure.

## Tooling changes and migration

Operational commands run from the canonical checkout; workers edit game code in their own
clones. Change shared tooling through a reviewed tooling change with its own failure-path
tests. Keep patches/commits available until the corresponding repository integrates them.
Never silently overwrite a worker's script or branch to distribute a fix.

The 2026-09-05 path corrections are documented in `C:\Repo\Project1998\workflow\ROLLOUT.md`.
Integrate the test-client portability fix into its main branch before preparing new clones
from that branch. The existing client clones retain their corrections; see the rollout notes for commits and remaining local edits.
The canonical `Test-Branch.ps1` already points to the nested layout; use its absolute path.
Preflight intentionally exposes uncommitted migration edits rather than hiding them.

Run offline tooling tests with `python Scripts/test_workflow.py`. They use temporary Git
repositories and sockets; they do not touch live state, fetch from GitHub or start servers.
