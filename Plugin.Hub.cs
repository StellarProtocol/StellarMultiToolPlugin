using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// The hub window (multitool.hub): the banner, then one card per tool (approved mockup multitool-hub.html option C).
// Each card = [40px icon] | [bold title over the tool's live status line] | › and opens that tool's window centred on
// the hub. Adding a tool = one entry in BuildHubTools().
//
// Z-order: opened via WindowCenterer.OpenCentred, which ALWAYS fronts — a once-fronted window permanently outranks
// never-fronted ones (ZFront sorts before ZSeq and survives hide), so every MultiTool open fronts.
public sealed partial class Plugin
{
    private IWindowControl _hubWindow = null!;

    private sealed class HubTool
    {
        public string               Title    = "";
        public Func<byte[]?>        Icon     = () => null;
        public Func<HudElement>     Status   = null!;   // the same builder the tool window's header uses
        public IWindowControl       Window   = null!;
        public WindowCenterer       Centerer = null!;   // re-centres Window on the hub's current centre (WindowCenterer.cs)
    }

    private readonly List<HubTool> _hubTools = new();

    // More cards than this → the card column scrolls at a fixed height that fits exactly this many cards.
    private const int   HubMaxVisibleCards = 5;
    // Card pitch, PINNED for every card: a HubCardStrut-tall spacer in the card row (rows align MiddleLeft, so the icon
    // and text stay vertically centred) + the SelectableElement's 4px vertical padding each side = 56. Pinned because the
    // cards differ naturally (in-game: Mute 48px vs Auto-Accept 56px — the chip row is taller than plain status text),
    // which cut the 5th card of the fixed-height scroll in half.
    private const float HubCardStrut  = 48f;
    private const float HubCardHeight = HubCardStrut + 8f;
    private const float HubCardGap    = 8f;

#if DEBUG
    // Debug builds repeat the card list so the >5-card scroll path can be seen with today's 2 tools (3 → 6 cards).
    // Release builds never duplicate.
    private const int HubDebugCardCopies = 3;
#else
    private const int HubDebugCardCopies = 1;
#endif

    private void BuildHubTools()
    {
        AddHubTool("Mute Instruments",    BandMuteIconPng,   BandMuteStatus,   _bandMuteWindow);
        AddHubTool("Auto-Accept Invites", AutoAcceptIconPng, InviteStatus,     _inviteWindow);
    }

    private void AddHubTool(string title, Func<byte[]?> icon, Func<HudElement> status, IWindowControl window) =>
        _hubTools.Add(new HubTool
        {
            Title = title, Icon = icon, Status = status, Window = window,
            Centerer = new WindowCenterer(_services.Framework, window, HubWindowCentre),
        });

    private void DisposeHub()
    {
        foreach (var t in _hubTools) t.Centerer.Stop();
    }

    // Centre of the hub's CURRENT rect (canvas units, same space as SetRect), or null while it isn't laid out so the
    // centerer keeps trying. Clamping to the canvas is WindowCenterer's job. Same shape as InviteMainWindowCentre.
    private (float X, float Y)? HubWindowCentre(float cw, float ch)
    {
        if (!_hubWindow.IsShown) return null;
        var h = _hubWindow.Rect;
        if (h.Width <= 1f || h.Height <= 1f) return null;
        return (h.X + h.Width / 2f, h.Y + h.Height / 2f);
    }

    // Every click re-centres, including when the tool window is already open (Begin restarts the bounded settle).
    private static void OpenHubTool(HubTool t)
    {
        t.Centerer.OpenCentred();   // fronts it only if already open (WindowCenterer.OpenCentred)
    }

    private HudElement BuildHubRoot()
    {
        BuildHubTools();

        var cards = new List<HudElement>();
        for (int copy = 0; copy < HubDebugCardCopies; copy++)
            foreach (var t in _hubTools) cards.Add(HubCard(t));

        HudElement cardColumn = new ColumnElement(cards, Gap: HubCardGap);
        if (cards.Count > HubMaxVisibleCards)
            cardColumn = new ScrollElement(cardColumn,
                Height: HubMaxVisibleCards * HubCardHeight + (HubMaxVisibleCards - 1) * HubCardGap)
            { FixedHeight = true };

        return new ColumnElement(new HudElement[]
        {
            // Banner art (832x128 drawn at 416x64; the "MultiTool / Small quality-of-life tools" text is baked in).
            new RowElement(new HudElement[] { new ImageElement(BannerPng, 416, 64) }, Justify: RowJustify.Center),
            cardColumn,
        }, Gap: 10f);
    }

    // A raised tile (Bubble fill, sitting directly in the Column → full width) around a SelectableElement row, so the
    // selectable's default hover wash covers the whole card. Bubble OUTSIDE the selectable on purpose: a bubble wrapped
    // IN a selectable gets a full-width chip painted behind it (WindowBuilder-Patterns.md §BubbleElement).
    private static HudElement HubCard(HubTool t) => new BubbleElement(new SelectableElement(
        new RowElement(new HudElement[]
        {
            IconBox(t.Icon, ToolIconSize),
            new ColumnElement(new HudElement[]
            {
                new TextElement(() => t.Title, Emphasis: true, NoWrap: true),
                t.Status(),
            }, Gap: 2f),
            new SpacerElement(0f),   // flexible: pushes the chevron to the right edge
            // Bold (styled/TMP path) so FontSize applies, and a glyph missing from the plain menu font can't fall back
            // to a taller OS font and grow the row.
            new TextElement(() => "›", Color: () => CaptionColor, NoWrap: true, FontSize: 18) { Bold = true },
            new SpacerElement(2f, HubCardStrut),   // right margin + the height strut that pins every card to HubCardHeight
        }, Gap: 10f),
        OnClick: () => OpenHubTool(t)))
    {
        FillColor = TileFill, PaddingX = 0f, PaddingY = 0f,
    };
}
