using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// Shared look for the hub and the tool windows (approved mockups _scratch\prototypes\multitool-hub.html option C +
// multitool-tools.html): a ToolHeader (40px icon + bold title over the tool's live status line), dark rounded
// SectionPanels with a small uppercase caption, status chips, and the embedded-PNG cache.
//
// Toolkit limits behind some choices (WindowBuilder-Patterns.md):
//   • Plain TextElement ignores FontSize on Menu windows (fixed 14px) — only the styled path (Bold/Emphasis) honours it,
//     so the small caption is Bold+FontSize while hints/status lines stay at the fixed plain size.
//   • Row/Column Gap 0 means the DEFAULT gap (8/12), never zero — every container here passes an explicit Gap.
//   • Bold can't be toggled live → an on/off chip is two complete pills swapped by a ConditionalElement, each in a
//     fixed-width cell so the swap never moves its neighbours.
public sealed partial class Plugin
{
    // ── palette (mockup values) ───────────────────────────────────────────────────────────────────────────────────
    private static readonly ColorRgba PanelFill    = Rgb(0x261d2b);
    private static readonly ColorRgba TileFill     = Rgb(0x3d3045);   // hub cards + roster rows (raised tile)
    private static readonly ColorRgba CaptionColor = Rgb(0x9b8aa5);   // captions, hints, chevron, "· N players"
    private static readonly ColorRgba StatusOff    = Rgb(0x8a7a92);
    private static readonly ColorRgba StatusOn     = Rgb(0x5fd38d);
    private static readonly ColorRgba ChipParty    = Rgb(0x66d9ff);
    private static readonly ColorRgba ChipSocial   = Rgb(0x8ce68c);
    private static readonly ColorRgba ChipJoin     = Rgb(0xffcc73);
    private static readonly ColorRgba Transparent  = new(0f, 0f, 0f, 0f);

    private const float ToolIconSize = 40f;
    // One width for every chip, wide enough for the widest BOLD label ("Social") + padding, so the on (bold) and off
    // (regular) variants are the same size and the three chips line up. Estimated, not measured — check in-game.
    private const float ChipWidth = 64f;

    private static ColorRgba Rgb(uint rgb, float a = 1f) =>
        new(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f, a);

    // ── embedded PNGs ─────────────────────────────────────────────────────────────────────────────────────────────
    // Read once per resource and kept: ImageElement Funcs are polled, and the launcher/hub/tool windows share icons.
    // A missing resource caches null (the element then just draws nothing) rather than re-reading every poll.
    private static readonly Dictionary<string, byte[]?> _pngCache = new();

    private static byte[]? CachedPng(string file)
    {
        if (_pngCache.TryGetValue(file, out var png)) return png;
        png = LoadEmbeddedPng("Stellar.MultiTool." + file);   // Plugin.InviteAddDialog.cs
        _pngCache[file] = png;
        return png;
    }

    private static byte[]? BandMuteIconPng()   => CachedPng("icon-bandmute.png");
    private static byte[]? AutoAcceptIconPng() => CachedPng("icon-autoaccept.png");
    private static byte[]? BannerPng()         => CachedPng("banner.png");

    // ── building blocks ───────────────────────────────────────────────────────────────────────────────────────────
    // A fixed-size square icon. ImageElement only sets a PREFERRED size, and Cell/Selectable force-expand a child's
    // width (the icon-squish gotcha): the fixed-width Cell pins the column, and the inner Row keeps the image at its
    // intrinsic 40x40 instead of stretching it.
    private static HudElement IconBox(Func<byte[]?> png, float size) => new CellElement(
        new RowElement(new HudElement[] { new ImageElement(png, (int)size, (int)size) }, Justify: RowJustify.Center),
        Width: size);

    // Tool title block: icon + bold title over the tool's live status line. The hub card and the tool window both
    // build their status from the SAME builder method, so the two can't drift.
    private static HudElement ToolHeader(Func<byte[]?> icon, string title, HudElement status) =>
        ToolHeader(icon, () => title, status);

    // Live-title variant (the per-player dialog's name, which upgrades from the uid once it resolves).
    private static HudElement ToolHeader(Func<byte[]?> icon, Func<string> title, HudElement status) => new RowElement(
        new HudElement[]
        {
            IconBox(icon, ToolIconSize),
            new ColumnElement(new HudElement[]
            {
                new TextElement(title, Emphasis: true, NoWrap: true),
                status,
            }, Gap: 2f),
        }, Gap: 10f);

    // A muted subtitle line for a ToolHeader (dialogs whose header has no live status).
    private static HudElement MutedLine(Func<string> text) => new TextElement(text, Color: () => CaptionColor);

    // ── pill buttons ──────────────────────────────────────────────────────────────────────────────────────────────
    // Every labelled button is ONE build: a fill-tinted Bubble around a SelectableElement (the hub-card pattern; the
    // selectable's hover wash sits on the fill). MenuButtonStyle only has Outline / Filled / Glass with no custom fill,
    // so a red or grey button can't be a ButtonElement — and mixing the two builds gave different heights in one row
    // (ButtonElement 23 px vs this 30 px, in-game). ONE fixed height for every pill: a PillStrutHeight-tall spacer on
    // each side of the label + the selectable's 4 + 4 vertical padding (ChatTools' proven PillBubble pattern), with the
    // label at bold 13 so its own line height stays under the strut and can't grow the pill.
    //
    // Width: a pill directly in a Column (or stretched by a weighted Cell) FILLS it, label centred. `width` > 0 fixes the
    // label box (centred, so the pill's natural width = width + 2×PillPadX + the selectable's 2×6) — used for a pill
    // sitting in a Row ("Look up") and for equal pairs: weighted cells only split equally when both children prefer the
    // same width (WindowBuilder-Patterns.md "Weight is NOT an equal split"). Bubble caps at its parent width (MaxWidth 0),
    // but every pill here sits in a Column / stretched Cell / content Row, never a natural Cell sized from the bubble, so a
    // live label change (Band Mute's "Unmute all" ↔ "Clear allow-list") can't hit the shrink-only ratchet.
    private const float PillStrutHeight = 20f;   // + the Selectable's 4 + 4 = a 28 px pill
    private const float PillPadX        = 6f;    // strut width; + the Selectable's own 6 = 12 each side of a label
    private const float PillPairWidth   = 96f;   // label box for the two-in-a-row pairs (both halves prefer the same)

    private static readonly ColorRgba PrimaryFill = Rgb(0xc8508f);         // the accent ("Filled" button) fill
    private static readonly ColorRgba NeutralFill = Rgb(0x4a3b52);         // grey; lighter than tiles (#3d3045) + panels
    private static readonly ColorRgba DangerFill  = Rgb(0xff4d4f, 0.85f);
    private static readonly ColorRgba PillWhite   = new(1f, 1f, 1f);
    private static readonly ColorRgba PillText    = Rgb(0xe8dcef);

    private static HudElement PillButton(Func<string> label, ColorRgba fill, ColorRgba text, Action onClick,
        float width = 0f) => new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(PillPadX, PillStrutHeight),
                new TextElement(label, Color: () => text, Width: width, Align: TextAlign.Center, NoWrap: true,
                    FontSize: 13) { Bold = true },
                new SpacerElement(PillPadX, PillStrutHeight),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick))
        {
            FillColor = fill, PaddingX = 0f, PaddingY = 0f,
        };

    private static HudElement PrimaryButton(Func<string> label, Action onClick, float width = 0f) =>
        PillButton(label, PrimaryFill, PillWhite, onClick, width);
    private static HudElement NeutralButton(Func<string> label, Action onClick, float width = 0f) =>
        PillButton(label, NeutralFill, PillText, onClick, width);
    private static HudElement DangerButton(Func<string> label, Action onClick, float width = 0f) =>
        PillButton(label, DangerFill, PillWhite, onClick, width);

    // Two pills side by side at equal widths filling the row (see the width note above).
    private static HudElement PillPair(HudElement left, HudElement right) => new RowElement(new HudElement[]
    {
        new CellElement(left,  Weight: 1f),
        new CellElement(right, Weight: 1f),
    }, Gap: 8f);

    // A dark rounded panel: optional small UPPERCASE caption (live) + optional right-aligned muted hint (live), then
    // the children. The Bubble sits directly in the window Column, which force-expands its width → it FILLS the
    // window width instead of hugging (the hug only happens inside a Row), and its padded VLG stretches the inner
    // Column (lists, full-width buttons) to the panel's inner width.
    private static HudElement SectionPanel(Func<string>? caption, Func<string>? hint, params HudElement[] children)
    {
        var items = new List<HudElement>(children.Length + 1);
        if (caption != null || hint != null)
            items.Add(new RowElement(new HudElement[]
            {
                new TextElement(() => caption?.Invoke().ToUpperInvariant() ?? "",
                    Color: () => CaptionColor, NoWrap: true, FontSize: 12) { Bold = true },
                new SpacerElement(0f),   // flexible: pushes the hint to the right edge
                new TextElement(() => hint?.Invoke() ?? "", Color: () => CaptionColor, NoWrap: true),
            }, Gap: 6f));
        items.AddRange(children);
        return new BubbleElement(new ColumnElement(items, Gap: 6f))
        {
            FillColor = PanelFill, CornerRadius = 6f, PaddingX = 10f, PaddingY = 8f,
        };
    }

    // An on/off status chip. On = bold coloured text on a ~22% pill of that colour; off = grey text on a faint grey
    // pill. Two complete pills (bold can't be toggled live) swapped by a Conditional wrapping the WHOLE pill — never a
    // Conditional inside a bubble's measured child (the bubble re-hug gotcha) — each in the same fixed-width cell.
    private static HudElement StatusChip(string label, ColorRgba color, Func<bool> on) => new ConditionalElement(on,
        ChipPill(new TextElement(() => label, Color: () => color, Align: TextAlign.Center, NoWrap: true) { Bold = true },
                 new ColorRgba(color.R, color.G, color.B, 0.22f)),
        ChipPill(new TextElement(() => label, Color: () => StatusOff, Align: TextAlign.Center, NoWrap: true),
                 new ColorRgba(CaptionColor.R, CaptionColor.G, CaptionColor.B, 0.15f)));

    // The fixed-width cell caps the bubble at ChipWidth (MaxWidth 0 = parent width) and stretches it to exactly that,
    // so both variants are identical in size; the centred text sits in the middle of the pill.
    private static HudElement ChipPill(TextElement text, ColorRgba fill) => new CellElement(
        new BubbleElement(text) { FillColor = fill, PaddingX = 4f, PaddingY = 1f },
        Width: ChipWidth);
}
