using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// UI for the invite auto-accept systems (see InviteAutoAccept for the Lua mechanism).
//
// Layout, top to bottom: "Party Requests" (one approve-all join-request toggle — not list-related), then "Auto-Accept
// from Listed Players" (Party invites + Social invites toggles), then ONE "Players" roster where each person has a
// Party switch and a Social switch (Plugin.InviteRoster.cs — the two per-system uid-sets plus a roster-only list for
// people with both off; rows keep a stable order-added, invite_player_order, so a switch flip never moves a row).
// The Social toggle/uid-set drives BOTH the co-op-emote (multaction) invite AND both ride-together (vehicle/mount)
// directions — one shared rule set (the existing invite_interaction_* config + __stellar_inv_int_* globals); no separate mount config.
//
// WHITELIST ONLY: party + social auto-accept ONLY the ticked uids (never mass-accept strangers). The old
// Blacklist/Whitelist mode dropdowns are gone; InviteAutoAccept's Set* API still takes a whitelist flag, so we always
// pass true (InviteWhitelistOnly), and the invite_*_whitelist config keys are still written — always true — so an old
// Blacklist config is rewritten on load (InitInviteAccept). Join requests stay approve-ALL (no list).
// All default OFF. uids are decimal character ids, kept as strings (int64 precision — the uid can exceed 2^53),
// persisted to config, and pushed to the controller on every change so the Lua state updates immediately rather than
// waiting for the ~3s re-assert. "Clear all" asks first (Plugin.InviteClearConfirm.cs).
public sealed partial class Plugin
{
    private IWindowControl  _inviteWindow = null!;
    private InviteAutoAccept _invite      = null!;
    private InviteNameResolver _nameResolver = null!;   // resolves uid → display name for the list rows (session cache)
    private InviteProfileResolver _profile = null!;     // resolves uid → {name, level, avatar png} for the Add dialog card

    // Whitelist-only: the flag InviteAutoAccept.SetParty / SetInteraction still take. Never false (see header).
    private const bool InviteWhitelistOnly = true;

    private bool _invPartyEnabled;
    private bool _invIntEnabled;
    private bool _invPartyApplyEnabled;   // (4) approve ALL party-join requests — single toggle, no list/mode
    private List<string> _invPartyUids = new();
    private List<string> _invIntUids   = new();

    // Players list geometry. A row holds the name + two toggle capsules + a ✕ remove button; 26f pitch fits the ✕
    // button (a comfortable click target; VirtualList RowHeight IS the pitch).
    private const float InviteRowHeight   = 26f;
    private const float InviteListHeight  = 156f;  // 6 visible rows
    private const int   InviteUidPoolSize = 9;     // ceil(156/26) + 3 (WindowBuilder-Patterns.md §VirtualList pitch)
    private const float InviteSwitchColW  = 100f;  // switch column; "Party"/"Social" header + toggle both centred in it
    private const float InviteRemoveColW  = 28f;   // the ✕ column (24px button, right-aligned)
    private const float InviteWindowWidth = 480f;  // 440 default + room for the two switch columns + ✕

    // ── setup ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Config is restored and pushed into the controller BEFORE Install() so a persisted rule set is already in force
    // the first time the wrap fires. Install() injects the Lua wrap and starts the ~3s re-assert (it owns its own
    // IFramework.Update subscription — same shape as AutoPlayPermitControl).
    private void InitInviteAccept()
    {
        _invPartyEnabled   = _cfg.Get<bool>("invite_party_enabled",   false);
        _invPartyUids      = _cfg.Get<List<string>>("invite_party_uids", null) ?? new List<string>();
        _invIntEnabled     = _cfg.Get<bool>("invite_interaction_enabled",   false);
        _invIntUids        = _cfg.Get<List<string>>("invite_interaction_uids", null) ?? new List<string>();
        // Whitelist-only migration: a config saved in the old Blacklist mode (whitelist=false) is rewritten to true
        // now, so the file can't disagree with what's in force (the Set* calls below push true regardless).
        if (!_cfg.Get<bool>("invite_party_whitelist", true) || !_cfg.Get<bool>("invite_interaction_whitelist", true))
        {
            _cfg.Set("invite_party_whitelist",       InviteWhitelistOnly);
            _cfg.Set("invite_interaction_whitelist", InviteWhitelistOnly);
            _cfgLoadDirty = true;   // persisted by the constructor's single post-Init save (Plugin.cs)
        }
        _invPartyApplyEnabled = _cfg.Get<bool>("invite_party_apply_enabled", false);
        // Roster-only = listed with both switches off (Plugin.InviteRoster.cs). Never pushed to Lua. Normalised after
        // both uid-sets are loaded so anyone with a switch on is dropped from it.
        _invRosterUids     = _cfg.Get<List<string>>("invite_roster_uids", null) ?? new List<string>();
        NormalizeRosterOnly();
        // Display order (invite_player_order): seeded from the current grouped order on first load, then reconciled
        // against the three lists — must run after all of them are loaded + normalised (Plugin.InviteRoster.cs).
        LoadInviteOrder();

        _invite = new InviteAutoAccept(_services);
        _invite.SetParty(_invPartyEnabled, InviteWhitelistOnly, _invPartyUids);
        // SetInteraction drives BOTH the co-op-emote invite and both ride-together directions (one shared uid-set).
        _invite.SetInteraction(_invIntEnabled, InviteWhitelistOnly, _invIntUids);
        _invite.SetPartyApply(_invPartyApplyEnabled);   // (4) approve-all party-join requests (no list)
        _invite.Install();

        // Name resolver: injects its Lua helper + polls the shared flush global. PushWantedNames seeds the set with the
        // persisted uids so names start resolving immediately (the VirtualList re-reads row text each frame, so rows
        // pick up a name the moment it lands — no manual refresh).
        _nameResolver = new InviteNameResolver(_services);
        _nameResolver.Install();
        PushWantedNames();

        // Profile resolver: the richer per-uid lookup (name + level + avatar png) that backs the Add-player dialog's
        // player-info card. Independent of _nameResolver (which covers the compact list rows); installed here so its
        // Lua helper + poll are live before the dialog is ever opened.
        _profile = new InviteProfileResolver(_services);
        _profile.Install();

        _inviteWindow = RegisterWindow("multitool.inviteaccept", "MultiTool: Auto-Accept Invites", 2,
            BuildInviteAcceptRoot(), InviteWindowWidth);

        // "Add player…" dialog (hidden window, no launcher tile) — picks a player and opens the per-player dialog for
        // them (Plugin.InviteAddDialog.cs). Registered after the resolver + invite window so it can lean on both.
        // PlayerNameResolver.Init is already done in InitBandMute (runs first).
        InitInviteAddDialog();
        // Per-player dialog opened from the profile card's "Auto-Accept" button and from the Add-player dialog
        // (Plugin.InviteCardDialog.cs).
        InitInviteCardDialog();
        // "Clear all" confirmation, centred over this window (Plugin.InviteClearConfirm.cs).
        InitInviteClearConfirm();
    }

    // Every save path funnels through here, so it also refreshes the cached roster view the Players rows index.
    // The wanted set = everyone the roster shows (party ∪ social ∪ roster-only) — the set to keep names for.
    private void PushWantedNames()
    {
        RebuildRosterView();
        _nameResolver?.SetWanted(_invRosterView);
    }

    // A uid row, name-first: "SixSeven (18434296)"; a bare uid until the name resolves.
    private string InviteRowText(string uid)
    {
        var n = _nameResolver?.TryGet(uid);
        return string.IsNullOrEmpty(n) ? uid : $"{n} ({uid})";
    }

    private void DisposeInviteAccept()
    {
        try { _invite?.Dispose(); } catch { }   // restores the Lua wrap + stops the timer; tolerates the game being gone
        try { _nameResolver?.Dispose(); } catch { }   // unsubscribes the poll; tolerates the game being gone
        try { _profile?.Dispose(); } catch { }   // unsubscribes the profile poll; tolerates the game being gone
    }

    // ── window ────────────────────────────────────────────────────────────────────────────────────────────────────
    // Layout per the approved mockup (multitool-tools.html): header (icon + title + the hub card's live status chips),
    // then the three sections as dark captioned panels (Plugin.Style.cs).
    private HudElement BuildInviteAcceptRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader(AutoAcceptIconPng, "Auto-Accept Invites", InviteStatus()),
        BuildPartyRequestsSection(),
        BuildListedAutoAcceptSection(),
        BuildPlayersSection(),
    }, Gap: 8f);

    // Live status, shared by the hub card and this window's header (one builder → they can't drift): one chip per
    // system (lit in its own colour when on, grey when off), then the roster size — or "all off".
    private HudElement InviteStatus() => new RowElement(new HudElement[]
    {
        StatusChip("Party",  ChipParty,  () => _invPartyEnabled),
        StatusChip("Social", ChipSocial, () => _invIntEnabled),
        StatusChip("Join",   ChipJoin,   () => _invPartyApplyEnabled),
        new TextElement(InviteStatusTail, Color: () => MutedColor(), NoWrap: true),
    }, Gap: 4f);

    private string InviteStatusTail()
    {
        if (!_invPartyEnabled && !_invIntEnabled && !_invPartyApplyEnabled) return "· all off";
        int n = _invRosterView.Count;
        return n == 1 ? "· 1 player" : $"· {n} players";
    }

    // (4) Party JOIN REQUESTS — the inverse of Party invites (someone applying to join MY party). Its own section
    // because it has nothing to do with the roster: no per-player switch, one toggle that approves EVERY request.
    private HudElement BuildPartyRequestsSection() => SectionPanel(() => "Party requests", null,
        InviteToggleRow("Accept all party requests", () => _invPartyApplyEnabled, OnInvitePartyApplyToggle));

    // The two list-driven systems: they only ever accept players ticked in the roster below (whitelist-only) — the
    // caption says so, so the labels don't repeat "(listed players)".
    private HudElement BuildListedAutoAcceptSection() => SectionPanel(() => "From listed players", null,
        InviteToggleRow("Party invites", () => _invPartyEnabled, OnInvitePartyToggle),
        InviteToggleRow("Social invites (co-op emote + ride together)", () => _invIntEnabled, OnInviteIntToggle));

    // Toggle capsule + label. Shared by all three toggle rows so their layouts can't drift.
    private static HudElement InviteToggleRow(string label, Func<bool> get, Action<bool> set) => new RowElement(
        new HudElement[]
        {
            new ToggleElement(Label: () => "", Get: get, Set: set),
            new TextElement(() => label),
        }, Gap: 6f);

    // ── party handlers ────────────────────────────────────────────────────────────────────────────────────────────
    private void OnInvitePartyToggle(bool on) { _invPartyEnabled = on; SavePartyInvite(); }
    // Per-person switch handlers live in Plugin.InviteRoster.cs (shared with the per-player dialog).
    // ONE _cfg.Save per user action. The framework's FileConfigStore writes on a single background thread with no
    // retry on IOException: two back-to-back Saves meant the second write could hit a transient external lock on the
    // just-written file (AV scan / watcher read) and be DROPPED — "Clear all" left invite_interaction_uids on disk
    // while memory showed 0 players. So the per-section Stage* methods only Set keys + push into Lua, and
    // CommitInviteConfig does the single disk Save + re-view. Multi-section actions stage every section, commit once.
    private void SavePartyInvite() { StagePartyInvite(); CommitInviteConfig(); }

    private void StagePartyInvite()
    {
        _cfg.Set("invite_party_enabled",   _invPartyEnabled);
        _cfg.Set("invite_party_whitelist", InviteWhitelistOnly);   // kept, always true (whitelist-only)
        _cfg.Set("invite_party_uids",      _invPartyUids);
        _invite.SetParty(_invPartyEnabled, InviteWhitelistOnly, _invPartyUids);
    }

    // The single disk write for any invite-roster change, then refresh the roster view + the resolver's wanted set
    // (uid lists may have changed). PushWantedNames is idempotent, so running it once after all sections is enough.
    private void CommitInviteConfig()
    {
        _cfg.Save();
        PushWantedNames();
    }

    // ── party-join-request handler ────────────────────────────────────────────────────────────────────────────────
    // Single toggle, no uids → never touches PushWantedNames / name resolution.
    private void OnInvitePartyApplyToggle(bool on) { _invPartyApplyEnabled = on; SavePartyApply(); }

    private void SavePartyApply()
    {
        _cfg.Set("invite_party_apply_enabled", _invPartyApplyEnabled);
        _cfg.Save();
        _invite.SetPartyApply(_invPartyApplyEnabled);
    }

    // ── interaction handlers ──────────────────────────────────────────────────────────────────────────────────────
    private void OnInviteIntToggle(bool on) { _invIntEnabled = on; SaveIntInvite(); }

    private void SaveIntInvite() { StageIntInvite(); CommitInviteConfig(); }

    private void StageIntInvite()
    {
        _cfg.Set("invite_interaction_enabled",   _invIntEnabled);
        _cfg.Set("invite_interaction_whitelist", InviteWhitelistOnly);   // kept, always true (whitelist-only)
        _cfg.Set("invite_interaction_uids",      _invIntUids);
        _invite.SetInteraction(_invIntEnabled, InviteWhitelistOnly, _invIntUids);
    }

    // ── validate ──────────────────────────────────────────────────────────────────────────────────────────────────
    // Char ids are numeric; the Add-player dialog (Plugin.InviteAddDialog.cs) uses this to gate a typed uid.
    private static bool AllDigits(string s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s) if (c < '0' || c > '9') return false;
        return true;
    }
}
