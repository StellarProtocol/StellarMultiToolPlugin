using System;
using System.Globalization;
using Stellar.Abstractions.Domain;

namespace Stellar.MultiTool;

// Effect Filter: skip loading other players' skill effects (mechanism + rule: EffectFilterPatch.cs; window:
// Plugin.EffectFilterWindow.cs). This partial owns the settings, the lazy patch install and a 1 s tick.
//
// The tick (Framework.Update + accumulator — SDK 2.28 has no IFramework.Every) and the SceneChanged / Logout /
// CombatState handlers are SUBSCRIBED only while some setting can hide (SyncEffSubscriptions) — otherwise the filter is
// fully inert. The tick refreshes the world gate at 10 Hz; at 1 Hz it refreshes my uuid + the entity-manager wrapper,
// re-evaluates Active (scope = dungeon run id), retries the install every 10 s until the game types bind, and flushes
// the diagnostics line. Fight boundaries: CombatState.Changed(true) resets the "hid N this fight" counter;
// SceneChanged resets the counter and every cache. All handlers are O(1) apart from clearing the caches once per scene.
//
// ⚠️ WORLD GATE (Game-Phases-and-Tick-Gating.md "Patching ZEffect.LoadEffect before world entry breaks login"):
// installing the LoadEffect prefix at plugin construction / the title screen made the next world entry fail
// ("disconnected [50000]" / "Scene switch failed"). So NOTHING here binds game types, installs the patch or touches
// ZEntityMgr until Phase == World && IsWorldActive && !Loading — a persisted-ON filter installs on its first world
// tick. Once installed, EffectFilterPatch.WorldReady keeps the prefix inert outside that state: the framework tick
// only runs in-world, so the falling edge comes from SceneChanged (OnLeaveScene) / Logout, the rising edge from the tick.
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
    private float _effTick, _effGateTick;
    private long  _effLastInstallTry, _effDiagStart, _effLastMe;
    private bool  _effSubscribed;

    private const float EffGateInterval  = 0.1f;   // world gate: 10 Hz (three field reads)
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
        // Construction is never in world: subscribe only (when something can hide); the install waits for the first
        // world tick.
        SyncEffSubscriptions();

        // The help popover first: the window's "?" fill funcs read _tipWindow.
        InitHelpTip();
        _effectWindow = RegisterWindow("multitool.effectfilter", "Effect Filter", 4, BuildEffectFilterRoot(),
            EffWindowWidth);
        _tipOwner = _effectWindow;
    }

    private void DisposeEffectFilter()
    {
        EffectFilterPatch.Active = false;
        EffectFilterPatch.WorldReady = false;
        UnsubscribeEff();
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
        SyncEffSubscriptions();
        if (EffAnyHide && EffWorldReady)
        {
            TryInstallEffectFilter();
            // The tick doesn't run while nothing can hide, so "me" may be stale (another character since) — re-read.
            RefreshEffMe();
            EffectFilterPatch.RefreshManager();
        }
        RefreshEffWorldGate();
        UpdateEffActive();
    }

    // Handlers live only while something can hide; nothing-can-hide = zero per-frame or per-event work.
    private void SyncEffSubscriptions()
    {
        if (!EffAnyHide) { UnsubscribeEff(); return; }
        if (_effSubscribed) return;
        _services.Framework.Update         += OnEffectFilterUpdate;
        _services.ClientState.SceneChanged += OnEffectSceneChanged;
        _services.ClientState.Logout       += OnEffectLogout;
        _services.CombatState.Changed      += OnEffectCombatChanged;
        _effSubscribed = true;
    }

    private void UnsubscribeEff()
    {
        if (!_effSubscribed) return;
        try { _services.Framework.Update         -= OnEffectFilterUpdate; } catch { }
        try { _services.ClientState.SceneChanged -= OnEffectSceneChanged; } catch { }
        try { _services.ClientState.Logout       -= OnEffectLogout; } catch { }
        try { _services.CombatState.Changed      -= OnEffectCombatChanged; } catch { }
        _effSubscribed = false;
    }

    // The ONLY state in which game types may be bound, the patch installed or ZEntityMgr touched.
    private bool EffWorldReady
    {
        get
        {
            var cs = _services.ClientState;
            return cs.Phase == GamePhase.World && cs.IsWorldActive && (cs.UiState & GameUIState.Loading) == 0;
        }
    }

    private void RefreshEffWorldGate() => EffectFilterPatch.WorldReady = EffectFilterPatch.Installed && EffWorldReady;

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
    // Called only while EffWorldReady (see the class note); a failed type lookup retries from the tick every 10 s.
    private void TryInstallEffectFilter()
    {
        _effLastInstallTry = Environment.TickCount64;
        if (EffectFilterPatch.Installed || EffectFilterPatch.HardFailed) return;
        _effHarmony ??= _services.Harmony.Create("effectfilter");
        if (!EffectFilterPatch.Install(_effHarmony, _services.Log.Info)) return;
        _effDiagStart = Environment.TickCount64;
        RefreshEffMe();
        EffectFilterPatch.RefreshManager();
        RefreshEffWorldGate();
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
        _effGateTick += dt;
        if (_effGateTick < EffGateInterval) return;
        _effGateTick = 0f;
        bool ready = EffWorldReady;
        EffectFilterPatch.WorldReady = ready && EffectFilterPatch.Installed;
        if (!ready) return;   // loading / not in world: no binding, no install, no ZEntityMgr
        _effTick += EffGateInterval;
        if (_effTick < EffTickInterval) return;
        _effTick = 0f;
        try
        {
            long now = Environment.TickCount64;
            if (!EffectFilterPatch.Installed)
            {
                // First world tick installs at once (persisted ON); a failed type lookup retries every 10 s.
                if (_effLastInstallTry == 0 || now - _effLastInstallTry >= EffInstallRetryMs) TryInstallEffectFilter();
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
        // Leaving a scene flips IsWorldActive false BEFORE this event, so the live re-read drops the gate at once (the
        // tick is frozen until the next world entry); entering a scene usually still shows Loading → stays closed.
        RefreshEffWorldGate();
        UpdateEffActive();
    }

    private void OnEffectLogout()
    {
        EffectFilterPatch.WorldReady = false;
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
