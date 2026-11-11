using System;
using Stellar.Abstractions.Diagnostics;

namespace Stellar.MultiTool;

// Performance → "Reduce party list updates": throttles the game's party/raid frame Lua updates. Every member's
// HP/MaxHp/ShieldList watcher and ENowBuffList watcher calls TeamView:updateMemberData / :updateBuffInfo on each fire
// (×19 in a raid, ~4 % of the main thread in the 11-10 WPR). While active, both class methods are replaced by wrappers
// that only mark (view, charId) dirty; a Lua timer flushes the marks at 10 or 5 Hz by calling the ORIGINAL methods.
// Same information, ≤100–200 ms later; buff/debuff icons are never removed (they can be mechanic cues). Recipe + line
// refs: Performance-Levers.md "Lua attr-change watchers in raids". Port of the in-game-validated Experiment copy
// (Plugin.PartyThrottle*.cs @ 632d8a8) with its own Lua names; Lua half + the sharing rules: Plugin.PartyThrottle.Lua.cs.
//
// Lua side (compile-once, Plugin-Performance-Rules.md §4): ONE DoString defines _G functions; C# then only sends
// one-line calls. If the Lua state was rebuilt (relogin) the functions are nil → the ensure readback fails → re-define
// + re-install. No C# callback is ever handed to Lua.
//
// Gating (Game-Phases-and-Tick-Gating.md): ACTIVE = toggle on && world ready (Phase == World && IsWorldActive &&
// !Loading, the effect filter's EffWorldReady) && Lua ready && the shared WHERE scope allows (EffInScope: Everywhere, or
// a dungeon/raid run). A 1 Hz Framework.Every tick exists ONLY while the toggle is on; it is a few field reads, with Lua
// work only on the rising edge (install), on a scope drop (Off = originals restored at once) and every 10th tick (stats
// + a re-ensure that repairs a missed scene rebuild). Toggle off / Dispose → Off at once. Nothing runs per frame.
public sealed partial class Plugin
{
    private bool _ptOn;                  // "Reduce party list updates" (default OFF on first install)
    private int  _ptRate = 10;           // flush rate, Hz: 10 or 5
    private IDisposable? _ptTick;
    private bool _ptWasReady;            // world + scope gate on the previous tick (rising edge → ensure)
    private bool _ptActive;              // the last ensure succeeded and Off hasn't run since (status line)
    private bool _ptFailed;              // the last ensure failed (status line: "not available")
    private int  _ptSubTick;
    private bool _ptErrLogged, _ptInstallLogged;

    private const string PtOnKey   = "perf_party_reduce";
    private const string PtRateKey = "perf_party_rate";

    // One-line calls, prebuilt per rate (no string building per call). The ensure function writes __mtTvThrOk = true
    // on success (nil otherwise; type-strict readback via TryReadGlobalBool). Every global write is rawset (the game's
    // _G __newindex drops plain new globals).
    private const string PtEnsure10 =
        "rawset(_G,'__mtTvThrOk',nil) local f=rawget(_G,'__stellarMtTvThrEnsure') if f then f(10) end";
    private const string PtEnsure5 =
        "rawset(_G,'__mtTvThrOk',nil) local f=rawget(_G,'__stellarMtTvThrEnsure') if f then f(5) end";
    private const string PtOffFlush   = "local f=rawget(_G,'__stellarMtTvThrOff') if f then f(true) end";
    private const string PtOffNoFlush = "local f=rawget(_G,'__stellarMtTvThrOff') if f then f(false) end";
    private const string PtStats =
        "rawset(_G,'__mtTvThrIn',nil) rawset(_G,'__mtTvThrOut',nil) rawset(_G,'__mtTvThrPass',nil) " +
        "rawset(_G,'__mtTvThrErr',nil) local f=rawget(_G,'__stellarMtTvThrStats') if f then f() end";

    private void InitPartyThrottle()
    {
        _ptOn = _cfg.Get<bool>(PtOnKey, false);
        int r = _cfg.Get<int>(PtRateKey, 10);
        _ptRate = r == 5 ? 5 : 10;
        if (_ptOn) StartPtTick();   // at construction → never in world → install on the first world tick
    }

    private void DisposePartyThrottle()
    {
        StopPtTick();
        if (_ptOn) PtOff();
    }

    // ── settings (one save per click) ────────────────────────────────────────────────────────────────────────────
    private void TogglePartyThrottle()
    {
        _ptOn = !_ptOn;
        _cfg.Set<bool>(PtOnKey, _ptOn);
        _cfg.Save();
        if (!_ptOn) { StopPtTick(); PtOff(); return; }
        StartPtTick();
        PartyThrGateNow();
    }

    private void SetPartyRate(int hz)
    {
        if (hz != 5) hz = 10;
        if (_ptRate == hz) return;
        _ptRate = hz;
        _cfg.Set<int>(PtRateKey, hz);
        _cfg.Save();
        // Already installed → the ensure restarts the Lua timer at the new rate (S.hz ~= hz).
        if (_ptActive) PtEnsure("rate change");
    }

    private void StartPtTick()
    {
        _ptWasReady = false;
        _ptSubTick = 0;
        _ptTick ??= _services.Framework.Every(TimeSpan.FromSeconds(1), PartyThrTick);
    }

    private void StopPtTick()
    {
        _ptTick?.Dispose();
        _ptTick = null;
    }

    // Restore at once. Outside the world the pending marks are dropped instead of flushed (stale views).
    private void PtOff()
    {
        _ptWasReady = false;
        _ptFailed = false;
        bool wasActive = _ptActive;
        _ptActive = false;
        try
        {
            if (!_services.Lua.Ready) return;
            _services.Lua.DoString(EffWorldReady ? PtOffFlush : PtOffNoFlush);
            if (wasActive && StellarDiagnostics.IsEnabled) _services.Log.Info("[PerfParty] off (originals restored)");
        }
        catch (Exception ex) { _services.Log.Warning($"[PerfParty] off: {ex.Message}"); }
    }

    // World + Lua + the shared WHERE scope (Plugin.EffectFilter.cs EffInScope = the dungeon run id unless Everywhere).
    private bool PtGateOpen => EffWorldReady && _services.Lua.Ready && EffInScope;

    // Re-evaluate right away (toggle on, WHERE changed) instead of waiting ≤1 s for the tick.
    private void PartyThrGateNow()
    {
        if (!_ptOn) return;
        _ptWasReady = false;   // force the edge path: install now if open, Off now if not
        PartyThrTick();
    }

    // 1 Hz while the toggle is on: three ClientState reads + the run id. Lua work only on edges and every 10th tick.
    private void PartyThrTick()
    {
        if (!_ptOn) return;
        try
        {
            if (!EffWorldReady || !_services.Lua.Ready)
            {
                // Loading / not in world: touch nothing (no Lua). The next ready tick re-ensures (a relogin rebuilt
                // the Lua state → the ensure re-defines).
                _ptWasReady = false;
                return;
            }
            if (!EffInScope)
            {
                // Scope dropped (left the dungeon, or WHERE switched to Dungeons & raids in the open world) → Off.
                if (_ptActive || _ptWasReady) PtOff();
                return;
            }
            if (!_ptWasReady)
            {
                _ptWasReady = true;
                _ptSubTick = 0;
                PtEnsure("world entry");
                return;
            }
            if (++_ptSubTick < 10) return;
            _ptSubTick = 0;
            PtStatsTick();
            PtEnsure("10 s check");   // a scene rebuild / a wrapper drop the 1 Hz gate missed → re-install
        }
        catch (Exception ex) { _services.Log.Warning($"[PerfParty] tick: {ex.InnerException?.Message ?? ex.Message}"); }
    }

    // One-line ensure; on a failed readback (functions nil = fresh Lua state) define once and retry.
    private void PtEnsure(string why)
    {
        var lua = _services.Lua;
        string call = _ptRate == 5 ? PtEnsure5 : PtEnsure10;
        lua.DoString(call);
        if (lua.TryReadGlobalBool("__mtTvThrOk", out bool ok) && ok) { _ptActive = true; _ptFailed = false; return; }

        // Re-compile the definition only when it is really gone (fresh Lua state); a defined-but-failing ensure is a
        // real error (logged once below), not a reason to recompile every 10 s.
        bool ok2 = false;
        if (!(lua.TryReadGlobalBool("__stellarMtTvThrDefined", out bool defined) && defined))
        {
            lua.DoString(PartyThrLuaDefine);
            lua.DoString(call);
            ok2 = lua.TryReadGlobalBool("__mtTvThrOk", out bool v2) && v2;
        }
        _ptActive = ok2;
        _ptFailed = !ok2;
        if (ok2)
        {
            if (!_ptInstallLogged || why != "10 s check")
                _services.Log.Info($"[PerfParty] installed ({why}, {_ptRate}/s)");
            _ptInstallLogged = true;
        }
        else if (!_ptErrLogged)
        {
            _ptErrLogged = true;
            // No error text + no definition sentinel = the define chunk itself never registered (e.g. a plain _G
            // write dropped by the game's _G __newindex, or a compile error swallowed by DoString).
            bool def = lua.TryReadGlobalBool("__stellarMtTvThrDefined", out bool d2) && d2;
            _services.Log.Warning($"[PerfParty] install failed ({why}, defined={def}): " +
                                  $"{lua.ReadGlobalString("__mtTvThrErr") ?? "no error text"}");
        }
    }

    // Every 10 s while active: the first Lua error is always surfaced (once); the counters line (marks in vs original
    // calls out since the last read; counters reset in Lua) only with the framework diagnostics flag on.
    private void PtStatsTick()
    {
        if (!_ptActive) return;
        var lua = _services.Lua;
        lua.DoString(PtStats);
        if (!lua.TryReadGlobalNumber("__mtTvThrIn", out double marks)) return;   // not defined yet → the ensure follows
        if (StellarDiagnostics.IsEnabled)
        {
            lua.TryReadGlobalNumber("__mtTvThrOut", out double calls);
            lua.TryReadGlobalNumber("__mtTvThrPass", out double pass);
            _services.Log.Info($"[PerfParty] 10s: rate={_ptRate}/s marks={marks:0} calls={calls:0} " +
                               $"saved={marks - calls:0} passthrough={pass:0}");
        }
        if (_ptErrLogged) return;
        var err = lua.ReadGlobalString("__mtTvThrErr");
        if (string.IsNullOrEmpty(err)) return;
        _ptErrLogged = true;   // first Lua error only (no spam)
        _services.Log.Warning($"[PerfParty] Lua error: {err}");
    }
}
