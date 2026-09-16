namespace Server;

/// <summary>
/// One lookup-or-refuse gate for the "find this online player or tell me why not" sites (#57 finding 31).
///
/// <para><b>Why one method.</b> <c>_world.Online.FindPlayer(name)</c> is called at a couple of dozen sites.
/// Eleven of them are literally the same three statements — look the name up, send one line if it came back
/// null, carry on with the peer if it did not — and each had retyped its own copy, several with a refusal
/// string that had drifted into different wordings for the same condition. What made that worth collapsing is
/// not the line count: it is that every one of those sites also carries the SAME lock-discipline claim in a
/// comment ("Rule 1 holds: FindPlayer takes and releases World._lock inside itself"), and a claim repeated
/// eleven times is a claim nobody checks. Stated once, here, it is checkable once.</para>
///
/// <para>Six routed in the first pass (PR #238); #57 part 3 added <c>@where</c>, <c>@bring</c>, <c>@rez</c>,
/// <c>@click</c> and the party invite, each of which kept its own sentence and its own channel byte for
/// byte.</para>
///
/// <para><b>What is NOT unified.</b> The refusal TEXT and the CHANNEL stay the caller's, passed in. These are
/// RTK-sourced strings a player reads and, at the GM sites, the command-reply channel <c>Commands.cs</c>
/// insists on; folding them into one house wording would be a behaviour change dressed up as a dedup. The
/// three sites that had retyped the same sentence keep the sentence they were already sending, byte for byte.
/// Sites whose shape is not "refuse and return" — <c>Session.Dialog.cs</c>'s parcel notifier, which silently
/// no-ops when the recipient is offline, and the fiancé/spouse and mail-flag lookups that refuse nothing —
/// are left where they are: they have no refusal to parameterise.</para>
///
/// <para><b>Cost.</b> Unchanged: exactly one <c>FindPlayer</c> per call, as before. The per-online-player
/// <c>Snapshot()</c> that <c>FindPlayer</c> itself builds is issue #87 and is deliberately untouched here.</para>
/// </summary>
public sealed partial class Session
{
    /// <summary>Which channel a resolver refusal goes out on. One value per channel actually in use at a
    /// resolve site, so the set is the set — a new channel has to be added here to be reachable.
    /// <para><c>internal</c> rather than <c>private</c> only so <c>ResolveOnlinePlayerTests</c> can name the
    /// channel it is asserting on; nothing outside <c>Session</c> sends a refusal.</para></summary>
    internal enum RefuseChannel
    {
        /// <summary>The type-3 status pane (<see cref="SendMiniText"/>) — the spell/dialog flows.</summary>
        MiniText,
        /// <summary>The blue wisp channel (<see cref="SendBlueMessage"/>) — whisper's not-found line, which
        /// RTK sends on the same channel the whisper itself would have used.</summary>
        Blue,
        /// <summary>The chat log (<see cref="SendLog"/>) — the moderation commands.</summary>
        Log,
        /// <summary>The command-reply channel (<see cref="Refuse"/>) — a GM command's own refusal, which
        /// <c>Commands.cs</c> owns the routing rule for even though it resolves to the log today.</summary>
        CommandReply,
    }

    /// <summary>Resolve <paramref name="name"/> to an online session, or send <paramref name="refusal"/> on
    /// <paramref name="channel"/> and return null.
    ///
    /// <para><b>Lock discipline (#29 rule 1, Server/Session.State.cs).</b> <c>FindPlayer</c> takes and
    /// releases <c>World._lock</c> inside itself (World.OnlineRegistry.cs:45-53), so nothing world-scoped is
    /// held on return and this must not be called while holding <c>World._lock</c>. It takes no session
    /// monitor either — neither ours nor the peer's — so every caller enters its own critical section around
    /// the peer AFTER this returns, exactly as each of them already did. That is the whole rule, and it now
    /// lives in one place instead of six comments.</para></summary>
    internal Session? ResolveOnlinePlayer(string name, string refusal, RefuseChannel channel)
    {
        var target = _world.Online.FindPlayer(name);
        if (target is not null) return target;

        switch (channel)
        {
            case RefuseChannel.MiniText:     SendMiniText(refusal); break;
            case RefuseChannel.Blue:         SendBlueMessage(refusal); break;
            case RefuseChannel.CommandReply: Refuse(refusal); break;
            default:                         SendLog(refusal); break;
        }
        return null;
    }
}
