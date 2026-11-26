using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// UI for the invite auto-accept systems (see InviteAutoAccept for the Lua mechanism).
//
// Layout, top to bottom (approved mockups multitool-autoaccept-modes.html + multitool-autoaccept-leader.html option
// B): ONE "Accept automatically" panel with a 3-way mode per system — Party (invites + join requests), Social (co-op
// emote + ride together), Party leader requests — each Off / Listed / Everyone; then ONE "Players" roster where each
// person has a Party, a Social and a Leader switch (Plugin.InviteAccept.Table.cs + Plugin.InviteRoster.cs — the three
// per-system uid-sets plus a roster-only list for people with every switch off; rows keep a stable order-added,
// invite_player_order, so a switch flip never moves a row). A column's switches only matter while its system is
// Listed, so the column is dimmed otherwise (state kept, still clickable).
//
// Config: invite_party_mode / invite_social_mode / invite_leader_mode (int 0/1/2, InviteAutoAccept.Mode*), plus the
// uid lists invite_party_uids / invite_interaction_uids / invite_leader_uids. The pre-1.3 on/off keys
// (invite_party_enabled, invite_interaction_enabled, invite_party_apply_enabled) are only READ, once, to migrate
// (MigrateInviteModes). All default Off. uids are decimal character ids, kept as strings (int64 precision — the uid
// can exceed 2^53), persisted to config, and pushed to the controller on every change so the Lua state updates
// immediately rather than waiting for the ~3s re-assert. "Clear all" asks first (Plugin.InviteClearConfirm.cs).
public sealed partial class Plugin
{
    private IWindowControl  _inviteWindow = null!;
    private InviteAutoAccept _invite      = null!;
    private InviteNameResolver _nameResolver = null!;   // resolves uid → display name for the list rows (session cache)
    private InviteProfileResolver _profile = null!;     // resolves uid → {name, level, avatar png} for the Add dialog card

    private int _invPartyMode, _invIntMode, _invLeaderMode;   // InviteAutoAccept.ModeOff / ModeListed / ModeEveryone
    private List<string> _invPartyUids  = new();
    private List<string> _invIntUids    = new();
    private List<string> _invLeaderUids = new();

    // Players list geometry. A row holds the name + three toggle capsules + a ✕ remove button; 26f pitch fits the ✕
    // button (a comfortable click target; VirtualList RowHeight IS the pitch).
    private const float InviteRowHeight   = 26f;
    private const float InviteListHeight  = 156f;  // 6 visible rows
    private const int   InviteUidPoolSize = 9;     // ceil(156/26) + 3 (WindowBuilder-Patterns.md §VirtualList pitch)
    private const float InviteSwitchColW  = 80f;   // switch column; "Party"/"Social"/"Leader" header + toggle centred in it
    private const float InviteRemoveColW  = 28f;   // the ✕ column (24px button, right-aligned)
    private const float InviteWindowWidth = 540f;  // 480 + ~60 for the Leader column (approved mockup option B)

    // ── setup ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Config is restored and pushed into the controller BEFORE Install() so a persisted rule set is already in force
    // the first time the wrap fires. Install() injects the Lua wrap and starts the ~3s re-assert (it owns its own
    // IFramework.Update subscription — same shape as AutoPlayPermitControl).
    private void InitInviteAccept()
    {
        _invPartyUids  = _cfg.Get<List<string>>("invite_party_uids", null) ?? new List<string>();
        _invIntUids    = _cfg.Get<List<string>>("invite_interaction_uids", null) ?? new List<string>();
        _invLeaderUids = _cfg.Get<List<string>>("invite_leader_uids", null) ?? new List<string>();
        MigrateInviteModes();
        // Roster-only = listed with every switch off (Plugin.InviteRoster.cs). Never pushed to Lua. Normalised after
        // all uid-sets are loaded so anyone with a switch on is dropped from it.
        _invRosterUids = _cfg.Get<List<string>>("invite_roster_uids", null) ?? new List<string>();
        NormalizeRosterOnly();
        // Display order (invite_player_order): seeded from the current grouped order on first load, then reconciled
        // against the lists — must run after all of them are loaded + normalised (Plugin.InviteRoster.cs).
        LoadInviteOrder();

        _invite = new InviteAutoAccept(_services);
        _invite.SetParty(_invPartyMode, _invPartyUids);
        // SetInteraction drives BOTH the co-op-emote invite and both ride-together directions (one shared uid-set).
        _invite.SetInteraction(_invIntMode, _invIntUids);
        _invite.SetLeader(_invLeaderMode, _invLeaderUids);
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

    // One-time migration to the 3-way modes. A mode key that is missing (sentinel -1) is derived from the pre-1.3
    // on/off keys and written, so this runs once per key and the old keys are never consulted again:
    //   Party  = Listed if the old party-invite toggle was on; else Everyone if "Accept all party requests" was on; else Off.
    //   Social = Listed if the old social toggle was on; else Off.     Leader = Off (new).
    // Staged only; persisted by the constructor's single post-Init save (Plugin.cs _cfgLoadDirty).
    private void MigrateInviteModes()
    {
        _invPartyMode = _cfg.Get<int>("invite_party_mode", -1);
        if (_invPartyMode < 0)
        {
            _invPartyMode = _cfg.Get<bool>("invite_party_enabled", false) ? InviteAutoAccept.ModeListed
                : _cfg.Get<bool>("invite_party_apply_enabled", false) ? InviteAutoAccept.ModeEveryone
                : InviteAutoAccept.ModeOff;
            _cfg.Set("invite_party_mode", _invPartyMode);
            _cfgLoadDirty = true;
        }
        _invIntMode = _cfg.Get<int>("invite_social_mode", -1);
        if (_invIntMode < 0)
        {
            _invIntMode = _cfg.Get<bool>("invite_interaction_enabled", false)
                ? InviteAutoAccept.ModeListed : InviteAutoAccept.ModeOff;
            _cfg.Set("invite_social_mode", _invIntMode);
            _cfgLoadDirty = true;
        }
        _invLeaderMode = _cfg.Get<int>("invite_leader_mode", -1);
        if (_invLeaderMode < 0)
        {
            _invLeaderMode = InviteAutoAccept.ModeOff;
            _cfg.Set("invite_leader_mode", _invLeaderMode);
            _cfgLoadDirty = true;
        }
        _invPartyMode  = ClampInviteMode(_invPartyMode);   // a hand-edited value can only ever mean 0/1/2
        _invIntMode    = ClampInviteMode(_invIntMode);
        _invLeaderMode = ClampInviteMode(_invLeaderMode);
    }

    private static int ClampInviteMode(int m) =>
        m == InviteAutoAccept.ModeListed || m == InviteAutoAccept.ModeEveryone ? m : InviteAutoAccept.ModeOff;

    // Every save path funnels through here, so it also refreshes the cached roster view the Players rows index.
    // The wanted set = everyone the roster shows (party ∪ social ∪ leader ∪ roster-only) — the set to keep names for.
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
    // Header (icon + title + the hub card's live status chips), then the two sections as dark captioned panels.
    private HudElement BuildInviteAcceptRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader(AutoAcceptIconPng, "Auto-Accept Invites", InviteStatus()),
        BuildAcceptModesSection(),
        BuildPlayersSection(),
    }, Gap: 8f);

    // Live status, shared by the hub card and this window's header (one builder → they can't drift): one chip per
    // system ("Party · Listed"; grey when Off, lit in its colour otherwise), then the roster size — or "all off".
    private HudElement InviteStatus() => new RowElement(new HudElement[]
    {
        ModeChip("Party",  ChipParty,  () => _invPartyMode),
        ModeChip("Social", ChipSocial, () => _invIntMode),
        ModeChip("Leader", ChipJoin,   () => _invLeaderMode),   // the old Join chip's colour
        new TextElement(InviteStatusTail, Color: () => MutedColor(), NoWrap: true),
    }, Gap: 4f);

    private int _invTailN = -1;       // cached tail text: polled per frame by two windows, rebuilt only when N changes
    private string _invTailText = "";

    private string InviteStatusTail()
    {
        if (_invPartyMode == InviteAutoAccept.ModeOff && _invIntMode == InviteAutoAccept.ModeOff
            && _invLeaderMode == InviteAutoAccept.ModeOff) return "· all off";
        int n = _invRosterView.Count;
        if (n != _invTailN) { _invTailN = n; _invTailText = n == 1 ? "· 1 player" : $"· {n} players"; }
        return _invTailText;
    }

    // A 3-state status chip: "<name> · Off|Listed|Everyone". Two complete pills swapped by a Conditional wrapping the
    // WHOLE pill (never a Conditional inside a bubble's measured child): Off = muted on a faint muted pill, else the
    // system's colour on a 22% pill of it. Unlike StatusChip it HUGS its label (bubble in a Row, explicit MaxWidth —
    // a MaxWidth-0 bubble under a Conditional caps at 100 px): a fixed width for "Everyone" in all three chips would not
    // fit the hub card. Both states bold at an explicit 12 px (the plain path ignores FontSize) so they size alike
    // (Stellar-Design-Language "clickable chip" note). Labels prebuilt — nothing is formatted per frame.
    private const float ModeChipMaxWidth = 150f;

    private HudElement ModeChip(string name, ColorRgba color, Func<int> mode)
    {
        var labels = new[] { name + " · Off", name + " · Listed", name + " · Everyone" };
        return new ConditionalElement(() => mode() != InviteAutoAccept.ModeOff,
            ModeChipPill(new TextElement(() => labels[ClampInviteMode(mode())], Color: () => color, NoWrap: true,
                    FontSize: 12) { Bold = true }, () => color with { A = 0.22f }),
            ModeChipPill(new TextElement(() => labels[0], Color: () => MutedColor(), NoWrap: true, FontSize: 12)
                    { Bold = true }, () => MutedColor() with { A = 0.15f }));
    }

    // Same strut + padding as ChipPill (24 px tall in both states), minus the fixed-width cell.
    private HudElement ModeChipPill(TextElement text, Func<ColorRgba> fill) => new BubbleElement(
        new RowElement(new HudElement[]
        {
            new SpacerElement(0f, ChipStrutHeight),   // width 0 + height > 0 = a non-flexible height strut
            text,
        }, Gap: 0.01f, Justify: RowJustify.Center))
    { FillColorFn = () => Fade(fill()), PaddingX = 6f, PaddingY = 1f, MaxWidth = ModeChipMaxWidth };

    // "Accept automatically": one row per system — bold name + muted sub-label, then Off / Listed / Everyone pills.
    private HudElement BuildAcceptModesSection() => SectionPanel(() => "Accept automatically", null,
        InviteModeRow("Party", "invites and join requests", () => _invPartyMode, SetInvitePartyMode),
        InviteModeRow("Social", "co-op emote + ride together", () => _invIntMode, SetInviteIntMode),
        InviteModeRow("Party leader requests", "a member asks to take over as leader", () => _invLeaderMode,
            SetInviteLeaderMode));

    private HudElement InviteModeRow(string name, string sub, Func<int> get, Action<int> set) => new ColumnElement(
        new HudElement[]
        {
            new RowElement(new HudElement[]
            {
                new TextElement(() => name, Color: () => TextColor(), Emphasis: true, NoWrap: true),
                new TextElement(() => sub, Color: () => MutedColor(), NoWrap: true),
            }, Gap: 6f),
            new RowElement(new HudElement[]
            {
                ChoicePill("Off",      () => get() == InviteAutoAccept.ModeOff,      () => set(InviteAutoAccept.ModeOff)),
                ChoicePill("Listed",   () => get() == InviteAutoAccept.ModeListed,   () => set(InviteAutoAccept.ModeListed)),
                ChoicePill("Everyone", () => get() == InviteAutoAccept.ModeEveryone, () => set(InviteAutoAccept.ModeEveryone)),
            }, Gap: 6f),
        }, Gap: 4f);

    // Toggle capsule + label (no longer used by Auto-Accept itself; the Language window's rows share it).
    private static HudElement InviteToggleRow(string label, Func<bool> get, Action<bool> set) => new RowElement(
        new HudElement[]
        {
            new ToggleElement(Label: () => "", Get: get, Set: set),
            new TextElement(() => label),
        }, Gap: 6f);

    // A roster column only matters while its system is Listed → dim it otherwise (Plugin.InviteAccept.Table.cs).
    private bool PartyColDim()  => _invPartyMode  != InviteAutoAccept.ModeListed;
    private bool SocialColDim() => _invIntMode    != InviteAutoAccept.ModeListed;
    private bool LeaderColDim() => _invLeaderMode != InviteAutoAccept.ModeListed;

    // ── mode handlers ─────────────────────────────────────────────────────────────────────────────────────────────
    private void SetInvitePartyMode(int m)  { if (m == _invPartyMode) return;  _invPartyMode = m;  SavePartyInvite(); }
    private void SetInviteIntMode(int m)    { if (m == _invIntMode) return;    _invIntMode = m;    SaveIntInvite(); }
    private void SetInviteLeaderMode(int m) { if (m == _invLeaderMode) return; _invLeaderMode = m; SaveLeaderInvite(); }

    // Per-person switch handlers live in Plugin.InviteRoster.cs (shared with the per-player dialog).
    // ONE _cfg.Save per user action. The framework's FileConfigStore writes on a single background thread with no
    // retry on IOException: two back-to-back Saves meant the second write could hit a transient external lock on the
    // just-written file (AV scan / watcher read) and be DROPPED — "Clear all" left invite_interaction_uids on disk
    // while memory showed 0 players. So the per-section Stage* methods only Set keys + push into Lua, and
    // CommitInviteConfig does the single disk Save + re-view. Multi-section actions stage every section, commit once.
    private void SavePartyInvite()  { StagePartyInvite();  CommitInviteConfig(); }
    private void SaveIntInvite()    { StageIntInvite();    CommitInviteConfig(); }
    private void SaveLeaderInvite() { StageLeaderInvite(); CommitInviteConfig(); }

    private void StagePartyInvite()
    {
        _cfg.Set("invite_party_mode", _invPartyMode);
        _cfg.Set("invite_party_uids", _invPartyUids);
        _invite.SetParty(_invPartyMode, _invPartyUids);
    }

    private void StageIntInvite()
    {
        _cfg.Set("invite_social_mode",      _invIntMode);
        _cfg.Set("invite_interaction_uids", _invIntUids);
        _invite.SetInteraction(_invIntMode, _invIntUids);
    }

    private void StageLeaderInvite()
    {
        _cfg.Set("invite_leader_mode", _invLeaderMode);
        _cfg.Set("invite_leader_uids", _invLeaderUids);
        _invite.SetLeader(_invLeaderMode, _invLeaderUids);
    }

    // The single disk write for any invite-roster change, then refresh the roster view + the resolver's wanted set
    // (uid lists may have changed). PushWantedNames is idempotent, so running it once after all sections is enough.
    private void CommitInviteConfig()
    {
        _cfg.Save();
        PushWantedNames();
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
