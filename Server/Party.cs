namespace Server;

/// <summary>
/// A live party ("group" in RTK terms) — RTK's <c>groups[MAX_GROUPS][MAX_GROUP_MEMBERS]</c> table
/// (<c>rtk/src/map/clif.c</c>), but as a plain in-memory object here instead of a static array: transient,
/// never persisted, gone the moment every member logs off. RTK's own <c>MAX_GROUP_MEMBERS</c> is 256 — just
/// an arbitrary bound for that static array, not a real gameplay rule — so this uses NexusTK's actual
/// historical party cap instead. The leader is always <c>Members[0]</c>; leaving/kicking removes from the
/// list, which naturally promotes the next member (matches RTK's <c>clif_leavegroup</c> re-assigning
/// <c>group_leader = groups[groupid][0]</c>).
///
/// <para><b>The membership is an IMMUTABLE SNAPSHOT, replaced whole under <see cref="_gate"/> (#167).</b>
/// It is written by whichever session leaves, kicks or invites and read from a different thread on every
/// group kill (<c>Session.AwardKillExp</c>'s eligibility scan, reached from the tick's pet and trap kills as
/// well as the killer's own handler), on group chat, on the roster text and in <see cref="Broadcast"/>.
/// As a <c>List&lt;Session&gt;</c> that was an unsynchronised collection: a kick or a disconnect landing
/// inside the killer's <c>foreach</c> threw <c>InvalidOperationException</c> out of it, and
/// <c>Session.Handle</c> logs and drops the packet — so that kill paid nobody. Copy-on-write rather than a
/// lock around the readers because the readers call INTO sessions — and, since the group notifications were
/// put under their owner's monitor, into those sessions' MONITORS (<c>NotifyGroup</c>, <c>WithState</c>) — so
/// a lock held across them would be a second ordering to reason about against the session monitors and
/// <c>World._lock</c>.</para>
///
/// <para><b><see cref="_gate"/> is a LEAF lock.</b> It is held around the array copy and the decisions that
/// have to be taken against the array it installs, and nothing else: no call into a <c>Session</c>, no
/// session monitor, no <c>World._lock</c>, no allocation that can run user code. A thread holding it can
/// therefore always finish, so it cannot participate in any cycle and it needs no rank in the
/// <c>Session.State.cs</c> ordering. Keep it that way — a single <c>m.Something()</c> inside one of these
/// <c>lock</c> blocks would make it an ordering question. <see cref="Broadcast"/> is the live example and is
/// deliberately NOT under the gate: it enters every member's monitor, so putting it there would hand the
/// leaf a rank and make it cycle against a leave, which takes the leaver's monitor and then the gate.</para>
///
/// <para><b>Seating a member and retiring the party are ONE decision, taken here (#167 review, F1).</b> A
/// removal takes only this gate — it needs neither the leaver's nor the inviter's monitor — so it can land
/// between an invite's <c>_party</c> read and its seat. When the disband a removal decided was simply acted
/// on afterwards, an invite landing in between grew the roster back to two and the disband then ran against
/// a live party: the invitee was seated next to a member who had just been told the group had disbanded and
/// had their own <c>_party</c> nulled. So <see cref="Add"/> refuses once the array is down to one (a disband
/// is pending) or no longer holds the inviter, and the disband itself is <see cref="TryDisband"/>, which
/// fires only while the array is still exactly the straggler. Whichever of the two reaches the gate first
/// wins and the other one gives up: the caller of a refused <c>Add</c> forms a new party instead, and the
/// caller of a refused <c>TryDisband</c> tells nobody anything.</para>
///
/// <para>An EMPTY array marks the party RETIRED. <see cref="TryDisband"/> installs it, <see cref="Add"/>
/// refuses one and <see cref="Remove"/> finds nothing in one, so a retired party can never come back, and no
/// member the roster still holds names a retired party — <c>RemoveFromParty</c> clears the last member's
/// field inside the same critical section that retires it. A KICK used to be the exception: it swapped the
/// member out on the leader's thread and only then waited for the member's monitor to clear their
/// <c>_party</c>, and in the descending-rank case rule 2 had dropped the leader's monitor for that wait, so
/// another member's leave could retire the party inside that gap (#167 review, F3). Since #198 the swap and
/// that field write are ONE critical section on the member — <see cref="Remove"/> is called from inside the
/// member's own <c>WithState</c> body — so the gap is closed and no reachable state has a member naming a
/// party the roster has already let them go from. The readers here still survive the EMPTY array, but as
/// defence rather than against a live window — <see cref="Leader"/> answers <c>null</c> for one rather than
/// indexing it, and the roster text treats that as "no party".</para>
/// </summary>
public sealed class Party
{
    public const int MaxMembers = 6;

    /// <summary>Serialises the writers against each other. Readers take nothing — see the class doc.</summary>
    private readonly object _gate = new();

    /// <summary>The current membership. Never mutated in place after publication; a writer builds a new array
    /// and swaps it in, so a reader that has grabbed a reference is holding a consistent, frozen roster.</summary>
    private Session[] _members;

    /// <summary>The membership as of RIGHT NOW. Every caller iterates the array it got, which no writer will
    /// ever touch again, so a kick or an invite mid-loop is invisible to it rather than fatal.</summary>
    public IReadOnlyList<Session> Members => Volatile.Read(ref _members);

    /// <summary>The leader — always <c>Members[0]</c> — or <c>null</c> when the array is empty, i.e. the
    /// party has been retired. Indexing it threw <c>IndexOutOfRangeException</c> out of the packet handler of
    /// a member a kick had swapped out but not yet cleared (#167 review F3); #198 closed that window (class
    /// doc), so the <c>null</c> is defensive now rather than a state a caller still reaches.</summary>
    public Session? Leader
    {
        get
        {
            var members = Volatile.Read(ref _members);
            return members.Length == 0 ? null : members[0];
        }
    }

    public bool IsFull => Volatile.Read(ref _members).Length >= MaxMembers;

    public Party(Session leader, Session firstMember) => _members = new[] { leader, firstMember };

    /// <summary>Tell every current member something (RTK <c>clif_updategroup</c>'s minitext broadcast to the
    /// whole group) on the dedicated "group" minitext channel. ONE snapshot, read before the first send: a
    /// member who joins or leaves mid-broadcast is either told or not told, never told twice.
    ///
    /// <para><b>This ENTERS each member's state monitor</b> — <c>Session.NotifyGroup</c> is wrapped at its own
    /// definition (#29 rule 2), because the send writes that member's <c>_gameInc</c> and this runs on the
    /// thread of whichever member invited, left, kicked or disconnected. One member at a time, released before
    /// the next, so it is a single nested acquisition per member and rule 2 resolves the ascending and the
    /// descending case exactly as it does for <c>WithStatePair</c> and <c>Session.RemoveFromParty</c>. It
    /// takes <see cref="_gate"/> at no point, and must not start: see the leaf-lock paragraph on the
    /// class.</para></summary>
    public void Broadcast(string text)
    {
        foreach (var m in Volatile.Read(ref _members)) m.NotifyGroup(text);
    }

    /// <summary>Seats <paramref name="s"/> on <paramref name="inviter"/>'s behalf, and returns whether it
    /// did. FALSE — nothing swapped — when this party is no longer the inviter's to seat anyone into: the
    /// array does not hold them any more (they were kicked, they left, they disconnected), or it is down to
    /// one member, which means a removal has already decided to disband it. See the class doc: the caller
    /// treats a refusal as "that party is gone" and forms a new one, which is the same thing it does for an
    /// inviter who had no party at all.</summary>
    public bool Add(Session inviter, Session s)
    {
        lock (_gate)
        {
            var old = _members;
            // Length < 2 is both cases at once: one member left is a disband on its way, none is a party
            // already retired.
            if (old.Length < 2 || Array.IndexOf(old, inviter) < 0) return false;

            var next = new Session[old.Length + 1];
            Array.Copy(old, next, old.Length);
            next[old.Length] = s;
            Volatile.Write(ref _members, next);
            return true;
        }
    }

    /// <summary>Retires the party on behalf of its last member — the other half of the decision
    /// <see cref="Add"/> takes. Succeeds, emptying the roster, only while the array is still exactly
    /// <paramref name="last"/>: if an invite has seated someone since the removal that produced the
    /// straggler, the party is alive again and this returns false, so the caller tells nobody it
    /// disbanded.</summary>
    public bool TryDisband(Session last)
    {
        lock (_gate)
        {
            var old = _members;
            if (old.Length != 1 || !ReferenceEquals(old[0], last)) return false;
            Volatile.Write(ref _members, Array.Empty<Session>());
            return true;
        }
    }

    /// <summary>Removes a member, and answers both halves of what the caller has to know: whether THIS call
    /// is the one that took them out, and who the last one standing is — non-null exactly when this removal
    /// is the one that left a single straggler, who is then told the group disbanded (RTK
    /// <c>clif_leavegroup</c>: <c>group_count</c> reaching 0/1 dissolves it). Same rule as before, same
    /// texts; what changed is who computes it. The straggler is a PROPOSAL, not a verdict: the caller acts
    /// on it through <see cref="TryDisband"/>, which re-takes the gate and only fires while the roster is
    /// still just that one member.
    ///
    /// <para><b>The straggler comes from the array this call installed</b>, inside the same critical section,
    /// not from a re-read by the caller afterwards. That is the difference when two members leave at once:
    /// with a bool and a <c>Members.Count == 1</c> re-read, the removal that left two members could see the
    /// OTHER removal's result and disband the same person a second time, and the two could disagree about who
    /// was last. A given member's departure is now reported to exactly one caller — the one whose swap took
    /// them out — and any straggler it leaves goes to that same caller and to nobody else.</para></summary>
    public (bool Removed, Session? Straggler) Remove(Session s)
    {
        lock (_gate)
        {
            var old = _members;
            int at = Array.IndexOf(old, s);
            // Already gone: a second, no-op removal of the same member (a leader's kick landing after that
            // member's own leave), or a party since retired. This call swapped nothing, so it left no
            // straggler either — the removal that DID take them out has already been handed one, and handing
            // the same survivor back again disbanded them twice (#167 review, F2).
            if (at < 0) return (false, null);

            var next = new Session[old.Length - 1];
            Array.Copy(old, next, at);
            Array.Copy(old, at + 1, next, at, next.Length - at);
            Volatile.Write(ref _members, next);
            return (true, next.Length == 1 ? next[0] : null);
        }
    }
}
