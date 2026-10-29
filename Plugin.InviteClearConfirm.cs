using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// "Clear all" confirmation for the auto-accept Players roster. The main window's "Clear all" button no longer deletes
// directly — it opens this small hidden window (no launcher tile), centred OVER the main Auto-Accept window (not the
// screen), and only its "Clear all" button runs ClearAllInviteLists (Plugin.InviteRoster.cs).
//
// Z-order: opened via WindowCenterer.OpenCentred, which always fronts. A fresh ZSeq alone is not enough: the main
// window may have been fronted (from the hub), and a once-fronted window permanently outranks never-fronted ones
// (ZFront sorts before ZSeq and survives hide). Pressing "Clear all" again
// while it is already open fronts + re-centres it.
//
// Lifetime: it belongs to the main window — a watcher tick (subscribed only while the confirm is open) hides it the
// moment the main window closes, so a stale "Remove all N players?" can't outlive the list it refers to.
public sealed partial class Plugin
{
    private IWindowControl _invClearWindow = null!;
    private WindowCenterer _invClearCenter = null!;   // bounded re-centre on the main window's centre (WindowCenterer.cs)
    private bool _invClearWatchSubscribed;
    private bool _invClearSeenShown;   // the confirm has mounted since this open (see OnClearWatchTick)
    private const float InviteClearWidth = 360f;   // small dialog: the question panel + one row of two equal buttons

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
        _invClearCenter.OpenCentred();   // show + front-if-already-open + centre
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

    // Layout per the approved mockup (multitool-dialogs.html): a panel with the question + consequence, then ONE row of
    // equal halves — red "Clear all" and grey "Cancel" (same pill build + PillPairWidth, Plugin.Style.cs PillPair).
    private HudElement BuildInviteClearRoot() => new ColumnElement(new HudElement[]
    {
        SectionPanel(null, null,
            // Live N: re-read each frame, so it matches the roster even if it changed while the confirm was open.
            new TextElement(() => $"Remove all {_invRosterView.Count} players?", Emphasis: true),
            MutedLine(() => "Their Party and Social settings are deleted too. This can't be undone.")),
        PillPair(
            DangerButton(() => "Clear all", ConfirmClearAllInviteLists, PillPairWidth),
            NeutralButton(() => "Cancel", CloseInviteClearConfirm, PillPairWidth)),
    }, Gap: 8f);
}
