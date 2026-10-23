using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// MultiTool — a hub of small, self-contained tools that don't each warrant their own plugin. A single "MultiTool"
// launcher tile opens a hub window whose buttons each open one sub-menu window per tool.
//
// Currently:
//   • Band Mute — mute specific other players' band instruments.
//   • Auto-Accept Invites — auto-accept party invites / join requests / social invites (whitelist-only); one Players
//     roster with per-person Party/Social switches; "Clear all" asks for confirmation.
//   • Profile-card "Auto-Accept" button — opens a per-player dialog with that person's Party / Social switches.
//
// Adding a tool:
//   1. Put it in its own partial `Plugin.<Feature>.cs` with an `Init<Feature>()` that registers its window via
//      RegisterWindow(...) (and a `Dispose<Feature>()` if it owns patches/hotkeys beyond the window).
//   2. Call `Init<Feature>()` from the constructor (and `Dispose<Feature>()` from Dispose).
//   3. Add ONE button for it to BuildHubRoot.
public sealed partial class Plugin : IStellarPlugin
{
    public string Name => "MultiTool";

    private readonly IPluginServices _services;
    private readonly IConfigSection  _cfg;
    private bool _cfgLoadDirty;   // an Init* staged a config migration; saved once at the end of the constructor
    private readonly List<IWindowControl> _windows   = new();
    private readonly List<IDisposable>    _launchers = new();

    public Plugin(IPluginServices services)
    {
        _services = services;
        _cfg = _services.Config.GetSection("settings");

        // Sub-menus are registered as hidden windows with NO launcher tile of their own —
        // they are opened from the single hub window below.
        InitBandMute();       // Band Mute: silence specific other players' band instruments (Plugin.BandMute.cs)
        InitInviteAccept();   // Auto-Accept Invites: party + interaction invite auto-accept (Plugin.InviteAccept.cs)
        InitProfileCardAdd(); // "Auto-Accept" on the native profile card → per-player dialog — needs the lists (Plugin.ProfileCardAdd.cs)

        // Load-time migrations above only STAGE (Set) and raise _cfgLoadDirty; persist them with ONE save here. Each
        // Init saving on its own meant back-to-back writes, and the framework's single-thread writer has no retry on
        // IOException — the second could hit a transient lock on the just-written file and be dropped.
        if (_cfgLoadDirty) { _cfg.Save(); _cfgLoadDirty = false; }

        // The one and only main-menu tile — opens the hub, which links out to the sub-menus.
        var hub = RegisterWindow("multitool.hub", "MultiTool", 0, BuildHubRoot());
        _launchers.Add(_services.Launcher.Register(new LauncherEntry(
            Title:   "MultiTool",
            IconPng: LoadIconPng(),
            IconKey: null,
            OnOpen:  () => hub.SetVisible(true))
        { Group = LauncherGroup.Plugin,
          // Gate the MultiTool launcher tile to the in-world phase.
          ShouldShow = () => _services.ClientState.Phase == GamePhase.World }));

        _services.Log.Info("[MultiTool] constructed");
    }

    // Registers a hidden, draggable menu window (no launcher tile). Windows are staggered so they don't stack.
    // DefaultRect is a fixed literal (calibrated to the 2560×1440 reference canvas), NOT derived from ScreenWidth:
    // "reset all HUD" restores it from a path where ScreenWidth reads 0 → a ScreenWidth-relative x lands off-screen
    // (WindowBuilder-Patterns.md §"DefaultRect must be a CONSTANT").
    // `width` defaults to 440 (every existing menu); a wider window is shifted left so it stays on the 2560 canvas
    // (x = min(2100, 2540 − width) — still a constant per window, never ScreenWidth-derived). Non-resizable windows take
    // their width from DefaultRect at build time; a saved layout only restores the POSITION (WindowRenderer.SetRect
    // writes sizeDelta only when Resizable), so a changed default width applies even with an old saved layout.
    private IWindowControl RegisterWindow(string id, string title, int index, HudElement root, float width = 440f)
    {
        IWindowControl w = null!;
        w = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          id,
                Title:       title,
                DefaultRect: new WindowRect(MathF.Min(2100f, 2540f - width), 20f + index * 40f, width, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = true, StartVisible = false,
              // Dev/utility tool: show at title/menus, but hide during loading screens.
              ShouldRender = () => (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            Root: root,
            OnClose: () => w!.SetVisible(false)));
        _windows.Add(w);
        return w;
    }

    // The hub window: a single main-menu tile opens this, and its buttons open the sub-menus.
    private HudElement BuildHubRoot() => new ColumnElement(
    [
        new TextElement(() => "MultiTool", Emphasis: true),
        new ButtonElement(Label: () => "Mute Instruments",
            OnClick: () => _bandMuteWindow.SetVisible(true), Width: 320f),
        new ButtonElement(Label: () => "Auto-Accept Invites",
            OnClick: () => _inviteWindow.SetVisible(true), Width: 320f),
    ], Gap: 8f);

    private static byte[]? LoadIconPng()
    {
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream("Stellar.MultiTool.multitool-icon.png");
            if (s == null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    public void Dispose()
    {
        DisposeBandMute();
        DisposeProfileCardAdd();
        DisposeInviteCardDialog();
        DisposeInviteClearConfirm();
        DisposeInviteAccept();
        foreach (var l in _launchers) l.Dispose();
        foreach (var w in _windows) w.Remove();
    }
}
