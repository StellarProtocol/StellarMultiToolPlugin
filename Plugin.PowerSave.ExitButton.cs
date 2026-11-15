using System;
using System.Text;
using Stellar.Abstractions.Domain;

namespace Stellar.MultiTool;

// Power Saving — native "Exit" button on the power_saving_window logo screen (approved design A). A REAL game button
// (prefab loaded through the view's own UIBase:AsyncLoadUiUnit), bottom-right, labelled "Exit  [F8]" with the bound
// hotkey as a dimmed rich-text hint (omitted when unbound). Clicking it exits power save exactly like the hotkey.
// Prefab (user-picked after an in-game A/B): newbiebackflow_strategy_content_button_tpl (white rect button, btn_goto +
// lab_content; its star img_flag_icon is hidden). c_com_btn_second_tpl_2 is a stale path-table entry that never loads.
// Fallback dead_btn_tpl_pc: hide img_icon AND node_cost (else a stray "Loading" cost row sits above the button).
//
// ⚠️ PURE LUA CLICK + C# SENTINEL POLL. Native Lua→C# callbacks crash this IL2CPP game, so the button's listener is a
// pure Lua closure: it runs the exit body in Lua (instant feedback) and parks `__stellar_ps_exit_clicked = true`. C#
// polls that boolean at 10 Hz ONLY while a session is active and calls ExitPowerSave() (idempotent) so the C# side
// (sticky flag, HUD-overlay restore) catches up. The tick is one TryReadGlobalBool — no DoString unless clicked.
//
// The unit is released by the C# exit chunk (RemoveUiUnit), NOT by the click handler — releasing a button's
// GameObject from inside its own onClick invoke is avoidable risk; the C# run follows ≤0.1 s later.
public sealed partial class Plugin
{
    private IDisposable? _powerSaveExitPoll;
    // Diagnostics: confirms which prefab loaded + its binder fields in game, so the mount parks one summary string (prefab,
    // resolved btn/label, binder keys, or the error). Read it a bounded number of ticks per session, log once.
    private int _powerSaveExitDiagTicks;

    private const string PowerSaveExitUnit = "stellar_ps_exit";

    // Spliced into PowerSaveEnterChunk's SUCCESS branch (locals in scope: m = Z.UIMgr, v = view, b = v.uiBinder), so
    // the button never appears when the enter fails. Mounts the button in a coroutine (AsyncLoadUiUnit yields + needs
    // the open view's cancelSource). No cursor handling — the cursor is already free on this screen. Idempotent: an existing
    // unit is returned as-is by AsyncLoadUiUnit; the busy flag stops a second coroutine racing the first load (a
    // second AsyncLoadUiUnit call would RemoveUiUnit the in-flight one). Listener wiring is tracked in a weak-keyed
    // table so a reused unit (cached view re-opened) never gets a second listener.
    internal const string PowerSaveExitButtonMountLua = @"
    if not rawget(v, '__stellar_ps_exit_busy') then
      rawset(v, '__stellar_ps_exit_busy', true)
      ;(Z.CoroUtil.create_coro_xpcall)(function()
        local parent = b.Trans or (v.panel and v.panel.Trans)
        local u
        -- Hide a decorative child via SetActive(false) on its GameObject. ⚠️ Ref:SetVisible returns OK but does
        -- NOTHING on these unit binders (verified in game 2026-11-15: star stayed visible), so don't rely on it.
        local hide = function(n)
          if n == nil then return end
          pcall(function() n.gameObject:SetActive(false) end)
        end
        u = v:AsyncLoadUiUnit('ui/prefabs/newbiebackflow/newbiebackflow_strategy_content_button_tpl', 'stellar_ps_exit', parent)
        local prefab = 'newbiebackflow_strategy_content_button_tpl'
        if u then
          hide(u.img_flag_icon)
        else
          u = v:AsyncLoadUiUnit('ui/prefabs/dead/dead_btn_tpl_pc', 'stellar_ps_exit', parent)
          prefab = 'dead_btn_tpl_pc'
          if u then hide(u.img_icon); hide(u.node_cost) end
        end
        rawset(v, '__stellar_ps_exit_busy', nil)
        if u == nil then rawset(_G, '__stellar_ps_exit_diag', 'load failed (both prefabs)'); return end
        local btn = u.btn_goto or u.btn or u.btn_icon
        local lab = u.lab_content or u.lab_name
        pcall(function()
          local t = u.Trans
          t:SetAnchors(1, 1, 0, 0)
          t:SetPivot(1, 0)
          t:SetAnchorPosition(-40, 40)
          t:SetAsLastSibling()
        end)
        if lab then
          pcall(function()
            local txt = 'Exit'
            if Z.LangMgr and Z.LangMgr:IsContainKey('Exit') then txt = Lang('Exit') end
            lab.text = txt .. (rawget(_G, '__stellar_ps_exit_hint') or '')
          end)
        end
        local w = rawget(_G, '__stellar_ps_exit_wired')
        if w == nil then w = setmetatable({}, { __mode = 'k' }); rawset(_G, '__stellar_ps_exit_wired', w) end
        if btn and not w[btn] then
          w[btn] = true
          btn:AddListener(function()
            pcall(function()
              local pvm = Z.VMMgr and (Z.VMMgr.GetVM)('power_save')
              if pvm and pvm.ExitPowerSaveMode then (pvm.ExitPowerSaveMode)() end
              local um = Z.UIMgr
              if um then
                um:CloseView('power_saving_window')
              end
              rawset(_G, '__stellar_ps_entered', false)
            end)
            rawset(_G, '__stellar_ps_exit_clicked', true)
          end)
        end
        if not rawget(_G, '__stellar_ps_exit_diag_done') then
          rawset(_G, '__stellar_ps_exit_diag_done', true)
          local ks = {}
          pcall(function() for k, _ in pairs(u) do ks[#ks + 1] = tostring(k) end end)
          rawset(_G, '__stellar_ps_exit_diag', 'prefab=' .. prefab .. ' btn=' .. tostring(btn ~= nil) ..
            ' lab=' .. tostring(lab ~= nil) .. ' keys=' .. table.concat(ks, ','))
        end
      end, function(err)
        rawset(v, '__stellar_ps_exit_busy', nil)
        rawset(_G, '__stellar_ps_exit_diag', 'mount error: ' .. tostring(err))
      end)()
    end
";

    // Spliced into PowerSaveExitChunk BEFORE CloseView (local m = Z.UIMgr in scope, non-nil). Release our unit
    // (belt-and-braces — the view's UnLoad also clears units) and clear the click sentinel. pcall'd + idempotent.
    internal const string PowerSaveExitButtonTeardownLua = @"
    pcall(function()
      local ev = m:GetView('power_saving_window')
      if ev then ev:RemoveUiUnit('stellar_ps_exit') end
    end)
    rawset(_G, '__stellar_ps_exit_clicked', false)
";

    // Called from EnterPowerSave: publish the hotkey hint (read fresh each enter so a rebind is picked up), clear the
    // click sentinel, and start the 10 Hz sentinel poll. One string build per ENTER — not a hot path.
    private void StartPowerSaveExitButton()
    {
        var hint = "";
        try
        {
            if (_powerSaveHotkey is IHotkeyAction ha && ha.CurrentBinding is { } kb)
                hint = "  <color=#c9d5e3><size=80%>[" + kb + "]</size></color>";
        }
        catch { /* no hint */ }

        _services.Lua.DoString(
            "pcall(function() rawset(_G, '__stellar_ps_exit_clicked', false); rawset(_G, '__stellar_ps_exit_hint', '"
            + LuaEscape(hint) + "') end)");

        _powerSaveExitDiagTicks = 0;
        StopPowerSaveExitPoll();
        _powerSaveExitPoll = _services.Framework.Every(TimeSpan.FromSeconds(0.1), PollPowerSaveExitButton);
    }

    private void PollPowerSaveExitButton()
    {
        if (!_powerSaveActive || !_services.Lua.Ready) return;
        if (_services.Lua.TryReadGlobalBool("__stellar_ps_exit_clicked", out var clicked) && clicked)
        {
            _services.Log.Info("[MultiTool] PowerSave: Exit button clicked → exit");
            ExitPowerSave();   // exit chunk resets the sentinel + releases the unit; disposes this poll
            return;
        }
        // Bounded diag read (~5 s per session): one log line confirming the prefab's binder fields in game.
        if (_powerSaveExitDiagTicks < 50)
        {
            _powerSaveExitDiagTicks++;
            var diag = _services.Lua.ReadGlobalString("__stellar_ps_exit_diag");
            if (diag != null)
            {
                _powerSaveExitDiagTicks = 50;
                _services.Log.Info($"[MultiTool] PowerSave: Exit button {diag}");
                _services.Lua.DoString("pcall(function() rawset(_G, '__stellar_ps_exit_diag', nil) end)");
            }
        }
    }

    private void StopPowerSaveExitPoll()
    {
        _powerSaveExitPoll?.Dispose();
        _powerSaveExitPoll = null;
    }

    // Escape for a single-quoted Lua string literal. KeyBinding.ToString is "Ctrl+Alt+P"-style, but never trust it.
    private static string LuaEscape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\'': sb.Append("\\'"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\0': break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
