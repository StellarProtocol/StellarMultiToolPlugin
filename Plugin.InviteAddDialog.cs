using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// "Add player…" dialog for the single Players roster (Plugin.InviteRoster.cs). Adding a player lists them at once —
// EnsureInviteListed, every switch off if new, same as the profile card's "Auto-Accept" button — then opens the
// per-player dialog (OpenInviteCardDialog, which centres it) to tick Party / Social / Leader. They stay listed until
// removed from the main window. This window stays open behind.
//
// Layout per the approved mockup multitool-addplayer-name.html:
//   (a) "Find player · name or UID": one input (Enter = Search) + a Primary "Search" pill, a fixed-height results area
//       (Plugin.InviteFindRows.cs) and a hint line.
//       • Digits only → the existing UID look-up (_nameResolver + _profile), unchanged: the avatar + name + level card
//         shows in the results area with an "Add" pill. Add = the old "Choose this player…" commit.
//       • Anything else → a NAME search through the game's friend search (Plugin.InviteFind.cs), one row per match.
//   (b) From the game's own name card: its "Auto-Accept" button (Plugin.ProfileCardAdd.cs). This dialog only shows an
//       instruction block for it.
public sealed partial class Plugin
{
    private enum InvFindMode { None, Uid, Name }

    private IWindowControl _invAddWindow = null!;
    private string _invAddDraft    = "";  // name or uid being typed
    private bool   _invAddDraftDigits;    // cached AllDigits(draft.Trim()) — read by the per-frame pill Funcs
    private string _invAddLookedUp = "";  // the uid last searched (the UID card shows this one)
    private InvFindMode _invFindMode;     // what the results area shows

    private const float InvAddInnerW   = 440f - 24f - 20f;   // 396: window − 2×12 body inset − 2×10 SectionPanel padding
    private const float InvSearchW     = 60f;                // label box that holds "Wait 10 s" → an 84 px pill
    private const float InvSearchCellW = InvSearchW + 24f;
    private const float InvInputW      = InvAddInnerW - InvSearchCellW - 6f;

    // Registered as a hidden window with no launcher tile (same shape as the other sub-menus), opened from the Players
    // "Add player…" button. RegisterWindow adds it to _windows (Dispose removes it); DisposeInviteAddDialog stops the
    // re-centre tick and a still-pending search poll.
    private void InitInviteAddDialog()
    {
        _invAddWindow = RegisterWindow("multitool.inviteadd", "MultiTool: Add Player", 3, BuildInviteAddRoot());
        // Centring rule: opens centred on the window it was opened FROM — the main Auto-Accept window.
        _invAddCenter = new WindowCenterer(_services.Framework, _invAddWindow, InviteMainWindowCentre);
    }

    private WindowCenterer _invAddCenter = null!;   // bounded re-centre on the main window (WindowCenterer.cs)

    private void DisposeInviteAddDialog()
    {
        _invAddCenter?.Stop();
        StopInvFindPoll();
    }

    // Z-order via WindowCenterer.OpenCentred, which always fronts: a once-fronted window permanently outranks
    // never-fronted ones, so every MultiTool open fronts. A search still out keeps running; its answer lands normally.
    private void OpenInviteAddDialog()
    {
        SetInviteAddDraft("");
        _invAddLookedUp = "";
        _invFindMode    = InvFindMode.None;
        _invMatches.Clear();
        _invFindScrollReset = true;
        _invAddCenter.OpenCentred();   // show + front-if-already-open + centre
    }

    private void CloseInviteAdd() => _invAddWindow.SetVisible(false);

    private void SetInviteAddDraft(string? s)
    {
        _invAddDraft = s ?? "";
        _invAddDraftDigits = AllDigits(_invAddDraft.Trim());
    }

    // "Search" (pill or Enter): digits only → the UID look-up; anything else → the name search. Reads the live draft,
    // not the Submit arg, so a stale/empty Submit arg on Enter can't break it.
    private void InviteAddSearch()
    {
        var s = (_invAddDraft ?? "").Trim();
        if (s.Length == 0 || !InvSearchEnabled()) return;
        if (AllDigits(s))
        {
            InviteAddLookup(s);
            _invFindMode = InvFindMode.Uid;
            return;
        }
        _invFindMode = InvFindMode.Name;
        InvFindStart(s);   // Plugin.InviteFind.cs
    }

    // UID look-up (unchanged): mark it looked-up and request both its name (for the list row) and its full profile
    // (name + level + avatar, for the card) — both eager RPCs when in-world.
    private void InviteAddLookup(string s)
    {
        _invAddLookedUp = s;
        _nameResolver.Request(s);
        _profile.Request(s);
    }

    // The card's "Add" = the old "Choose this player…" commit, for the uid the card SHOWS (the looked-up one, even if
    // the draft was edited since). The name resolving is NOT a gate — a flaky/absent lookup that never returns a name
    // must not trap the user; the dialog title resolves its name later.
    private void InviteAddCommit()
    {
        var s = (_invAddLookedUp ?? "").Trim();
        if (!AllDigits(s)) return;
        EnsureInviteListed(s);                    // listed now (all off if new); the switches there pick the rules
        OpenInviteCardDialog(s, WindowCenterer.CentreOf(_invAddWindow));   // centred on THIS window (centring rule)
    }

    // True once the user has searched a valid uid — the results area then shows the card.
    private bool LookupActive() => AllDigits((_invAddLookedUp ?? "").Trim());

    // The result card: avatar + name + "Lv.N" (mirrors the game's player-info popup) + an "Add" pill, on a raised tile
    // (TileColor, live from the theme) at the top of the results area. Reads the LOOKED-UP uid so the card keeps showing
    // the fetched player even if the draft is edited afterwards.
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
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(CardTitle, Emphasis: true),
                new TextElement(CardSubtitle, Color: muted),
            }, Gap: 4f), Weight: 1f),
            // Same Add pill + cell as a name-search row (Plugin.InviteFindRows.cs), so the two read as one control.
            new CellElement(new RowElement(new HudElement[] { PrimaryButton(() => "Add", InviteAddCommit, InvFindAddW) },
                Justify: RowJustify.Center), Width: InvFindAddCellW),
        }, Gap: 10f)) { FillColorFn = () => Fade(TileColor()), PaddingX = 8f, PaddingY = 8f };
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
        // Say so instead of leaving the card on "Looking up…" forever; Add is not gated on the name, still works.
        return "No player details found";
    }

    // Sub line: "Lv.N · UID …", plus a "(no custom photo)" note once we know the player set no avatar.
    private string CardSubtitle()
    {
        var s = (_invAddLookedUp ?? "").Trim();
        var p = _profile.TryGet(s);
        if (p == null) return "You can add them now or wait for details.";
        // Terminal not-found (no name): drop the "Lv.0" line — just the uid + the reassurance that Add still works.
        if (string.IsNullOrEmpty(p.Name)) return $"UID {s} — you can still add them.";
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
    // Layout per the approved mockup (multitool-addplayer-name.html): header, "Find player" panel, "From the name card"
    // panel, Close.
    private HudElement BuildInviteAddRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader(AutoAcceptIconPng, "Add Player",
            MutedLine(() => "Adds them to the list; then pick what to auto-accept.")),

        SectionPanel(null, null,
            // Caption with the sub-caption right beside it (the mockup), not SectionPanel's right-aligned hint.
            new RowElement(new HudElement[]
            {
                new TextElement(() => "FIND PLAYER", Color: () => MutedColor(), NoWrap: true, FontSize: 12) { Bold = true },
                new TextElement(() => "name or UID", Color: () => MutedColor(), NoWrap: true),
            }, Gap: 6f),
            new RowElement(new HudElement[]
            {
                new CellElement(new InputElement(Get: () => _invAddDraft, Submit: _ => InviteAddSearch(), Width: InvInputW,
                    OnChange: SetInviteAddDraft) { SingleLine = true, Placeholder = () => "Name or UID" }, Width: InvInputW),
                // Recessed "Wait N s" while the game's search cooldown blocks a name search (Plugin.InviteFind.cs).
                new CellElement(PrimaryButton(InvSearchLabel, InviteAddSearch, InvSearchW, InvSearchEnabled),
                    Width: InvSearchCellW),
            }, Gap: 6f),
            BuildInviteFindResults(),   // fixed height in every state (Plugin.InviteFindRows.cs)
            MutedLine(() => "Names can repeat, so check the UID. Add lists the player and opens their switches.")),

        // Card route: instructions only — the card's own button does the adding (Plugin.ProfileCardAdd.cs). TextElement
        // wraps by default, so the long line folds to the panel width.
        SectionPanel(() => "From the name card", null,
            new TextElement(() => "Open any player's name card (click their name or avatar), then press Auto-Accept in the card's button row."),
            BuildHowToImage()),

        NeutralButton(() => "Close", CloseInviteAdd),
    }, Gap: 8f);
}
