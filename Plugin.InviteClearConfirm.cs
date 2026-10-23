using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// "Clear all" confirmation for the auto-accept Players roster. The main window's "Clear all" button no longer deletes
// directly — it opens this small hidden window (no launcher tile), centred OVER the main Auto-Accept window (not the
// screen), and only its "Clear all" button runs ClearAllInviteLists (Plugin.InviteRoster.cs).
//
// Z-order: no BringToFront (CLAUDE.md Rule 5). It opens from hidden → remounts with a fresh top ZSeq, and it shares
// WindowCategory.Tools with the main window, so it sorts above it. Nothing in this plugin fronts the main window
// (ZFront stays 0 — and the framework never fronts on click), so the fresh ZSeq always wins. Pressing "Clear all" again
// while it's already open just re-centres it; it's still above for the same reason.
//
// Lifetime: it belongs to the main window — a watcher tick (subscribed only while the confirm is open) hides it the
// moment the main window closes, so a stale "Remove all N players?" can't outlive the list it refers to.
public sealed partial class Plugin
{
    private IWindowControl _invClearWindow = null!;
    private WindowCenterer _invClearCenter = null!;   // bounded re-centre on the main window's centre (WindowCenterer.cs)
    private bool _invClearWatchSubscribed;
    private bool _invClearSeenShown;   // the confirm has mounted since this open (see OnClearWatchTick)
    private const float InviteClearWidth = 360f;   // small dialog: fits the two 150px buttons + gap + panel padding

    private void InitInviteClearConfirm()
    {
        _invClearWindow = RegisterWindow("multitool.inviteclearconfirm", "MultiTool: Clear list?", 5,
            BuildInviteClearRoot(), InviteClearWidth);
        _invClearCenter = new WindowCenterer(_services.Framework, _invClearWindow, InviteMainWindowCentre);
    }

    private void DisposeInviteClearConfirm()
    {
        _invClearCenter?.Stop();
        StopClearWatch();
    }

    // Centre of the main Auto-Accept window's CURRENT rect (canvas units — the same space as SetRect), or null while it
    // isn't laid out yet so the centerer keeps trying. Clamping to the canvas is done by WindowCenterer.
    private (float X, float Y)? InviteMainWindowCentre(float cw, float ch)
    {
        if (!_inviteWindow.IsShown) return null;
        var m = _inviteWindow.Rect;
        if (m.Width <= 1f || m.Height <= 1f) return null;
        return (m.X + m.Width / 2f, m.Y + m.Height / 2f);
    }

    private void OpenInviteClearConfirm()
    {
        _invClearWindow.SetVisible(true);
        _invClearCenter.Begin();
        _invClearSeenShown = false;
        if (!_invClearWatchSubscribed) { _services.Framework.Update += OnClearWatchTick; _invClearWatchSubscribed = true; }
    }

    private void CloseInviteClearConfirm() => _invClearWindow.SetVisible(false);

    private void StopClearWatch()
    {
        if (_invClearWatchSubscribed) { try { _services.Framework.Update -= OnClearWatchTick; } catch { } }
        _invClearWatchSubscribed = false;
    }

    // Two cheap IsShown reads per frame, only while the confirm is open. Unsubscribes itself once the confirm is
    // closed (by either button or its own ✕).
    // ⚠️ Same first-open trap as WindowCenterer: IsShown is false until WindowService MOUNTS the window (its Band 3
    // tick runs after our Band 2 Update), so "not shown" only means "closed" once it has been seen shown. Before
    // that, keep waiting — the first-open watcher otherwise unsubscribed itself on tick 1.
    private void OnClearWatchTick(float _)
    {
        if (!_invClearWindow.IsShown)
        {
            if (_invClearSeenShown) StopClearWatch();
            return;
        }
        _invClearSeenShown = true;
        if (!_inviteWindow.IsShown) { CloseInviteClearConfirm(); StopClearWatch(); }
    }

    private void ConfirmClearAllInviteLists()
    {
        ClearAllInviteLists();
        CloseInviteClearConfirm();
    }

    private HudElement BuildInviteClearRoot() => new ColumnElement(new HudElement[]
    {
        // Live N: re-read each frame, so it matches the roster even if it changed while the confirm was open.
        new TextElement(() => $"Remove all {_invRosterView.Count} players from the auto-accept list?"),
        new RowElement(new HudElement[]
        {
            new ButtonElement(Label: () => "Clear all", OnClick: ConfirmClearAllInviteLists, Width: 150f),
            new ButtonElement(Label: () => "Cancel",    OnClick: CloseInviteClearConfirm,    Width: 150f),
        }, Gap: 8f),
    }, Gap: 8f);
}
