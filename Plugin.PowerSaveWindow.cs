using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// UI for the Power Saving tool (mechanism: Plugin.PowerSave.cs + Plugin.PowerSave.ExitButton.cs). Built to the approved
// prototype (option A, user-approved 2026-11-15, _scratch\prototypes\multitool-power-saving-APPROVED.md):
// ToolHeader (moon icon, title, live On/Off), one SCREENSAVER panel (muted description, muted hotkey line, full-width
// Primary "Enter power saving"), then a full-width Neutral "Close".
//
// "Enter power saving" = the hotkey's EnterPowerSave(), after closing the game's Esc menu. MultiTool windows stay open
// (user request 2026-11-16); instead the pill is RECESSED + inert while power saving is active and comes back live when
// it ends by any path (Exit button, hotkey, logout), since it polls _powerSaveActive.
// The Esc menu is the `mainui_funcs_list` view (ui_config.lua: PrefabPath main/main_funcs_list_window; the Esc
// key / bottom-bar btn_esc → GotoMainUIFunc(MainFuncMenu) opens it, and the game closes it with
// Z.UIMgr:CloseView('mainui_funcs_list') — mainui_funcs_list_vm.lua CloseView, main_line_window_view.lua:33).
public sealed partial class Plugin
{
    private IWindowControl _powerSaveWindow = null!;

    private static byte[]? PowerSaveIconPng() => CachedPng("icon-powersave.png");

    // Close the game's Esc menu if it is open. pcall'd: a missing view/manager just does nothing.
    private const string CloseEscMenuChunk =
@"pcall(function()
  local m = Z.UIMgr
  if m and m:IsActive('mainui_funcs_list') then m:CloseView('mainui_funcs_list') end
end)";

    private HudElement BuildPowerSaveRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader(PowerSaveIconPng, "Power Saving", PowerSaveWindowStatus()),
        SectionPanel(() => "Screensaver", null,
            new TextElement(() => "Lowers the frame rate and covers the screen with a screensaver while you're away. " +
                                  "Leave with the Exit button or your hotkey.",
                Color: () => MutedColor()),
            new TextElement(PowerSaveHotkeyLine, Color: () => MutedColor()),
            PrimaryButton(() => "Enter power saving", OnPowerSaveEnterClick, enabled: () => !_powerSaveActive)),
        NeutralButton(() => "Close", () => _powerSaveWindow.SetVisible(false)),
    }, Gap: 8f);

    // Hub card status: "On" (green) while active, else "Screensaver · hotkey <key>" / "Screensaver · no hotkey" (muted).
    private HudElement PowerSaveStatus() => new TextElement(
        () => _powerSaveActive ? "On" : PowerSaveBindTexts().Hub,
        Color: () => _powerSaveActive ? StatusOn : MutedColor(), NoWrap: true);

    // Window header status: "On" (green) / "Off" (muted).
    private HudElement PowerSaveWindowStatus() => new TextElement(() => _powerSaveActive ? "On" : "Off",
        Color: () => _powerSaveActive ? StatusOn : MutedColor(), NoWrap: true);

    private string PowerSaveHotkeyLine() => PowerSaveBindTexts().Line;

    // The two binding-dependent strings, rebuilt only when the binding changes (a rebind in Settings › Hotkeys shows on
    // the next refresh). The Funcs above are polled on every window refresh, so no per-poll string building.
    private bool _psBindCached;
    private KeyBinding? _psBindKey;
    private (string Hub, string Line) _psBindText;

    private (string Hub, string Line) PowerSaveBindTexts()
    {
        KeyBinding? kb = null;
        try { if (_powerSaveHotkey is IHotkeyAction ha) kb = ha.CurrentBinding; } catch { /* treat as unbound */ }
        if (_psBindCached && kb == _psBindKey) return _psBindText;
        _psBindCached = true;
        _psBindKey = kb;
        _psBindText = kb is { } k
            ? ($"Screensaver · hotkey {k}", $"Hotkey {k} · change it in Settings › Hotkeys")
            : ("Screensaver · no hotkey", "No hotkey · set one in Settings › Hotkeys");
        return _psBindText;
    }

    // World only (Z.UIMgr / the Lua VMs aren't wired elsewhere; the window only renders in World anyway). Posted to the
    // next tick so the Lua + power-save entry don't run from inside the pill's own click; re-checked there. The
    // _powerSaveActive guard also covers a click racing the recessed swap (the pill is inert while active).
    private void OnPowerSaveEnterClick()
    {
        if (_powerSaveActive || _services.ClientState.Phase != GamePhase.World) return;
        _services.Framework.Post(() =>
        {
            if (_powerSaveActive || _services.ClientState.Phase != GamePhase.World) return;
            try { if (_services.Lua.Ready) _services.Lua.DoString(CloseEscMenuChunk); }
            catch (Exception ex) { _services.Log.Warning($"[MultiTool] PowerSave: close Esc menu failed: {ex.Message}"); }
            _services.Log.Info("[MultiTool] PowerSave: Enter button → enter");
            EnterPowerSave();
        });
    }
}
