using System;
using System.Globalization;

namespace Stellar.MultiTool;

// Effect Filter: skip loading other players' skill effects (mechanism + rule: EffectFilterPatch.cs; window:
// Plugin.EffectFilterWindow.cs). This partial owns the settings, the lazy patch install and a 1 s tick.
//
// The tick (Framework.Update + accumulator — SDK 2.28 has no IFramework.Every) runs only while some setting can hide:
// refreshes my uuid + the entity-manager wrapper, re-evaluates Active (scope = dungeon run id), retries the install
// every 10 s until the game types load, and flushes the diagnostics line. Off = one bool read per frame.
// Fight boundaries: CombatState.Changed(true) resets the "hid N this fight" counter; SceneChanged resets the counter
// and every cache. Both handlers are O(1) apart from clearing the caches once per scene.
public sealed partial class Plugin
{
    private bool _effOn;              // master: "Other players' skill effects" (default OFF on first install)
    private bool _effPets;            // "Their pets & summons"
    private bool _effShowMarkers;     // SHOW rows (default ON = shown; off = hidden) — "Telegraphs & important markers"
    private bool _effShowMonsters;    // "Boss & monster effects"
    private bool _effShowMine;        // "My own effects"
    private bool _effKeepOnMe;        // "Effects that land on me"
    private bool _effKeepBuffs;       // "Heal & buff zones"
    private bool _effEverywhere;      // Where: false = Dungeons & raids (default), true = Everywhere

    private HarmonyLib.Harmony? _effHarmony;   // created once; install retries reuse it
    private float _effTick;
    private long  _effLastInstallTry, _effDiagStart, _effLastMe;
    private bool  _effSubscribed;

    private const float EffTickInterval  = 1f;
    private const long  EffInstallRetryMs = 10_000;
    private const long  EffDiagWindowMs   = 10_000;

    private void InitEffectFilter()
    {
        _effOn           = _cfg.Get<bool>("effect_filter_enabled",       false);
        _effPets         = _cfg.Get<bool>("effect_filter_pets",          true);
        _effShowMarkers  = _cfg.Get<bool>("effect_filter_show_markers",  true);
        _effShowMonsters = _cfg.Get<bool>("effect_filter_show_monsters", true);
        _effShowMine     = _cfg.Get<bool>("effect_filter_show_mine",     true);
        _effKeepOnMe     = _cfg.Get<bool>("effect_filter_keep_on_me",    true);
        _effKeepBuffs    = _cfg.Get<bool>("effect_filter_keep_buffs",    true);
        _effEverywhere   = _cfg.Get<bool>("effect_filter_everywhere",    false);
        PushEffectRules();

        _services.Framework.Update        += OnEffectFilterUpdate;
        _services.ClientState.SceneChanged += OnEffectSceneChanged;
        _services.CombatState.Changed     += OnEffectCombatChanged;
        _effSubscribed = true;
        if (EffAnyHide) TryInstallEffectFilter();

        // The help popover first: the window's "?" fill funcs read _tipWindow.
        InitHelpTip();
        _effectWindow = RegisterWindow("multitool.effectfilter", "Effect Filter", 4, BuildEffectFilterRoot(),
            EffWindowWidth);
        _tipOwner = _effectWindow;
    }

    private void DisposeEffectFilter()
    {
        EffectFilterPatch.Active = false;
        if (_effSubscribed)
        {
            try { _services.Framework.Update        -= OnEffectFilterUpdate; } catch { }
            try { _services.ClientState.SceneChanged -= OnEffectSceneChanged; } catch { }
            try { _services.CombatState.Changed     -= OnEffectCombatChanged; } catch { }
            _effSubscribed = false;
        }
        DisposeHelpTip();
        // The patch itself is unpatched by IHarmonyHost on plugin dispose — never here (hot method).
    }

    // ── settings (one save per click, MultiTool-Plugin.md §4.1) ──────────────────────────────────────────────────
    private void ToggleEffMaster()       => SaveEffBool("effect_filter_enabled",       _effOn           = !_effOn);
    private void ToggleEffPets()         => SaveEffBool("effect_filter_pets",          _effPets         = !_effPets);
    private void ToggleEffShowMarkers()  => SaveEffBool("effect_filter_show_markers",  _effShowMarkers  = !_effShowMarkers);
    private void ToggleEffShowMonsters() => SaveEffBool("effect_filter_show_monsters", _effShowMonsters = !_effShowMonsters);
    private void ToggleEffShowMine()     => SaveEffBool("effect_filter_show_mine",     _effShowMine     = !_effShowMine);
    private void ToggleEffKeepOnMe()     => SaveEffBool("effect_filter_keep_on_me",    _effKeepOnMe     = !_effKeepOnMe);
    private void ToggleEffKeepBuffs()    => SaveEffBool("effect_filter_keep_buffs",    _effKeepBuffs    = !_effKeepBuffs);

    private void SetEffEverywhere(bool everywhere)
    {
        if (_effEverywhere == everywhere) return;
        _effEverywhere = everywhere;
        SaveEffBool("effect_filter_everywhere", everywhere);
        UpdateEffActive();
    }

    // Every setting change may switch the filter between "can hide" and "keeps everything", so each one re-arms.
    private void SaveEffBool(string key, bool value)
    {
        _cfg.Set<bool>(key, value);
        _cfg.Save();
        PushEffectRules();
        if (EffAnyHide)
        {
            TryInstallEffectFilter();
            // The tick doesn't run while nothing can hide, so "me" may be stale (another character since) — re-read.
            RefreshEffMe();
            EffectFilterPatch.RefreshManager();
        }
        UpdateEffActive();
    }

    // Can any setting hide something? The master hides other players; a SHOW row off hides its class. "Effects that land
    // on me" / "Heal & buff zones" only KEEP (they override the rows below them), so turning them off alone hides nothing.
    private bool EffAnyHide => _effOn || !_effShowMarkers || !_effShowMonsters || !_effShowMine;

    // A rule change invalidates every cached decision (an effect hidden under the old rule may now be kept).
    private void PushEffectRules()
    {
        EffectFilterPatch.HideOthers   = _effOn;
        EffectFilterPatch.HidePets     = _effPets;
        EffectFilterPatch.ShowMarkers  = _effShowMarkers;
        EffectFilterPatch.ShowMonsters = _effShowMonsters;
        EffectFilterPatch.ShowMine     = _effShowMine;
        EffectFilterPatch.KeepOnMe     = _effKeepOnMe;
        EffectFilterPatch.KeepBuffs    = _effKeepBuffs;
        EffectFilterPatch.ResetCaches();
    }

    // ── install + active gate ────────────────────────────────────────────────────────────────────────────────────
    // Types may not resolve at plugin construction (persisted-ON at boot) → retried from the tick every 10 s.
    private void TryInstallEffectFilter()
    {
        _effLastInstallTry = Environment.TickCount64;
        if (EffectFilterPatch.Installed || EffectFilterPatch.HardFailed) return;
        _effHarmony ??= _services.Harmony.Create("effectfilter");
        if (!EffectFilterPatch.Install(_effHarmony, _services.Log.Info)) return;
        _effDiagStart = Environment.TickCount64;
        RefreshEffMe();
        EffectFilterPatch.RefreshManager();
        UpdateEffActive();
    }

    // In a dungeon/raid run = the framework's run id (0 between runs / in the open world), as Dungeon Hub uses.
    private bool EffInScope => _effEverywhere || _services.Dungeon.CurrentRunId != 0;

    // Active needs my uuid: without it "my own effects" can't be told apart from another player's.
    private void UpdateEffActive() =>
        EffectFilterPatch.Active = EffAnyHide && EffectFilterPatch.Installed && EffectFilterPatch.LocalUuid != 0 && EffInScope;

    private void RefreshEffMe()
    {
        long me = 0;
        try { me = _services.CombatSnapshot.LocalEntityId.Value; } catch { }
        EffectFilterPatch.LocalUuid = me;
        if (me == 0 || me == _effLastMe) return;
        // New character / first sync: old decisions were taken against another "me". Logged once per uuid so the uuid
        // source can be checked in game (meType must be 10 = Char).
        _effLastMe = me;
        EffectFilterPatch.ResetCaches();
        _services.Log.Info($"[EffFilter] me={me} meType={EffectFilterPatch.EntTypeOf(me)}");
    }

    private void OnEffectFilterUpdate(float dt)
    {
        if (!EffAnyHide) return;
        _effTick += dt;
        if (_effTick < EffTickInterval) return;
        _effTick = 0f;
        try
        {
            long now = Environment.TickCount64;
            if (!EffectFilterPatch.Installed)
            {
                if (now - _effLastInstallTry >= EffInstallRetryMs) TryInstallEffectFilter();
                return;
            }
            RefreshEffMe();
            EffectFilterPatch.RefreshManager();
            UpdateEffActive();
            if (now - _effDiagStart >= EffDiagWindowMs)
            {
                EffectFilterPatch.FlushDiag((now - _effDiagStart) / 1000.0);
                _effDiagStart = now;
            }
        }
        catch (Exception ex) { _services.Log.Warning($"[EffFilter] tick: {ex.InnerException?.Message ?? ex.Message}"); }
    }

    private void OnEffectSceneChanged(string? _)
    {
        EffectFilterPatch.HiddenThisFight = 0;
        EffectFilterPatch.ResetCaches();
        // Disarm until the next tick re-reads "me" (a scene change can be a relog onto another character — a stale uuid
        // would hide MY effects). Also turns the filter off at once when leaving a dungeon. Costs ≤1 s unfiltered.
        EffectFilterPatch.LocalUuid = 0;
        UpdateEffActive();
    }

    private void OnEffectCombatChanged(bool inCombat)
    {
        if (inCombat) EffectFilterPatch.HiddenThisFight = 0;
    }

    // ── status text (hub card + window header) ───────────────────────────────────────────────────────────────────
    private bool EffWaiting => EffAnyHide && !EffInScope;
    private bool EffFiltering => EffAnyHide && EffInScope && !EffectFilterPatch.HardFailed;

    private string EffHubStatusText() =>
        !EffAnyHide ? "Off" : _effEverywhere ? "On · everywhere" : "On · dungeons & raids";

    private string EffWindowStatusText()
    {
        if (!EffAnyHide) return "Off · all effects shown";
        if (EffectFilterPatch.HardFailed) return "On · not available in this game version";
        if (EffWaiting) return "On · waiting for a dungeon";
        return EffHidText();
    }

    // Rebuilt only when the count changes (the status Func is polled every refresh while the window is shown).
    private int _effStatusN = -1;
    private string _effStatusText = "";

    private string EffHidText()
    {
        int n = EffectFilterPatch.HiddenThisFight;
        if (n == _effStatusN) return _effStatusText;
        _effStatusN = n;
        _effStatusText = "On · hid " + n.ToString("N0", CultureInfo.InvariantCulture)
                       + (n == 1 ? " effect this fight" : " effects this fight");
        return _effStatusText;
    }
}
