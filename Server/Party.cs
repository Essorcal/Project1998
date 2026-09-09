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
/// lock around the readers because the readers call INTO sessions (<c>NotifyGroup</c>, <c>WithState</c>) and
/// a lock held across those would be a second ordering to reason about against the session monitors and
/// <c>World._lock</c>.</para>
///
/// <para><b><see cref="_gate"/> is a LEAF lock.</b> It is held around the array copy and nothing else: no
/// call into a <c>Session</c>, no session monitor, no <c>World._lock</c>, no allocation that can run user
/// code. A thread holding it can therefore always finish, so it cannot participate in any cycle and it needs
/// no rank in the <c>Session.State.cs</c> ordering. Keep it that way — a single <c>m.Something()</c> inside
/// one of these <c>lock</c> blocks would make it an ordering question.</para>
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

    public Session Leader => Volatile.Read(ref _members)[0];
    public bool IsFull => Volatile.Read(ref _members).Length >= MaxMembers;

    public Party(Session leader, Session firstMember) => _members = new[] { leader, firstMember };

    /// <summary>Tell every current member something (RTK <c>clif_updategroup</c>'s minitext broadcast to the
    /// whole group) on the dedicated "group" minitext channel. ONE snapshot, read before the first send: a
    /// member who joins or leaves mid-broadcast is either told or not told, never told twice.</summary>
    public void Broadcast(string text)
    {
        foreach (var m in Volatile.Read(ref _members)) m.NotifyGroup(text);
    }

    public void Add(Session s)
    {
        lock (_gate)
        {
            var old = _members;
            var next = new Session[old.Length + 1];
            Array.Copy(old, next, old.Length);
            next[old.Length] = s;
            Volatile.Write(ref _members, next);
        }
    }

    /// <summary>Removes a member; returns true if the party is now down to a single straggler and should be
    /// disbanded (RTK <c>clif_leavegroup</c>: <c>group_count</c> reaching 0/1 dissolves it).</summary>
    public bool Remove(Session s)
    {
        lock (_gate)
        {
            var old = _members;
            int at = Array.IndexOf(old, s);
            if (at < 0) return old.Length <= 1;   // already gone; the disband rule still reads the same

            var next = new Session[old.Length - 1];
            Array.Copy(old, next, at);
            Array.Copy(old, at + 1, next, at, next.Length - at);
            Volatile.Write(ref _members, next);
            return next.Length <= 1;
        }
    }
}
