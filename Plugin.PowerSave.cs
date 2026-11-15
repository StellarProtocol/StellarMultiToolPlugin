using System;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// Power Saving — surfaces BPSR's mobile-only AFK "screensaver" on PC as a STICKY hotkey toggle. The game caps FPS +
// reduces quality (QualityGradeSetting.IsSavePowerMode), blocks steering, and occludes the 3D world behind a
// full-screen login logo. It's gated OFF on PC only at the SERVICE layer — SavePowerService and
// PowerSaveVM.OpenPowerSaveMode/ClosePowerSaveMode early-return on PC. The underlying pieces are NOT gated: the
// view `power_saving_window` and the VM's EnterPowerSaveMode/ExitPowerSaveMode all work on PC, so we drive those
// directly and never route through the gated Open/ClosePowerSaveMode (they no-op on PC).
//
// Ported 1:1 from StellarMahiruUtilityPlugin (fix/powersave-exit-on-logout @ 5a98979, user-validated in game
// 2026-11-15); the Lua chunks are unchanged. MultiTool adds a tool window (Plugin.PowerSaveWindow.cs) whose "Enter
// power saving" pill runs the same EnterPowerSave() as the hotkey. Press the hotkey → enter power-save and STAY (input
// does not exit — sticky); press again → exit. No AFK idle auto-trigger, no exit-on-input watcher, no config. The
// in-game UI is a native game "Exit [key]" button injected into the power_saving_window view
// (Plugin.PowerSave.ExitButton.cs): its click runs PURE Lua (Lua→C# callbacks crash) and parks a sentinel that a
// 10 Hz C# poll turns into ExitPowerSave(). (The cursor is already free on that screen.)
//
// AUTO-EXIT on leaving the world: a disconnect → title screen used to leave the logo covering the login screen, and
// the (world-gated) hotkey couldn't clear it → stuck. Now the session is torn down SYNCHRONOUSLY on
// IClientState.Logout AND, as a safety net, on PhaseChanged To=TitleScreen/CharSelect (a disconnect may not raise
// Logout; the TitleScreen promotion fires exactly when the login view appears). NOT via Framework.Post — Update is
// world-gated, so a posted exit would only run at the next login. World→world loading (zone change / teleport) keeps
// Phase==World, so the session deliberately PERSISTS through it — never exit on IsWorldActive/Loading. The hotkey
// still only ENTERS in World, but EXITS in any phase as an escape hatch.
//
// Two behaviors from the game's Lua:
//   * The VM's EnterPowerSaveMode()/ExitPowerSaveMode() are DOT calls (no `self`); they ONLY set
//     QualityGradeSetting.IsSavePowerMode + block/unblock steer. They do NOT touch the logo — so calling
//     EnterPowerSaveMode alone caps FPS + blocks steer but shows no logo.
//   * The LOGO is view-driven: power_saving_window shows rimg_bg + sets IsFullScreen=true + UpdateCameraState() +
//     EnterPowerSaveMode() on inactivity. We replicate that path ourselves so the logo appears immediately.
//
// All state lives on the Z.* singleton / view / VM tables reached via the game's own accessors — never _G (except
// the rawset `__stellar_ps_*` sentinels, which bypass the strict-_G __newindex guard).
public sealed partial class Plugin
{
    // Sticky ON/OFF flag — WE own the state (hotkey-driven), so we never poll the game's IsSavePowerMode to decide
    // enter vs exit. Only a second hotkey press flips it back off.
    private bool _powerSaveActive;
    // Set by the Logout exit: re-run the (idempotent) exit chunk once the login/char-select screen appears, in case
    // the logout-time run was skipped (Lua not Ready) or threw mid-teardown. Cleared on World entry and on enter.
    private bool _powerSavePendingTitleCleanup;
    // HUD-overlay suppression state. Tracks whether WE hold PerfControls.MasterHudKill on, plus a snapshot of the
    // prior value so we restore exactly what we changed and never leave overlays suppressed on unload.
    private bool _modUiHidden;   // true while WE hold MasterHudKill on
    private bool _hudKillPrev;   // snapshot of MasterHudKill before we set it (respect + restore a pre-existing kill)
    private IDisposable? _powerSaveHotkey;
    // Immediate-trigger only: the prefab mounts async, so EnterPowerSaveMode right after OpenView can run before the
    // view binder exists (logo won't show / nil-ref). Retry the enter until the binder is up, bounded.
    private IDisposable? _powerSaveTriggerRetry;
    private int _powerSaveTriggerAttempts;

    // Enter chunk. Opens the view if needed, then — only once the async-mounted uiBinder exists — replicates the
    // view's own inactivity path so the LOGO actually appears (EnterPowerSaveMode alone wouldn't show it): rimg_bg
    // visible + IsFullScreen + UpdateCameraState + EnterPowerSaveMode. Parks a boolean sentinel on success so C# can
    // stop the retry. Defensive `;` before paren-led lines to avoid Lua's call-continuation misparse.
    //
    // ⚠️ STICKY-EXIT BUG FIX: the view's own ZInputDetector (uiBinder.input_detector) arms an ACTIVITY listener
    // after its inactivity cycle that exits the screensaver on the next input — so an idle mouse-move would cancel
    // our sticky session. `input_detector:StartDetection(false)` (ZInputDetector, [ForceToLua] → COLON call) halts
    // its Update loop, killing BOTH the auto-exit and the auto-reenter, while leaving rimg_bg/rimg_logo untouched
    // (separate binder object). Re-applied on EVERY enter because the view's OnActive re-registers listeners +
    // calls StartDetection(true) fresh each open. No exit restore needed — CloseView → OnDeActive →
    // input_detector:UnInit() tears it down fully.
    private const string PowerSaveEnterChunk =
@"pcall(function()
  local m = Z.UIMgr
  if not m then return end
  if not m:IsActive('power_saving_window') then m:OpenView('power_saving_window') end
  local v = m:GetView('power_saving_window')
  local vm = Z.VMMgr and (Z.VMMgr.GetVM)('power_save')
  if v and v.uiBinder and vm then
    local b = v.uiBinder
    if b.rimg_bg then (b.Ref):SetVisible(b.rimg_bg, true) end
    if v.ViewConfig then v.ViewConfig.IsFullScreen = true end
    m:UpdateCameraState()
    ;(vm.EnterPowerSaveMode)()
    if b.input_detector then (b.input_detector):StartDetection(false) end
    rawset(_G, '__stellar_ps_entered', true)
" + PowerSaveExitButtonMountLua + @"
  end
end)";

    // Exit chunk — fully CLOSE the view. ⚠️ THE CURSOR TRAP: hiding only the rimg_bg CHILD never calls the VIEW's
    // own SetVisible(false), so UpdateAfterVisibleChanged → UpdateMouseVisible never runs and the UI mouse-lock
    // source stays latched FREE — cursor stays loose, camera control lost. CloseView → DeActive → SetVisible(false)
    // → UpdateAfterVisibleChanged runs UpdateMouseVisible + UpdateCameraState + audio for free, so the cursor
    // re-locks and camera control returns. ExitPowerSaveMode first so flag/steer clear even if the view never mounted.
    private const string PowerSaveExitChunk =
@"pcall(function()
  local vm = Z.VMMgr and (Z.VMMgr.GetVM)('power_save')
  if vm and vm.ExitPowerSaveMode then (vm.ExitPowerSaveMode)() end
  local m = Z.UIMgr
  if m then
" + PowerSaveExitButtonTeardownLua + @"
    m:CloseView('power_saving_window')
  end
  rawset(_G, '__stellar_ps_entered', false)
end)";

    private void InitPowerSave()
    {
        _powerSaveHotkey = _services.Hotkeys.DeclareAction(
            // Default End (user request, 2026-11-15) — the player can rebind or clear it in Settings › Hotkeys.
            new HotkeyAction("multitool.powersave-toggle", "Power Saving: Toggle Screensaver",
                new KeyBinding(StellarKeyCode.End, ModifierKeys.None)),
            // ENTER only in-world (Z.UIMgr / the Lua VMs aren't wired at title/char-select), but EXIT in any phase —
            // escape hatch if an auto-exit edge was ever missed and the logo is stuck over a non-world screen.
            callback: () =>
            {
                if (_powerSaveActive) ExitPowerSave();
                else if (_services.ClientState.Phase == GamePhase.World) EnterPowerSave();
            });
        _services.ClientState.Logout += OnPowerSaveLogout;
        _services.ClientState.PhaseChanged += OnPowerSavePhaseChanged;
        _powerSaveWindow = RegisterWindow("multitool.powersave", "MultiTool: Power Saving", 7, BuildPowerSaveRoot());
    }

    // Primary edge: raised from the game's OnLogout (main thread, once). Tear down inline — see header (no Post).
    private void OnPowerSaveLogout()
    {
        if (!_powerSaveActive) return;
        _services.Log.Info("[MultiTool] PowerSave: left world (Logout) → exit");
        // ExitPowerSave clears _powerSaveActive even if the Lua chunk was skipped (not Ready) or threw mid-teardown,
        // which would make the TitleScreen safety net early-return with the logo still up. Remember to re-run it.
        _powerSavePendingTitleCleanup = true;
        ExitPowerSave();
    }

    // Safety net: a disconnect may skip Logout, but the phase is promoted to TitleScreen when the login view actually
    // appears — the exact moment the user would be stuck. Only title/char-select count; world→world loads stay World.
    private void OnPowerSavePhaseChanged(PhaseChange change)
    {
        if (change.To == GamePhase.World) { _powerSavePendingTitleCleanup = false; return; }   // never fire stale later
        if (change.To != GamePhase.TitleScreen && change.To != GamePhase.CharSelect) return;
        if (_powerSaveActive)
        {
            _powerSavePendingTitleCleanup = false;
            _services.Log.Info($"[MultiTool] PowerSave: left world (phase {change.From} → {change.To}) → exit");
            ExitPowerSave();
        }
        else if (_powerSavePendingTitleCleanup)
        {
            // Logout already reset C# state; the exit chunk is idempotent, so re-run it now that Lua is settled.
            _powerSavePendingTitleCleanup = false;
            _services.Log.Info("[MultiTool] PowerSave: title reached → re-run exit chunk");
            RunPowerSaveExitChunk();
        }
    }

    private void DisposePowerSave()
    {
        try { _services.ClientState.Logout -= OnPowerSaveLogout; } catch { /* swallow */ }
        try { _services.ClientState.PhaseChanged -= OnPowerSavePhaseChanged; } catch { /* swallow */ }
        StopPowerSaveTrigger();
        StopPowerSaveExitPoll();
        _powerSaveHotkey?.Dispose();
        // NEVER leave the player throttled / steer-blocked / logo-covered / HUD-hidden when the plugin unloads.
        if (_powerSaveActive) ExitPowerSave();
    }

    // Enter a sticky power-save session. Suppress HUD overlays immediately, open the view, then poll the enter chunk
    // until the async-mounted binder exists (bounded ~2.5s) so the logo reliably appears.
    private void EnterPowerSave()
    {
        _powerSaveActive = true;
        _powerSavePendingTitleCleanup = false;   // a fresh session supersedes any leftover logout re-run
        SuppressHudOverlays();
        _services.Lua.DoString(
@"pcall(function()
  rawset(_G, '__stellar_ps_entered', false)
  local m = Z.UIMgr
  if m and not m:IsActive('power_saving_window') then m:OpenView('power_saving_window') end
end)");
        StartPowerSaveExitButton();   // hotkey hint + clear click sentinel + 10 Hz click poll (button mounts on enter success)
        _powerSaveTriggerAttempts = 0;
        StopPowerSaveTrigger();
        _powerSaveTriggerRetry = _services.Framework.Every(TimeSpan.FromSeconds(0.1), () =>
        {
            if (!_services.Lua.Ready) return;
            _services.Lua.DoString(PowerSaveEnterChunk);
            if (_services.Lua.TryReadGlobalBool("__stellar_ps_entered", out var entered) && entered)
            { StopPowerSaveTrigger(); return; }
            if (++_powerSaveTriggerAttempts >= 25) StopPowerSaveTrigger();   // give up after ~2.5s (never mounted)
        });
    }

    // Exit the sticky session: stop any pending enter retry, close the view (recomputes mouse-lock/camera/audio so
    // the cursor re-hides and camera control returns), then restore the HUD overlays we suppressed. C# state resets
    // unconditionally: on the logout path the world is tearing down, so Lua may be not-Ready or throw — the exit chunk
    // is idempotent, and the C# side (flag, retry, MasterHudKill) must never stay latched on.
    private void ExitPowerSave()
    {
        StopPowerSaveTrigger();
        StopPowerSaveExitPoll();
        _powerSaveActive = false;
        RunPowerSaveExitChunk();
        RestoreHudOverlays();
    }

    // Ready-guarded + swallowed so callers' C# resets always run; safe to repeat (chunk is pcall'd + idempotent).
    private void RunPowerSaveExitChunk()
    {
        try { if (_services.Lua.Ready) _services.Lua.DoString(PowerSaveExitChunk); }
        catch (Exception ex) { _services.Log.Warning($"[MultiTool] PowerSave: exit chunk failed: {ex.Message}"); }
    }

    private void StopPowerSaveTrigger()
    {
        _powerSaveTriggerRetry?.Dispose();
        _powerSaveTriggerRetry = null;
    }

    // ── framework-wide HUD-overlay suppression (selective; no framework change) ─────────────────────────────────
    // While power-save is active, suppress every plugin's HUD OVERLAYS (combat meter, telegraphs, on-screen
    // readouts) by setting the framework flag PerfControls.MasterHudKill, which WindowRenderer applies ONLY to
    // windows whose Spec.Category == WindowCategory.HUD. Regular plugin windows are Tools and dev windows Debug, so
    // menus/tools stay openable. MultiTool references SDK 2.28, which has the property, so it is used DIRECTLY (the
    // MahiruUtility original reflects because its SDK 2.0 predates it). We snapshot the prior value and restore to it,
    // so a pre-existing HUD-kill is respected.
    private void SuppressHudOverlays()
    {
        try
        {
            if (!_modUiHidden)
            {
                _hudKillPrev = PerfControls.MasterHudKill;   // snapshot once, before we take ownership
                _modUiHidden = true;
            }
            PerfControls.MasterHudKill = true;
        }
        catch (Exception ex) { _services.Log.Warning($"[MultiTool] PowerSave: HUD-overlay suppress failed: {ex.Message}"); }
    }

    private void RestoreHudOverlays()
    {
        if (!_modUiHidden) return;    // no-op if we never suppressed
        try { PerfControls.MasterHudKill = _hudKillPrev; } catch { }
        _modUiHidden = false;
    }
}
