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
    // ── palette: LIVE from the current framework theme ────────────────────────────────────────────────────────────
    // Every surface/text colour here is DERIVED from _services.Theme.Colors on each call and handed to elements as a
    // Func (TextElement.Color, BubbleElement.FillColorFn), so a theme switch (Default slate-teal / Dark / Light / Crimson,
    // or a custom theme) repaints on the next refresh with no restart. Nothing theme-derived is cached at build time.
    // Same approach as StellarChatToolsPlugin (Plugin.Tabs.cs TabTrackColor + Mix/Over/EnsureContrast).
    // Only SEMANTIC colours stay fixed: status green, the danger red and the Party/Social/Join chip colours (like
    // channel colours). Fills are composited opaque against the menu background (Over), as ChatTools does.
    private static readonly ColorRgba PanelShade  = new(0f, 0f, 0f, 0.20f);   // panel = background darkened
    private const float TileTextMix   = 0.10f;   // raised tile = panel + 10% MenuText (lighter on dark, darker on light)
    private const float NeutralMix    = 0.22f;   // neutral pill = panel + 22% MenuMuted
    private const float PillMinContrast = 4.5f;  // WCAG AA for the pill labels

    private static readonly ColorRgba StatusOn    = Rgb(0x5fd38d);
    private static readonly ColorRgba ChipParty   = Rgb(0x66d9ff);
    private static readonly ColorRgba ChipSocial  = Rgb(0x8ce68c);
    private static readonly ColorRgba ChipJoin    = Rgb(0xffcc73);
    private static readonly ColorRgba DangerFill  = Rgb(0xff4d4f, 0.85f);
    private static readonly ColorRgba White       = new(1f, 1f, 1f);
    private static readonly ColorRgba Transparent = new(0f, 0f, 0f, 0f);

    private IThemeMenuColors Menu => _services.Theme.Colors;

    private ColorRgba TextColor()    => Menu.MenuText;    // titles, body, neutral pill labels
    private ColorRgba MutedColor()   => Menu.MenuMuted;   // captions, hints, subtitles, chevron, "off" status
    private ColorRgba PanelColor()   => Over(PanelShade, Menu.MenuBackground);
    private ColorRgba TileColor()    => Over(Menu.MenuText with { A = TileTextMix }, PanelColor());
    private ColorRgba NeutralFill()  => Over(Menu.MenuMuted with { A = NeutralMix }, PanelColor());
    private ColorRgba PrimaryFill()  => Menu.MenuAccent;
    private ColorRgba PrimaryText()  => EnsureContrast(White, PrimaryFill(), PillMinContrast);
    private ColorRgba NeutralText()  => EnsureContrast(TextColor(), NeutralFill(), PillMinContrast);
    private ColorRgba DangerText()   => EnsureContrast(White, Over(DangerFill, PanelColor()), PillMinContrast);

    // Nudges fg toward white (dark bg) or black (light bg) in 15% steps until it reaches minRatio against the opaque
    // bg (copied from ChatTools Plugin.Tabs.cs). Pure struct math — no allocation on the per-refresh colour funcs.
    private static ColorRgba EnsureContrast(ColorRgba fg, ColorRgba bg, float minRatio)
    {
        float lb = Luminance(bg);
        // 0.179 = the luminance where white and black give equal contrast; pick whichever end gains more.
        var target = lb < 0.179f ? new ColorRgba(1f, 1f, 1f) : new ColorRgba(0f, 0f, 0f);
        for (int i = 0; i < 12 && ContrastRatio(Luminance(fg), lb) < minRatio; i++) fg = Mix(fg, target, 0.15f);
        return fg with { A = 1f };
    }

    private static float ContrastRatio(float la, float lb)
        => (MathF.Max(la, lb) + 0.05f) / (MathF.Min(la, lb) + 0.05f);

    // WCAG relative luminance (sRGB → linear).
    private static float Luminance(ColorRgba c) => 0.2126f * Lin(c.R) + 0.7152f * Lin(c.G) + 0.0722f * Lin(c.B);
    private static float Lin(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    // a → b by t (0 = a, 1 = b), opaque.
    private static ColorRgba Mix(ColorRgba a, ColorRgba b, float t)
        => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1f);

    // Alpha-composite fg (its A = coverage) over an opaque bg.
    private static ColorRgba Over(ColorRgba fg, ColorRgba bg) => Mix(bg, fg with { A = 1f }, fg.A);

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
    private HudElement ToolHeader(Func<byte[]?> icon, string title, HudElement status) =>
        ToolHeader(icon, () => title, status);

    // Live-title variant (the per-player dialog's name, which upgrades from the uid once it resolves).
    private HudElement ToolHeader(Func<byte[]?> icon, Func<string> title, HudElement status) => new RowElement(
        new HudElement[]
        {
            IconBox(icon, ToolIconSize),
            new ColumnElement(new HudElement[]
            {
                new TextElement(title, Color: () => TextColor(), Emphasis: true, NoWrap: true),
                status,
            }, Gap: 2f),
        }, Gap: 10f);

    // A muted subtitle line for a ToolHeader (dialogs whose header has no live status).
    private HudElement MutedLine(Func<string> text) => new TextElement(text, Color: () => MutedColor());

    // ── pill buttons ──────────────────────────────────────────────────────────────────────────────────────────────
    // Every labelled button is ONE build: a fill-tinted Bubble around a SelectableElement (the hub-card pattern; the
    // selectable's hover wash sits on the fill). MenuButtonStyle only has Outline / Filled / Glass with no custom fill,
    // so a red or grey button can't be a ButtonElement — and mixing the two builds gave different heights in one row
    // (ButtonElement 23 px vs this 30 px, in-game). ONE fixed height for every pill: a PillStrutHeight-tall spacer on
    // each side of the label + the selectable's 4 + 4 vertical padding (ChatTools' proven PillBubble pattern), with the
    // label at bold 13 so its own line height stays under the strut and can't grow the pill. (In-game: all 28 px.)
    //
    // Width: a pill directly in a Column (or stretched by a weighted Cell) FILLS it, label centred. `width` > 0 fixes the
    // label box (centred, so the pill's natural width = width + 2×PillPadX + the selectable's 2×6) — used for a pill
    // sitting in a Row ("Look up") and for equal pairs: weighted cells only split equally when both children prefer the
    // same width (WindowBuilder-Patterns.md "Weight is NOT an equal split"). Bubble caps at its parent width (MaxWidth 0),
    // but every pill here sits in a Column / stretched Cell / content Row, never a natural Cell sized from the bubble, so a
    // live label change (Band Mute's "Unmute all" ↔ "Clear allow-list") can't hit the shrink-only ratchet.
    //
    // Fill + label colour are Funcs (FillColorFn / Color) → re-evaluated each refresh, so a theme switch repaints live.
    private const float PillStrutHeight = 20f;   // + the Selectable's 4 + 4 = a 28 px pill
    private const float PillPadX        = 6f;    // strut width; + the Selectable's own 6 = 12 each side of a label
    private const float PillPairWidth   = 96f;   // label box for the two-in-a-row pairs (both halves prefer the same)

    private static HudElement PillButton(Func<string> label, Func<ColorRgba> fill, Func<ColorRgba> text, Action onClick,
        float width = 0f) => new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(PillPadX, PillStrutHeight),
                new TextElement(label, Color: () => text(), Width: width, Align: TextAlign.Center, NoWrap: true,
                    FontSize: 13) { Bold = true },
                new SpacerElement(PillPadX, PillStrutHeight),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick))
        {
            FillColorFn = () => fill(), PaddingX = 0f, PaddingY = 0f,
        };

    private HudElement PrimaryButton(Func<string> label, Action onClick, float width = 0f) =>
        PillButton(label, PrimaryFill, PrimaryText, onClick, width);
    private HudElement NeutralButton(Func<string> label, Action onClick, float width = 0f) =>
        PillButton(label, NeutralFill, NeutralText, onClick, width);
    private HudElement DangerButton(Func<string> label, Action onClick, float width = 0f) =>
        PillButton(label, () => DangerFill, DangerText, onClick, width);

    // Two pills side by side at equal widths filling the row (see the width note above).
    private static HudElement PillPair(HudElement left, HudElement right) => new RowElement(new HudElement[]
    {
        new CellElement(left,  Weight: 1f),
        new CellElement(right, Weight: 1f),
    }, Gap: 8f);

    // A rounded panel (background darkened, live): optional small UPPERCASE caption (live) + optional right-aligned muted
    // hint (live), then the children. The Bubble sits directly in the window Column, which force-expands its width → it
    // FILLS the window width instead of hugging (the hug only happens inside a Row), and its padded VLG stretches the
    // inner Column (lists, full-width buttons) to the panel's inner width.
    private HudElement SectionPanel(Func<string>? caption, Func<string>? hint, params HudElement[] children)
    {
        var items = new List<HudElement>(children.Length + 1);
        if (caption != null || hint != null)
            items.Add(new RowElement(new HudElement[]
            {
                new TextElement(() => caption?.Invoke().ToUpperInvariant() ?? "",
                    Color: () => MutedColor(), NoWrap: true, FontSize: 12) { Bold = true },
                new SpacerElement(0f),   // flexible: pushes the hint to the right edge
                new TextElement(() => hint?.Invoke() ?? "", Color: () => MutedColor(), NoWrap: true),
            }, Gap: 6f));
        items.AddRange(children);
        return new BubbleElement(new ColumnElement(items, Gap: 6f))
        {
            FillColorFn = () => PanelColor(), CornerRadius = 6f, PaddingX = 10f, PaddingY = 8f,
        };
    }

    // An on/off status chip. On = bold text in the chip's fixed (semantic) colour on a ~22% pill of it; off = MenuMuted
    // text on a faint MenuMuted pill (live). Two complete pills (bold can't be toggled live) swapped by a Conditional
    // wrapping the WHOLE pill — never a Conditional inside a bubble's measured child (the bubble re-hug gotcha) — each in
    // the same fixed-width cell.
    private HudElement StatusChip(string label, ColorRgba color, Func<bool> on) => new ConditionalElement(on,
        ChipPill(new TextElement(() => label, Color: () => color, Align: TextAlign.Center, NoWrap: true) { Bold = true },
                 () => color with { A = 0.22f }),
        ChipPill(new TextElement(() => label, Color: () => MutedColor(), Align: TextAlign.Center, NoWrap: true),
                 () => MutedColor() with { A = 0.15f }));

    // The fixed-width cell caps the bubble at ChipWidth (MaxWidth 0 = parent width) and stretches it to exactly that,
    // so both variants are the same WIDTH. HEIGHT is pinned separately: the bubble hugs its content's height, and bold
    // (on) text is taller than regular (off) text, so an off chip came out visibly shorter and the row jumped on a toggle
    // (in-game). A ChipStrutHeight-tall zero-width spacer beside the label pins both variants to strut + 2×PaddingY = 24,
    // the on chip's measured height (the 56 px Auto-Accept hub card = 8 + title + 2 + 24), so the card height is unchanged.
    private const float ChipStrutHeight = 22f;

    private static HudElement ChipPill(TextElement text, Func<ColorRgba> fill) => new CellElement(
        new BubbleElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, ChipStrutHeight),   // width 0 + height > 0 = a non-flexible height strut
                text,
            }, Gap: 0.01f, Justify: RowJustify.Center))
        { FillColorFn = () => fill(), PaddingX = 4f, PaddingY = 1f },
        Width: ChipWidth);
}
