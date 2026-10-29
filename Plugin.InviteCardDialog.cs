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
// Every open re-centres it (to draw the eye), including a retarget of an already-open dialog; the user may drag it
// freely in between. Centring rule: a child window opens centred on the window it was opened FROM — so the target is
// chosen per open: the Add Player window's centre from "Choose this player…", the SCREEN centre from the name-card
// button (no MultiTool parent there).
public sealed partial class Plugin
{
    private IWindowControl _invCardWindow = null!;
    private string _invCardUid = "";   // the carded player's char uid (decimal), "" until first opened
    private WindowCenterer _invCardCenter = null!;   // bounded re-centre on _invCardTarget (WindowCenterer.cs)
    // The current open's centring target; set by OpenInviteCardDialog before Begin(). The centerer reads it through a
    // lambda, so retargeting needs no new centerer.
    private Func<float, float, (float X, float Y)?> _invCardTarget = WindowCenterer.CanvasCentre;

    // Hidden window, no launcher tile (same shape as the other sub-menus). RegisterWindow adds it to _windows (Dispose
    // removes it); DisposeInviteCardDialog only drops a still-pending re-centre tick.
    private void InitInviteCardDialog()
    {
        _invCardWindow = RegisterWindow("multitool.invitecard", "MultiTool: Auto-Accept Player", 4,
            BuildInviteCardRoot());
        _invCardCenter = new WindowCenterer(_services.Framework, _invCardWindow, (cw, ch) => _invCardTarget(cw, ch));
    }

    private void DisposeInviteCardDialog() => _invCardCenter?.Stop();

    // Z-order via WindowCenterer.OpenCentred, which always fronts (a once-fronted window permanently outranks never-
    // fronted ones; user-approved Rule 5 exception), so a retarget never stays under its opener. Our windows render above the
    // native card's UI canvas (EntityInspector precedent), so no extra z work for the card either.
    // `centre` null = screen centre (the name-card route); otherwise the opener's window centre.
    private void OpenInviteCardDialog(string uid, Func<float, float, (float X, float Y)?>? centre = null)
    {
        _invCardUid = uid;
        _invCardTarget = centre ?? WindowCenterer.CanvasCentre;
        // Bounded re-centre until laid out (the size can still move a tick later, e.g. the live title widening once
        // the person's name resolves) — see WindowCenterer.cs for why it isn't one SetRect.
        _invCardCenter.OpenCentred();   // show + front-if-already-open + centre
    }

    private void CloseInviteCardDialog() => _invCardWindow.SetVisible(false);

    // Header title = the name only (same resolver InviteRowText reads, minus the "(uid)"); the bare uid until it
    // resolves — re-read each frame, so it upgrades the moment the name lands.
    private string InviteCardName()
    {
        var n = _nameResolver?.TryGet(_invCardUid);
        return string.IsNullOrEmpty(n) ? _invCardUid : n!;
    }

    // Layout per the approved mockup (multitool-dialogs.html): header, one panel with the two switches, Close.
    private HudElement BuildInviteCardRoot()
    {
        return new ColumnElement(new HudElement[]
        {
            ToolHeader(AutoAcceptIconPng, InviteCardName, MutedLine(() => $"UID {_invCardUid} · on your list")),

            SectionPanel(() => "Auto-accept from this player", null,
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
                }, Gap: 6f)),

            new ButtonElement(Label: () => "Close", OnClick: CloseInviteCardDialog),
        }, Gap: 8f);
    }
}
