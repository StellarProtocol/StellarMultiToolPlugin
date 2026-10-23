using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// Per-player "Auto-Accept" dialog, opened from the profile card's single "Auto-Accept" button
// (Plugin.ProfileCardAdd.cs) and from the Add-player dialog (Plugin.InviteAddDialog.cs). Shows the player's Party /
// Social switches — the same per-person switches as the main window's Players rows, edited through the same
// SetInviteSwitch path (Plugin.InviteRoster.cs), so the two views can't disagree. Every flip saves immediately.
// Ticking a switch moves the person into that system list; unticking the last keeps them listed (roster-only).
// There is deliberately no remove here — deleting someone is only the main window's ✕ / "Clear all".
//
// Both callers list the person (EnsureInviteListed, roster-only if new) BEFORE opening, so the dialog always edits a
// listed person and never adds or removes anyone itself. Re-opening for another player just retargets _invCardUid —
// every element reads it live, so an already-open dialog switches person in place.
//
// Every card press re-centres it on screen (to draw the eye), including a retarget of an already-open dialog; the
// user may drag it freely in between.
public sealed partial class Plugin
{
    private IWindowControl _invCardWindow = null!;
    private string _invCardUid = "";   // the carded player's char uid (decimal), "" until first opened
    private WindowCenterer _invCardCenter = null!;   // bounded re-centre on screen centre (WindowCenterer.cs)

    // Hidden window, no launcher tile (same shape as the other sub-menus). RegisterWindow adds it to _windows (Dispose
    // removes it); DisposeInviteCardDialog only drops a still-pending re-centre tick.
    private void InitInviteCardDialog()
    {
        _invCardWindow = RegisterWindow("multitool.invitecard", "MultiTool: Auto-Accept Player", 4,
            BuildInviteCardRoot());
        _invCardCenter = new WindowCenterer(_services.Framework, _invCardWindow, WindowCenterer.CanvasCentre);
    }

    private void DisposeInviteCardDialog() => _invCardCenter?.Stop();

    // No BringToFront: opening from hidden remounts with a fresh top ZSeq; if it's already visible this is a retarget
    // within WindowCategory.Tools — no cross-category conflict (CLAUDE.md Rule 5). Our windows already render above the
    // native card's UI canvas (EntityInspector precedent), so no extra z work for the card either.
    private void OpenInviteCardDialog(string uid)
    {
        _invCardUid = uid;
        _invCardWindow.SetVisible(true);
        // Bounded re-centre until laid out (the size can still move a tick later on a retarget, e.g. the title
        // wrapping once the new person's name resolves) — see WindowCenterer.cs for why it isn't one SetRect.
        _invCardCenter.Begin();
    }

    private void CloseInviteCardDialog() => _invCardWindow.SetVisible(false);

    private HudElement BuildInviteCardRoot()
    {
        return new ColumnElement(new HudElement[]
        {
            // InviteRowText re-reads the resolver each frame, so the title upgrades from the bare uid to "Name (uid)"
            // the moment the name lands.
            new TextElement(() => $"Auto-Accept: {InviteRowText(_invCardUid)}", Emphasis: true),

            new RowElement(new HudElement[]
            {
                new ToggleElement(Label: () => "", Get: () => InParty(_invCardUid),
                    Set: on => SetInviteSwitch(_invCardUid, social: false, on)),
                new TextElement(() => "Party invites"),
            }, Gap: 6f),

            new RowElement(new HudElement[]
            {
                new ToggleElement(Label: () => "", Get: () => InSocial(_invCardUid),
                    Set: on => SetInviteSwitch(_invCardUid, social: true, on)),
                new TextElement(() => "Social invites (co-op emote + ride together)"),
            }, Gap: 6f),

            new ButtonElement(Label: () => "Close", OnClick: CloseInviteCardDialog, Width: 320f),
        }, Gap: 8f);
    }
}
