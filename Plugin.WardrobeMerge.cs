using System;

namespace Stellar.MultiTool;

// Game UI tweak #2 — "Merge clothes and accessory tabs": the native Wardrobe (fashion_system) shows the 5 clothes slots
// and the 9 accessory slots as two columns side by side on the Clothes tab; the Accessories (ornament) tab is hidden;
// the Weapon tab is untouched. Pure Lua: the view's class methods are wrapped (Plugin.WardrobeMerge.Lua.cs has the
// mechanism + line refs; research in Fashion-Wardrobe-Screen.md). The game's own code does everything else — the item
// grid (OnSwitchRegion), head/body camera zoom per slot, per-row eye toggle, red dots, save.
//
// Applies on the NEXT Wardrobe open after turning it on (the clone is made in OnActive). Turning it off restores the
// game's methods at once, and an open Wardrobe drops back to its own two tabs.
//
// Lua side (compile-once, Plugin-Performance-Rules.md §4): ONE DoString defines the _G functions; the patch itself is
// a one-line call. Nothing polls the game: a 2 s Framework.Every tick exists ONLY while the setting is on (or an
// unpatch is still owed), and each tick is one type-strict TryReadGlobalBool of __mtWmPatched (+ one for the news
// flag) — the patch only re-runs when the sentinel is gone (Lua state rebuilt, e.g. a relogin), and only in the world.
// Logs ([GameUI] prefix): "Wardrobe merge: patched" per Lua state, the clone geometry line once per distinct value
// (for tuning WmCloneGap / placement in game), the first Lua error once.
// Config: "wardrobe_merge_tabs" (bool, default OFF). UI: the "Wardrobe" section of the Game UI window.
public sealed partial class Plugin
{
    private const string WmOnKey = "wardrobe_merge_tabs";

    private const string WmPatch   = "local f=rawget(_G,'__stellarMtWmPatch') if f then f() end";
    private const string WmUnpatch = "local f=rawget(_G,'__stellarMtWmUnpatch') if f then f() end";
    private const string WmClearNews = "rawset(_G,'__mtWmNews',nil) rawset(_G,'__mtWmDiag',nil)";

    private bool _wmOn;
    private bool _wmNeedUnpatch;    // turned off while Lua wasn't ready → unpatch on the first ready tick
    private IDisposable? _wmTick;
    private bool _wmErrLogged;
    private string? _wmLastDiag;

    internal bool WardrobeMergeTabs => _wmOn;

    private void InitWardrobeMerge()
    {
        _wmOn = _cfg.Get<bool>(WmOnKey, false);
        if (_wmOn) StartWmTick();   // at construction → never in world yet → the first world tick defines + patches
    }

    private void DisposeWardrobeMerge()
    {
        StopWmTick();
        // Pure-Lua wrappers would outlive the plugin → put the game's methods back.
        try { if (_wmOn && _services.Lua.Ready) _services.Lua.DoString(WmUnpatch); } catch { }
    }

    // The window's toggle (Plugin.RewardPopupWindow.cs). One save per click (MultiTool-Plugin.md §4.1).
    internal void SetWardrobeMergeTabs(bool on)
    {
        if (_wmOn == on) return;
        _wmOn = on;
        _cfg.Set<bool>(WmOnKey, on);
        _cfg.Save();
        _services.Log.Info($"[GameUI] Wardrobe merge {(on ? "on" : "off")}");
        _wmNeedUnpatch = !on;
        if (on) { StartWmTick(); WardrobeMergeTick(); return; }
        if (TryWmUnpatch()) StopWmTick(); else StartWmTick();
    }

    private void StartWmTick() => _wmTick ??= _services.Framework.Every(TimeSpan.FromSeconds(2), WardrobeMergeTick);

    private void StopWmTick()
    {
        _wmTick?.Dispose();
        _wmTick = null;
    }

    private bool TryWmUnpatch()
    {
        if (!_services.Lua.Ready) return false;
        try { _services.Lua.DoString(WmUnpatch); }
        catch (Exception ex) { _services.Log.Warning($"[GameUI] Wardrobe merge unpatch failed: {ex.Message}"); }
        _wmNeedUnpatch = false;
        return true;
    }

    private void WardrobeMergeTick()
    {
        if (!_wmOn)
        {
            // An owed unpatch (turned off before Lua was ready); stop the tick from outside its own callback.
            // Re-check _wmOn when the post runs: a quick off→on must keep its tick.
            if (!_wmNeedUnpatch || TryWmUnpatch()) _services.Framework.Post(() => { if (!_wmOn) StopWmTick(); });
            return;
        }
        if (!EffWorldReady || !_services.Lua.Ready) return;
        try
        {
            var lua = _services.Lua;
            if (!(lua.TryReadGlobalBool("__mtWmPatched", out var patched) && patched))
            {
                // Define only when it is really gone (fresh Lua state); a defined-but-failing patch is a real error.
                if (!(lua.TryReadGlobalBool("__stellarMtWmDefined", out var def) && def)) lua.DoString(WmLuaDefine);
                lua.DoString(WmPatch);
                if (lua.TryReadGlobalBool("__mtWmPatched", out var ok) && ok)
                    _services.Log.Info("[GameUI] Wardrobe merge: patched fashion_system_view");
            }
            WmDrainNews();
        }
        catch (Exception ex)
        {
            if (!_wmErrLogged) { _wmErrLogged = true; _services.Log.Warning($"[GameUI] Wardrobe merge tick failed: {ex.Message}"); }
        }
    }

    // Clone geometry (once per distinct value) and the first Lua error (once), both parked in Lua globals — injected
    // Lua's own logError goes to Player.log, not BepInEx (Lua-Injection-from-CSharp.md).
    private void WmDrainNews()
    {
        var lua = _services.Lua;
        if (!(lua.TryReadGlobalBool("__mtWmNews", out var news) && news)) return;
        var diag = lua.ReadGlobalString("__mtWmDiag");
        var err  = lua.ReadGlobalString("__mtWmErr");
        lua.DoString(WmClearNews);
        if (!string.IsNullOrEmpty(diag) && diag != _wmLastDiag)
        {
            _wmLastDiag = diag;
            _services.Log.Info("[GameUI] Wardrobe merge: " + diag);
        }
        if (!string.IsNullOrEmpty(err) && !_wmErrLogged)
        {
            _wmErrLogged = true;
            _services.Log.Warning("[GameUI] Wardrobe merge Lua error (game behaviour kept): " + err);
        }
    }
}
