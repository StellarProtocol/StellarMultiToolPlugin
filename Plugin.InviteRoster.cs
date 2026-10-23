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
// DISPLAY ORDER is stored separately in _invOrder (config invite_player_order): every listed uid exactly once, in the
// order first added. It controls ONLY the order — membership is still the three lists above. Deriving the order from
// the lists (party → social-only → roster-only, the old behaviour) made a switch flip MOVE the row between groups;
// with a stored order a toggle never touches it, so a row stays where it was added. Add (EnsureInviteListed) appends,
// ✕ removes, Clear all clears — each staged inside that action's single CommitInviteConfig save.
//
// The displayed roster (_invRosterView) = _invOrder filtered to listed uids, rebuilt on every change via
// PushWantedNames (every save path funnels through it) — so the per-frame row lambdas index a cached list instead of
// re-filtering per row per frame.
public sealed partial class Plugin
{
    private List<string> _invRosterUids = new();         // listed with both switches off (invite_roster_uids)
    private List<string> _invOrder      = new();         // display order, first-added first (invite_player_order)
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

    // The legacy derived order: party → social-only → roster-only, deduped. Only used to seed / repair _invOrder.
    private List<string> GroupedListedUids()
    {
        var seen = new HashSet<string>();
        var all = new List<string>(_invPartyUids.Count + _invIntUids.Count + _invRosterUids.Count);
        foreach (var u in _invPartyUids)  if (!string.IsNullOrEmpty(u) && seen.Add(u)) all.Add(u);
        foreach (var u in _invIntUids)    if (!string.IsNullOrEmpty(u) && seen.Add(u)) all.Add(u);
        foreach (var u in _invRosterUids) if (!string.IsNullOrEmpty(u) && seen.Add(u)) all.Add(u);
        return all;
    }

    // Load-time (after all three lists are loaded + normalised). No stored order (first run on this version, or an
    // emptied key) → seed from the grouped order, so the rows the user already sees don't move. Then reconcile: drop
    // dupes / entries no longer listed, append any listed uid the order lacks (in grouped order). If that changed the
    // stored value, stage it and let the constructor's single post-Init save persist it (Plugin.cs _cfgLoadDirty).
    private void LoadInviteOrder()
    {
        var stored = _cfg.Get<List<string>>("invite_player_order", null) ?? new List<string>();
        var seen = new HashSet<string>();
        var order = new List<string>(stored.Count);
        foreach (var u in stored)
            if (!string.IsNullOrEmpty(u) && IsListed(u) && seen.Add(u)) order.Add(u);
        foreach (var u in GroupedListedUids())          // seeds everything when stored was empty
            if (seen.Add(u)) order.Add(u);
        _invOrder = order;

        bool changed = order.Count != stored.Count;
        for (int i = 0; !changed && i < order.Count; i++) changed = order[i] != stored[i];
        if (changed)
        {
            _cfg.Set("invite_player_order", _invOrder);
            _cfgLoadDirty = true;
        }
    }

    // _invOrder filtered to listed uids. Defensive: a listed uid missing from the order (shouldn't happen — every add
    // path appends) is still shown, at the end, rather than silently hidden. Runs per change, not per frame.
    private void RebuildRosterView()
    {
        var seen = new HashSet<string>();
        var view = new List<string>(_invOrder.Count);
        foreach (var u in _invOrder) if (IsListed(u) && seen.Add(u)) view.Add(u);
        foreach (var u in GroupedListedUids()) if (seen.Add(u)) view.Add(u);
        _invRosterView = view;
    }

    // Append a newly listed uid to the display order and stage the key (caller commits). No-op if already ordered.
    private void StageOrderAppend(string uid)
    {
        if (_invOrder.Contains(uid)) return;
        _invOrder.Add(uid);
        _cfg.Set("invite_player_order", _invOrder);
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

        // Display order is NOT touched — that's the point (the row stays put). The append only fires if a switch is
        // turned on for a not-yet-listed uid, which the UI never does (callers EnsureInviteListed first).
        if (on) StageOrderAppend(uid);
        // Stage the roster-only key + the owning section (keys + Lua push), then ONE save + re-view.
        _cfg.Set("invite_roster_uids", _invRosterUids);
        if (social) StageIntInvite(); else StagePartyInvite();
        CommitInviteConfig();
    }

    // Make sure a picked person is on the Players list: a new uid goes in roster-only (both switches off — invariant
    // holds) and appended to the display order; only those two keys are staged (one Save); the party / social uid-sets
    // don't change, so nothing is pushed to Lua. Already listed (any of the three) → no change, no save. Shared by the
    // card button and the Add-player dialog.
    private void EnsureInviteListed(string uid)
    {
        if (string.IsNullOrEmpty(uid) || IsListed(uid)) return;
        _invRosterUids.Add(uid);
        StageOrderAppend(uid);
        _cfg.Set("invite_roster_uids", _invRosterUids);
        CommitInviteConfig();   // save + re-view (new row) + adds the uid to the wanted-names set
    }

    // The explicit delete (✕ on a row in the main window): drop the uid from all three lists + the order, save ONCE.
    // Only the sections that actually changed re-push to Lua; a roster-only person pushes nothing, just persists +
    // re-views. Staging both sections then committing once is the fix for a person in both lists (two Saves → the
    // second could be dropped by the framework's no-retry writer; see CommitInviteConfig).
    private void RemoveFromInviteList(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return;
        bool p = _invPartyUids.Remove(uid), s = _invIntUids.Remove(uid), r = _invRosterUids.Remove(uid);
        if (!p && !s && !r) return;

        _invOrder.Remove(uid);
        _cfg.Set("invite_player_order", _invOrder);
        _cfg.Set("invite_roster_uids", _invRosterUids);
        if (p) StagePartyInvite();
        if (s) StageIntInvite();
        CommitInviteConfig();
    }

    // All three lists staged, ONE save. This was the observed data-loss path: two section Saves back-to-back left
    // invite_interaction_uids un-cleared on disk while memory showed "Players (0)".
    private void ClearAllInviteLists()
    {
        _invPartyUids.Clear();
        _invIntUids.Clear();
        _invRosterUids.Clear();
        _invOrder.Clear();
        _cfg.Set("invite_player_order", _invOrder);
        _cfg.Set("invite_roster_uids", _invRosterUids);
        StagePartyInvite();
        StageIntInvite();
        CommitInviteConfig();
    }
}
