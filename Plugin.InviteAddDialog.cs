using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// "Add player…" dialog for the single Players roster (Plugin.InviteRoster.cs). Picking a player lists them at once —
// EnsureInviteListed, roster-only (both switches off) if new, same as the profile card's "Auto-Accept" button — then
// opens the per-player dialog (OpenInviteCardDialog, which centres it) to tick Party / Social. They stay listed until
// removed from the main window. This window stays open behind.
//
// Two ways to add:
//   (a) Here: type a uid → "Look up" (resolves + displays the player's name) → "Choose". The look-up gate ensures a
//       typed id is a real, resolved id before it can enter the rule set (a resolved-but-not-found id is still allowed
//       — valid id, just no social record).
//   (b) From the game's own name card: its "Auto-Accept" button (Plugin.ProfileCardAdd.cs). This dialog only shows an
//       instruction block for it — the old AOI-scan "Nearby players" picker was dropped in favour of the card button.
public sealed partial class Plugin
{
    private IWindowControl _invAddWindow = null!;
    private string _invAddDraft    = "";  // uid being typed
    private string _invAddLookedUp = "";  // the uid the user last pressed "Look up" for (gates Choose)

    // Registered as a hidden window with no launcher tile (same shape as the other sub-menus), opened from the Players
    // "Add player…" button. No separate Dispose: RegisterWindow adds it to _windows (Dispose removes it) and the
    // dialog holds no subscriptions.
    private void InitInviteAddDialog()
    {
        _invAddWindow = RegisterWindow("multitool.inviteadd", "MultiTool: Add Player", 3, BuildInviteAddRoot());
        // Centring rule: opens centred on the window it was opened FROM — the main Auto-Accept window.
        _invAddCenter = new WindowCenterer(_services.Framework, _invAddWindow, InviteMainWindowCentre);
    }

    private WindowCenterer _invAddCenter = null!;   // bounded re-centre on the main window (WindowCenterer.cs)

    private void DisposeInviteAddDialog() => _invAddCenter?.Stop();

    // No BringToFront: the dialog and the invite window share WindowCategory.Tools, and opening from hidden remounts
    // on top within the category (no cross-category conflict).
    private void OpenInviteAddDialog()
    {
        _invAddDraft    = "";
        _invAddLookedUp = "";
        _invAddWindow.SetVisible(true);
        _invAddCenter.Begin();
    }

    private void CloseInviteAdd() => _invAddWindow.SetVisible(false);

    // "Look up": validate the draft is a numeric id, mark it looked-up, and request both its name (for the list row)
    // and its full profile (name + level + avatar, for the card) — both eager RPCs when in-world.
    private void InviteAddLookup()
    {
        var s = (_invAddDraft ?? "").Trim();
        if (!AllDigits(s)) return;
        _invAddLookedUp = s;
        _nameResolver.Request(s);
        _profile.Request(s);
    }

    // "Choose": require only that the user pressed Look up for the CURRENT draft. The name resolving is NOT a gate — a
    // flaky/absent lookup that never returns a name must not trap the user; the dialog title resolves its name later.
    // The draft is kept (not cleared) so the lookup card stays put while the user ticks switches in the per-player dialog.
    private void InviteAddCommit()
    {
        var s = (_invAddDraft ?? "").Trim();
        if (!AllDigits(s)) return;
        if (s != _invAddLookedUp) return;         // must press Look up first
        EnsureInviteListed(s);                    // listed now (roster-only if new); the switches there pick the rules
        OpenInviteCardDialog(s, WindowCenterer.CentreOf(_invAddWindow));   // centred on THIS window (centring rule)
    }

    // True once the user has pressed Look up for a valid uid — switches the pre-lookup hint for the result card.
    private bool LookupActive() => AllDigits((_invAddLookedUp ?? "").Trim());

    // The pre-lookup hint (shown while nothing has been looked up): tracks the DRAFT's state.
    private string InviteAddLookupHint()
    {
        var s = (_invAddDraft ?? "").Trim();
        if (s.Length == 0) return "Enter a character uid, then press Look up.";
        if (!AllDigits(s)) return "A uid must be digits only.";
        return "Press Look up to fetch the name.";
    }

    // The result card: avatar + name + "Lv.N" (mirrors the game's player-info popup), on a raised tile (#3d3045) inside
    // the "By UID" panel. Reads the LOOKED-UP uid so the card keeps showing the fetched player even if the draft is
    // edited afterwards.
    private HudElement BuildLookupCard()
    {
        Func<ColorRgba?> muted = () => (ColorRgba?)_services.Theme.Colors.TextMuted;
        return new BubbleElement(new RowElement(new HudElement[]
        {
            // While still looking up → null texture → an invisible 56×56 box that holds the layout (don't flash a default
            // face before the result is known). Once resolved: the custom photo if the player set one, else the shared
            // default face (covers both "no custom photo" and terminal not-found). GameTextureElement re-pulls its texture
            // every frame, so the avatar appears the moment Poll decodes the async PNG (ImageElement only decoded once at
            // build time → never showed it).
            new GameTextureElement(
                () => {
                    var p = _profile.TryGet((_invAddLookedUp ?? "").Trim());
                    if (p?.AvatarTex != null) return p.AvatarTex;              // custom photo
                    if (p != null && p.Resolved) return _profile.DefaultAvatarTex;  // resolved, no custom photo OR not-found → default face
                    return null;                                               // still looking up → blank box
                },
                56, 56) { CornerRadius = 10 },   // no Fill needed; default aspect-fit is fine for square avatars
            new ColumnElement(new HudElement[]
            {
                new TextElement(CardTitle, Emphasis: true),
                new TextElement(CardSubtitle, Color: muted),
            }, Gap: 4f),
        }, Gap: 10f)) { FillColor = TileFill, PaddingX = 8f, PaddingY = 8f };
    }

    // Big line: the player name (or a looking-up / no-name placeholder). Seeds the name resolver opportunistically so
    // the per-player dialog + list row show the name instantly (cheap + idempotent — Seed never overwrites a cached entry).
    private string CardTitle()
    {
        var s = (_invAddLookedUp ?? "").Trim();
        var p = _profile.TryGet(s);
        if (p == null) return $"Looking up {s}…";
        if (p.Resolved && !string.IsNullOrEmpty(p.Name)) { _nameResolver.Seed(s, p.Name); return p.Name; }
        // Resolved with no name = genuine not-found OR a terminal timeout (retry cap hit) — a dead lookup either way.
        // Say so instead of leaving the card on "Looking up…" forever; Choose is gated only on Look-up-pressed, still works.
        return "No player details found";
    }

    // Sub line: "Lv.N · UID …", plus a "(no custom photo)" note once we know the player set no avatar.
    private string CardSubtitle()
    {
        var s = (_invAddLookedUp ?? "").Trim();
        var p = _profile.TryGet(s);
        if (p == null) return "You can choose it now or wait for details.";
        // Terminal not-found (no name): drop the "Lv.0" line — just the uid + the reassurance that Choose still works.
        if (string.IsNullOrEmpty(p.Name)) return $"UID {s} — you can still choose it.";
        var sub = $"Lv.{p.Level}   ·   UID {s}";
        if (p.Resolved && string.IsNullOrEmpty(p.Url)) sub += "   (no custom photo)";
        return sub;
    }

    // How-to picture for the card route: a crop of the game's name-card button row with Auto-Accept highlighted.
    // ImageElement takes the PNG bytes directly (framework decodes once at build) — no Texture2D lifetime to own,
    // fine for a static image. Shown at its native 300×90 canvas units, wrapped in a centred RowElement because the
    // column/cell layout force-expands a direct child's WIDTH (would stretch it; WindowBuilder-Patterns.md "squished").
    // A missing/unreadable resource → a never-shown ConditionalElement (inactive → skipped by the layout, no stray
    // column gap; a 0-width SpacerElement would be "flexible"), so the dialog just omits the picture.
    private static HudElement BuildHowToImage()
    {
        var png = LoadEmbeddedPng("Stellar.MultiTool.howto-autoaccept.png");
        if (png == null) return new ConditionalElement(() => false, new TextElement(() => ""));
        return new RowElement(new HudElement[] { new ImageElement(Png: () => png, Width: 300, Height: 90) },
                              Justify: RowJustify.Center);
    }

    private static byte[]? LoadEmbeddedPng(string name)
    {
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream(name);
            if (s == null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    // ── window ────────────────────────────────────────────────────────────────────────────────────────────────────
    // Layout per the approved mockup (multitool-dialogs.html): header, "By UID" panel, "From the name card" panel, Close.
    private HudElement BuildInviteAddRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader(AutoAcceptIconPng, "Add Player",
            MutedLine(() => "Adds them to the list; then pick what to auto-accept.")),

        SectionPanel(() => "By UID", null,
            // Enter in the box triggers Look up (not Choose) — matches "look up first". InviteAddLookup reads the live
            // draft, not the Submit arg, so a stale/empty Submit arg on Enter can't break it.
            new RowElement(new HudElement[]
            {
                new InputElement(Get: () => _invAddDraft, Submit: _ => InviteAddLookup(),
                                 OnChange: s => _invAddDraft = s, Width: 180f),
                new ButtonElement(Label: () => "Look up", OnClick: InviteAddLookup, Width: 120f),
            }, Gap: 6f),

            // Before a look-up: the hint. After: the avatar+name+level card. Keyed on LookupActive() so the switch
            // happens the moment Look up is pressed (both subtrees are built once; the renderer SetActive-toggles them).
            new ConditionalElement(() => !LookupActive(),
                new TextElement(InviteAddLookupHint, Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted)),
            new ConditionalElement(() => LookupActive(), BuildLookupCard()),
            new ButtonElement(Label: () => "Choose this player…", OnClick: InviteAddCommit,
                Style: MenuButtonStyle.Filled)),

        // Card route: instructions only — the card's own button does the adding (Plugin.ProfileCardAdd.cs). TextElement
        // wraps by default, so the long line folds to the panel width.
        SectionPanel(() => "From the name card", null,
            new TextElement(() => "Open any player's name card (click their name or avatar), then press Auto-Accept in the card's button row."),
            BuildHowToImage()),

        new ButtonElement(Label: () => "Close", OnClick: CloseInviteAdd),
    }, Gap: 8f);
}
