using System.Collections.Generic;

namespace Stellar.MultiTool;

// The ONE "Players" roster behind the auto-accept UI: every person with a per-person Party switch and Social switch
// (main window rows + the per-player dialog both edit through here).
//
// Membership is STORED, not derived from the switches: a person is listed IFF they are in _invPartyUids (Party on) ∪
// _invIntUids (Social on) ∪ _invRosterUids (listed with BOTH off). The first two are config invite_party_uids /
// invite_interaction_uids — the same uid-sets InviteAutoAccept consumes, so the Lua side is untouched; the third is
// config invite_roster_uids and never reaches Lua. Invariant: roster-only ⇔ both switches off.
//   • Turning a switch ON adds the uid to that system list and drops it from roster-only.
//   • Turning the LAST switch off keeps the person (moved to roster-only) — the row stays put. Remove-by-toggle was
//     tried and rejected as bad UX (a row vanishing under the cursor); only the main window's explicit ✕ / "Clear
//     all" delete anyone.
//   • Picking a person (card "Auto-Accept" button, Add-player Choose / nearby click) lists them straight away via
//     EnsureInviteListed — roster-only (both off) if new, untouched if already listed — before the dialog opens.
//
// The displayed roster (_invRosterView) is the ordered union party → social-only → roster-only, deduped, rebuilt on
// every change via PushWantedNames (every save path funnels through it) — so the per-frame row lambdas index a cached
// list instead of re-unioning three lists per row per frame.
public sealed partial class Plugin
{
    private List<string> _invRosterUids = new();         // listed with both switches off (invite_roster_uids)
    private List<string> _invRosterView = new();         // derived: what the Players list shows, in order
    private int _invRosterFirst;                         // VirtualList window start (OnWindow)

    private bool InParty(string uid)  => _invPartyUids.Contains(uid);
    private bool InSocial(string uid) => _invIntUids.Contains(uid);
    private bool IsListed(string uid) => InParty(uid) || InSocial(uid) || _invRosterUids.Contains(uid);

    // Load-time cleanup: enforce the invariant (roster-only ⇔ both off) and dedupe, so a hand-edited or stale config
    // can't list someone twice. In memory only — the next save of any section persists the cleaned list.
    private void NormalizeRosterOnly()
    {
        var seen = new HashSet<string>();
        _invRosterUids.RemoveAll(u => string.IsNullOrEmpty(u) || InParty(u) || InSocial(u) || !seen.Add(u));
    }

    private void RebuildRosterView()
    {
        var seen = new HashSet<string>();
        var view = new List<string>(_invPartyUids.Count + _invIntUids.Count + _invRosterUids.Count);
        foreach (var u in _invPartyUids)  if (seen.Add(u)) view.Add(u);
        foreach (var u in _invIntUids)    if (seen.Add(u)) view.Add(u);
        foreach (var u in _invRosterUids) if (seen.Add(u)) view.Add(u);
        _invRosterView = view;
    }

    // Row index → uid (null past the end — e.g. a pool slot whose row just dropped off the shrinking list).
    private string? RosterAt(int idx) => (idx >= 0 && idx < _invRosterView.Count) ? _invRosterView[idx] : null;

    // Flip one person's Party (social=false) or Social (social=true) switch and save immediately. Shared by the
    // main-window row toggles and the per-player dialog so the two views can't drift. Moves the uid between its
    // system list and roster-only as needed, but never drops it from the roster.
    private void SetInviteSwitch(string uid, bool social, bool on)
    {
        if (string.IsNullOrEmpty(uid)) return;
        var list = social ? _invIntUids : _invPartyUids;
        if (on == list.Contains(uid)) return;           // no change → no save (the capsule re-reads Get anyway)
        if (on)
        {
            list.Add(uid);
            _invRosterUids.Remove(uid);                 // has a switch now → no longer roster-only
        }
        else
        {
            list.Remove(uid);
            if (!InParty(uid) && !InSocial(uid) && !_invRosterUids.Contains(uid))
                _invRosterUids.Add(uid);                // last switch off → keep them listed, roster-only
        }

        // Stage the roster-only key; the owning section's save below does the single _cfg.Save, pushes the uid-set
        // into Lua, and refreshes the roster view.
        _cfg.Set("invite_roster_uids", _invRosterUids);
        if (social) SaveIntInvite(); else SavePartyInvite();
    }

    // Make sure a picked person is on the Players list: a new uid goes in roster-only (both switches off — invariant
    // holds) and only the roster-only key is saved; the party / social uid-sets don't change, so nothing is pushed to
    // Lua. Already listed (any of the three) → no change, no save. Shared by the card button and the Add-player dialog.
    private void EnsureInviteListed(string uid)
    {
        if (string.IsNullOrEmpty(uid) || IsListed(uid)) return;
        _invRosterUids.Add(uid);
        _cfg.Set("invite_roster_uids", _invRosterUids);
        _cfg.Save();
        PushWantedNames();   // re-view (new row) + adds the uid to the wanted-names set
    }

    // The explicit delete (✕ on a row in the main window): drop the uid from all three lists and save.
    // Only the sections that actually changed re-push to Lua.
    private void RemoveFromInviteList(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return;
        bool p = _invPartyUids.Remove(uid), s = _invIntUids.Remove(uid), r = _invRosterUids.Remove(uid);
        if (!p && !s && !r) return;

        _cfg.Set("invite_roster_uids", _invRosterUids);   // persisted by whichever save below runs
        if (p) SavePartyInvite();
        if (s) SaveIntInvite();
        if (!p && !s) { _cfg.Save(); PushWantedNames(); }  // roster-only person: no Lua push, just persist + re-view
    }

    private void ClearAllInviteLists()
    {
        _invPartyUids.Clear();
        _invIntUids.Clear();
        _invRosterUids.Clear();
        _cfg.Set("invite_roster_uids", _invRosterUids);   // persisted by the section saves below
        SavePartyInvite();
        SaveIntInvite();
    }
}
