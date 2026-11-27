using System;

namespace Stellar.MultiTool;

// Auto-close reward popup — closes the game's daily monthly-card popup (monthly_reward_card_window) while in the world,
// after first sending the server's "seen" ack, so it stops coming back at every login. The reward itself is already
// granted server-side; the popup only displays it (Lua-Injection-from-CSharp.md "Monthly-card daily popup"). Same
// behaviour as AutoGather 2980e9d / AutoFishing 8daaffb, but always on here (not only during automation). A second ack
// from one of those plugins is harmless (ignoreError=true) and CloseView on a closed window is a no-op → no coordination.
//
// com_rewards_window ("items received") is GENERIC (quests, events, mail…), so it is closed ONLY inside a short window
// (RpRewardsWatchSec) right after we closed the monthly card — the one that card queues — and only once per detection.
// Every other reward window stays up.
//
// Lua side (compile-once, Plugin-Performance-Rules.md §4): ONE DoString defines __stellarMtRpPoll; each poll is a
// one-line call + type-strict TryReadGlobalBool readbacks. If the Lua state was rebuilt (relogin) the function is nil →
// the __mtRpOk readback fails → re-define next poll. Every global write is rawset (the game's _G __newindex drops plain
// new globals). No C# callback is ever handed to Lua.
//
// Gating: a 0.5 s Framework.Every tick exists ONLY while the setting is on (off → no tick, no Lua at all); each tick
// returns at once unless world-ready (EffWorldReady: Phase == World && IsWorldActive && !Loading) and Lua is ready.
// Config: "reward_popup_autoclose" (bool, default RpDefaultOn). UI: the "Game UI Tweaks" hub card + window
// (Plugin.RewardPopupWindow.cs), whose toggle calls SetAutoCloseRewardPopup.
public sealed partial class Plugin
{
    // ⚠️ The default is undecided — keep it this single constant so flipping it is a one-line change.
    private const bool   RpDefaultOn      = false;
    private const string RpOnKey          = "reward_popup_autoclose";
    private const double RpRewardsWatchSec = 3.0;   // close com_rewards_window only this long after our card close

    private bool _rpOn;
    private IDisposable? _rpTick;
    private bool _rpDefined;        // the define chunk ran since the last failed readback
    private double _rpWatchUntil;   // TimeNow deadline of the com_rewards_window window; 0 = not watching
    private bool _rpErrLogged;

    // The poll function. Per call it clears the sentinels, then (all inside pcall):
    //  • monthly card active → on the FIRST poll of this showing only (S.acked latch, reset once it is seen closed): fire
    //    the ack in a coroutine exactly like the Get button's VM call (async → must run in a coro; true = ignoreError, no
    //    error tip) and set __mtRpCard; then CloseView (every poll while it is still up — a no-op once closed).
    //    GetVM('monthly_reward_card') only runs while the card is showing, so mod Lua is never the VM's first caller
    //    (Lua-Injection-from-CSharp.md "Z.DataMgr.Get caches BEFORE Init()").
    //  • watch == true and com_rewards_window active → CloseView it and set __mtRpRew.
    //  • __mtRpOk = true last, so C# can tell "function missing" from "nothing to do".
    // ⚠️ No statement may start with '(' (it would continue the previous call — Lua-Injection-from-CSharp.md), so the
    // calls are written Z.UIMgr:… rather than the decompiled (Z.UIMgr):… form.
    private const string RpDefine =
        "rawset(_G,'__stellarMtRpPoll',function(watch) " +
            "local S=rawget(_G,'__stellarMtRpS') if not S then S={} rawset(_G,'__stellarMtRpS',S) end " +
            "rawset(_G,'__mtRpCard',nil) rawset(_G,'__mtRpRew',nil) " +
            "pcall(function() " +
                "if Z.UIMgr:IsActive('monthly_reward_card_window') then " +
                    "if not S.acked then " +
                        "S.acked=true " +
                        "pcall(function() local vm=Z.VMMgr.GetVM('monthly_reward_card') " +
                            "if vm and vm.AsyncClickMonthlyCardTips then Z.CoroUtil.create_coro_xpcall(function() " +
                                "local cs=Z.CancelSource.Rent() vm:AsyncClickMonthlyCardTips(cs:CreateToken(),true) cs:Recycle() " +
                            "end)() end end) " +
                        "rawset(_G,'__mtRpCard',true) " +
                    "end " +
                    "Z.UIMgr:CloseView('monthly_reward_card_window') " +
                "else S.acked=nil end " +
                "if watch and Z.UIMgr:IsActive('com_rewards_window') then " +
                    "Z.UIMgr:CloseView('com_rewards_window') rawset(_G,'__mtRpRew',true) " +
                "end " +
            "end) " +
            "rawset(_G,'__mtRpOk',true) " +
        "end)";

    // One-line calls, prebuilt (no string building per poll).
    private const string RpPoll =
        "rawset(_G,'__mtRpOk',nil) local f=rawget(_G,'__stellarMtRpPoll') if f then f(false) end";
    private const string RpPollWatch =
        "rawset(_G,'__mtRpOk',nil) local f=rawget(_G,'__stellarMtRpPoll') if f then f(true) end";

    internal bool AutoCloseRewardPopup => _rpOn;

    private void InitRewardPopup()
    {
        _rpOn = _cfg.Get<bool>(RpOnKey, RpDefaultOn);
        _rewardPopupWindow = RegisterWindow("multitool.dailyrewards", "MultiTool: Game UI Tweaks", 8, BuildRewardPopupRoot());
        if (_rpOn) StartRpTick();   // at construction → never in world yet → the first world tick defines + polls
    }

    private void DisposeRewardPopup() => StopRpTick();

    // The window's toggle (Plugin.RewardPopupWindow.cs). One save per click (MultiTool-Plugin.md §4.1).
    internal void SetAutoCloseRewardPopup(bool on)
    {
        if (_rpOn == on) return;
        _rpOn = on;
        _cfg.Set<bool>(RpOnKey, on);
        _cfg.Save();
        if (on) StartRpTick(); else StopRpTick();
        _services.Log.Info($"[RewardPopup] auto-close {(on ? "on" : "off")}");
    }

    private void StartRpTick()
    {
        _rpWatchUntil = 0;
        _rpTick ??= _services.Framework.Every(TimeSpan.FromSeconds(0.5), RewardPopupTick);
    }

    private void StopRpTick()
    {
        _rpTick?.Dispose();
        _rpTick = null;
        _rpWatchUntil = 0;
    }

    private void RewardPopupTick()
    {
        if (!_rpOn || !EffWorldReady || !_services.Lua.Ready) { _rpWatchUntil = 0; return; }
        try
        {
            var lua = _services.Lua;
            if (!_rpDefined) { lua.DoString(RpDefine); _rpDefined = true; }

            double now = _services.Framework.TimeNow;
            bool watch = _rpWatchUntil > 0 && now < _rpWatchUntil;
            if (!watch) _rpWatchUntil = 0;

            lua.DoString(watch ? RpPollWatch : RpPoll);
            if (!(lua.TryReadGlobalBool("__mtRpOk", out var ok) && ok))
            {
                // Function lost (Lua state rebuilt) or the chunk failed → re-define on the next tick.
                _rpDefined = false;
                return;
            }

            // __mtRpCard is set only on the first poll of a showing → one log + one watch window per detection.
            if (lua.TryReadGlobalBool("__mtRpCard", out var card) && card)
            {
                _rpWatchUntil = now + RpRewardsWatchSec;
                _services.Log.Info("[RewardPopup] monthly card popup: sent seen-ack and closed it");
            }
            else if (watch && lua.TryReadGlobalBool("__mtRpRew", out var rew) && rew)
            {
                _rpWatchUntil = 0;   // only the one rewards window our card queued
                _services.Log.Info("[RewardPopup] closed the monthly card's rewards window");
            }
        }
        catch (Exception ex)
        {
            _rpDefined = false;
            if (!_rpErrLogged) { _rpErrLogged = true; _services.Log.Warning($"[RewardPopup] poll failed: {ex.Message}"); }
        }
    }
}
