using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// UI for the per-player band-instrument mute (see InstrumentMutePatch for the mechanism).
//
// Two lists, matching the two things being tracked:
//   • "Hearing now" — auto-populated from incoming notes. Anyone busking nearby shows up without being targeted or
//     named. Click a row to add them to the rule set.
//   • "Muted" / "Allowed" — the durable rule set, persisted to config. Always listed in full, whether or not those
//     players are around, so a mute is always undoable. Click a row to remove it.
public sealed partial class Plugin
{
    private IWindowControl _bandMuteWindow = null!;

    private bool  _bandMuteEnabled;
    private bool  _bandMuteAll;
    private float _bandMuteTick;

    private const float BandMuteTickInterval = 1.0f;

    // Mode dropdown: index 0 = Blacklist (default), index 1 = Whitelist — maps to _bandMuteAll false/true.
    private static readonly string[] BandMuteModeOptions = { "Blacklist", "Whitelist" };

    // ── setup (absorbed from the host plugin's ctor/Dispose) ──────────────────────────────────────────────────────
    // Config is restored BEFORE the patch goes live so a persisted rule set is already in force the first time a
    // note arrives — otherwise a muted player would be audible until the first tick. Ends by registering the
    // band-mute sub-menu window (opened from the hub button, no launcher tile of its own).
    private void InitBandMute()
    {
        _bandMuteEnabled = _cfg.Get<bool>("band_mute_enabled", false);
        _bandMuteAll     = _cfg.Get<bool>("band_mute_all",     false);

        var picks = _cfg.Get<List<BandMutePick>>("band_mute_picks", null);
        if (picks is { Count: > 0 })
        {
            InstrumentMutePatch.LoadPicks(picks);
        }
        else
        {
            // Migrate the older packed-string key forward, then re-save as JSON so this only ever happens once.
            InstrumentMutePatch.LoadLegacyNames(_cfg.Get<string>("band_mute_names", ""));
            // Staged only — the constructor's single post-Init save persists it (Plugin.cs _cfgLoadDirty).
            if (InstrumentMutePatch.Picks().Count > 0)
            {
                _cfg.Set("band_mute_picks", InstrumentMutePatch.Picks());
                _cfgLoadDirty = true;
            }
        }

        InstrumentMutePatch.Enabled       = _bandMuteEnabled;
        InstrumentMutePatch.MuteAllOthers = _bandMuteAll;
        PlayerNameResolver.Init(_services);
        InstrumentMutePatch.Install(_services.Harmony.Create(), _services.Log.Info,
            () => _services.CombatSnapshot.LocalEntityId.Value);
        _services.Log.Info($"[MultiTool] name resolver: {PlayerNameResolver.Status()}");
        _services.Framework.Update += OnBandMuteUpdate;

        _bandMuteWindow = RegisterWindow("multitool.bandmute", "MultiTool: Band Mute", 1, BuildBandMuteRoot());
    }

    private void DisposeBandMute()
    {
        try { _services.Framework.Update -= OnBandMuteUpdate; } catch { }
        InstrumentMutePatch.Uninstall();
    }

    // Runs whether or not the mute window is open. A pick restored from config by NAME is dormant until that name
    // resolves to a live uuid, so leaving this to the UI meant a restored mute did nothing until you happened to
    // open the window. (Picks restored by uuid bite immediately and don't need this.)
    private void OnBandMuteUpdate(float dt)
    {
        if (!_bandMuteEnabled) return;
        _bandMuteTick += dt;
        if (_bandMuteTick < BandMuteTickInterval) return;
        _bandMuteTick = 0f;
        try
        {
            InstrumentMutePatch.ResolveNames();
            // A pick that just learned its name (or uuid) must be written back, or the completed entry would exist
            // only in memory and be lost on exit — leaving a nameless "#1234…" row forever.
            if (InstrumentMutePatch.PicksDirty)
            {
                InstrumentMutePatch.ClearPicksDirty();
                SaveBandMutePicks();
            }
        }
        catch (Exception ex) { _services.Log.Warning($"[BandMute] resolve tick: {ex.InnerException?.Message ?? ex.Message}"); }
    }

    private HudElement BuildBandMuteRoot() => new ColumnElement(new HudElement[]
    {
        new TextElement(() => "Mute Other Players' Instruments", Emphasis: true),
        new TextElement(() => "Silences their notes only — the performer, their animation and nameplate are untouched.",
            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
        new RowElement(new HudElement[]
        {
            new ToggleElement(Label: () => "", Get: () => _bandMuteEnabled, Set: OnBandMuteToggle),
            new TextElement(() => "Enable"),
        }, Gap: 6f),
        new RowElement(new HudElement[]
        {
            // Blacklist (default) = mute only the listed players; Whitelist = mute everyone EXCEPT the listed players.
            // index 0/1 maps to _bandMuteAll false/true, routed through the existing toggle handler.
            new TextElement(() => "Mode"),
            new DropdownElement(
                Selected: () => _bandMuteAll ? 1 : 0,
                Options:  () => BandMuteModeOptions,
                OnSelect: i => OnBandMuteAllToggle(i == 1),
                Width:    140f),
        }, Gap: 6f),

        // ── who is audible right now ──
        new SeparatorElement(),
        new TextElement(() => MuteHeard().Count == 0
                ? "Hearing now — nobody yet, stand near a performer."
                : $"Hearing now ({MuteHeard().Count}) — click to {(_bandMuteAll ? "allow" : "mute")}",
            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
        new VirtualListElement(
            Count:     () => MuteHeard().Count,
            RowHeight: 22f,
            Pool:      BuildMuteHeardPool(),
            OnWindow:  first => _muteHeardFirst = first,
            Height:    132f),
        new ButtonElement(Label: () => "Clear heard list", OnClick: InstrumentMutePatch.ForgetAll, Width: 320f),

        // ── the durable rule set ──
        new SeparatorElement(),
        new TextElement(() => InstrumentMutePatch.Picks().Count == 0
                ? (_bandMuteAll ? "Allowed — empty (everyone is muted)." : "Muted — empty.")
                : $"{(_bandMuteAll ? "Allowed" : "Muted")} ({InstrumentMutePatch.Picks().Count}) — click to remove",
            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
        new VirtualListElement(
            Count:     () => InstrumentMutePatch.Picks().Count,
            RowHeight: 22f,
            Pool:      BuildMutePickPool(),
            OnWindow:  first => _mutePickFirst = first,
            Height:    110f),
        new ButtonElement(Label: () => _bandMuteAll ? "Clear allow-list" : "Unmute all",
            OnClick: ClearBandMutePicks, Width: 320f),

        new TextElement(() => _bandMuteEnabled
                ? (_bandMuteAll ? "Active — all others muted except those listed." : "Active — listed players muted.")
                : "Off — everyone audible.",
            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
    }, Gap: 8f);

    // ── toggles ───────────────────────────────────────────────────────────────────────────────────────────────
    private void OnBandMuteToggle(bool on)
    {
        _bandMuteEnabled = on;
        InstrumentMutePatch.Enabled = on;
        _cfg.Set<bool>("band_mute_enabled", on);
        _cfg.Save();
    }

    private void OnBandMuteAllToggle(bool on)
    {
        _bandMuteAll = on;
        InstrumentMutePatch.MuteAllOthers = on;
        _cfg.Set<bool>("band_mute_all", on);
        _cfg.Save();
    }

    private void SaveBandMutePicks()
    {
        _cfg.Set("band_mute_picks", InstrumentMutePatch.Picks());
        _cfg.Save();
    }

    private void ClearBandMutePicks()
    {
        InstrumentMutePatch.ClearPicks();
        SaveBandMutePicks();
    }

    // ── list 1: hearing now ───────────────────────────────────────────────────────────────────────────────────
    // Snapshot() allocates and sorts, and every element getter runs per frame, so the list is cached on a short
    // throttle rather than rebuilt per poll.
    private const int    MutePoolSize  = 8;    // ≈ visible rows (132/22 = 6) + margin
    private const int    PickPoolSize  = 7;    // ≈ visible rows (110/22 = 5) + margin
    private const double MuteRefreshMs = 250;

    private List<InstrumentMutePatch.Performer> _muteHeard = new();
    private DateTime _muteHeardAt;
    private int      _muteHeardFirst;
    private int      _mutePickFirst;

    private List<InstrumentMutePatch.Performer> MuteHeard()
    {
        var now = DateTime.UtcNow;
        if ((now - _muteHeardAt).TotalMilliseconds >= MuteRefreshMs)
        {
            _muteHeard   = InstrumentMutePatch.Snapshot();
            _muteHeardAt = now;
        }
        return _muteHeard;
    }

    private HudElement[] BuildMuteHeardPool()
    {
        var pool = new HudElement[MutePoolSize];
        for (int s = 0; s < MutePoolSize; s++)
        {
            int slot = s;
            pool[s] = new SelectableElement(
                new TextElement(() => MuteHeardLabel(slot), Color: () => MuteHeardColor(slot)),
                OnClick:  () => ToggleBandMutePick(_muteHeardFirst + slot),
                Selected: () =>
                {
                    var list = MuteHeard();
                    int i = _muteHeardFirst + slot;
                    return i < list.Count && InstrumentMutePatch.IsPicked(list[i].Uuid);
                });
        }
        return pool;
    }

    private void ToggleBandMutePick(int idx)
    {
        var list = MuteHeard();
        if (idx < 0 || idx >= list.Count) return;
        InstrumentMutePatch.TogglePick(list[idx].Uuid);
        SaveBandMutePicks();
    }

    private string MuteHeardLabel(int slot)
    {
        var list = MuteHeard();
        int i = _muteHeardFirst + slot;
        if (i < 0 || i >= list.Count) return "";

        var    p     = list[i];
        string name  = p.Name.Length > 0 ? p.Name : $"#{p.Uuid}";
        bool   muted = _bandMuteEnabled && InstrumentMutePatch.IsMuted(p.Uuid);
        double age   = (DateTime.UtcNow - p.LastHeardUtc).TotalSeconds;
        string when  = age < 2 ? "playing" : age < 90 ? $"{age:F0}s" : $"{age / 60:F0}m";
        return $"{(muted ? "x" : "♪")} {name}   {(muted ? $"muted {p.Blocked}" : $"{p.Notes} notes")}   {when}";
    }

    private ColorRgba? MuteHeardColor(int slot)
    {
        var list = MuteHeard();
        int i = _muteHeardFirst + slot;
        if (i < 0 || i >= list.Count) return null;
        return _bandMuteEnabled && InstrumentMutePatch.IsMuted(list[i].Uuid)
            ? (ColorRgba?)_services.Theme.Colors.TextMuted
            : null;
    }

    // ── list 2: the rule set ──────────────────────────────────────────────────────────────────────────────────
    private HudElement[] BuildMutePickPool()
    {
        var pool = new HudElement[PickPoolSize];
        for (int s = 0; s < PickPoolSize; s++)
        {
            int slot = s;
            pool[s] = new SelectableElement(
                new TextElement(() => MutePickLabel(slot)),
                OnClick:  () => RemoveBandMutePick(_mutePickFirst + slot),
                Selected: () => false);   // this list IS the selection; highlighting every row would be noise
        }
        return pool;
    }

    private void RemoveBandMutePick(int idx)
    {
        if (idx < 0 || idx >= InstrumentMutePatch.Picks().Count) return;
        InstrumentMutePatch.RemovePickAt(idx);
        SaveBandMutePicks();
    }

    private string MutePickLabel(int slot)
    {
        var picks = InstrumentMutePatch.Picks();
        int i = _mutePickFirst + slot;
        if (i < 0 || i >= picks.Count) return "";

        var    pick = picks[i];
        string name = pick.Name.Length > 0 ? pick.Name : $"#{pick.Uuid}";
        // Flag entries that can't act yet: a name-only pick (uuid 0, from an older config) does nothing until that
        // player's name resolves, so it's worth showing rather than looking like an active rule.
        string note = pick.Uuid == 0 ? "  (by name — waiting)" : "";
        return $"− {name}{note}";
    }
}
