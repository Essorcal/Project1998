namespace Server;

/// <summary>
/// A live party ("group" in RTK terms) — RTK's <c>groups[MAX_GROUPS][MAX_GROUP_MEMBERS]</c> table
/// (<c>rtk/src/map/clif.c</c>), but as a plain in-memory object here instead of a static array: transient,
/// never persisted, gone the moment every member logs off. RTK's own <c>MAX_GROUP_MEMBERS</c> is 256 — just
/// an arbitrary bound for that static array, not a real gameplay rule — so this uses NexusTK's actual
/// historical party cap instead. The leader is always <c>Members[0]</c>; leaving/kicking removes from the
/// list, which naturally promotes the next member (matches RTK's <c>clif_leavegroup</c> re-assigning
/// <c>group_leader = groups[groupid][0]</c>).
/// </summary>
public sealed class Party
{
    public const int MaxMembers = 6;

    private readonly List<Session> _members;
    public IReadOnlyList<Session> Members => _members;
    public Session Leader => _members[0];
    public bool IsFull => _members.Count >= MaxMembers;

    public Party(Session leader, Session firstMember) => _members = new List<Session> { leader, firstMember };

    /// <summary>Tell every current member something (RTK <c>clif_updategroup</c>'s minitext broadcast to the
    /// whole group) on the dedicated "group" minitext channel.</summary>
    public void Broadcast(string text) { foreach (var m in _members) m.NotifyGroup(text); }

    public void Add(Session s) => _members.Add(s);

    /// <summary>Removes a member; returns true if the party is now down to a single straggler and should be
    /// disbanded (RTK <c>clif_leavegroup</c>: <c>group_count</c> reaching 0/1 dissolves it).</summary>
    public bool Remove(Session s)
    {
        _members.Remove(s);
        return _members.Count <= 1;
    }
}
