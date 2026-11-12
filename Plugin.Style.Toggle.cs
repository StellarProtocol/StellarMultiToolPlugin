using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// Settings-row controls for the newer house style (Stellar-Design-Language.md §0 + "Clickable on/off chip"), first used
// by the Effect Filter window (approved prototype, 2026-11-11). Recipes ported from MinimalNameplate Plugin.Style.cs
// (measured in game: 64 × 24 chip in both states) and Maestro: every control here is ROUND (radius side/2 − 1, R1) with
// its hover drawn by the Bubble fill around a Bare selectable (R2), so no square hover wash shows.
//
// `dim` (polled) greys a control out while keeping it clickable and the same size — a setting with no effect right now.
// That is NOT the disabled look: a disabled/locked control is RECESSED (rule 22: sunken fill, faint label).
public sealed partial class Plugin
{
    private const float ToggleChipHeight = 24f;
    private const float ChipOnWash       = 0.25f;   // on chip = bold accent label on a 25% accent wash (prototype)
    private const float ChipHoverAlpha   = 0.12f;   // hover adds this to the on-chip wash
    private const float HoverMix         = 0.10f;   // hover elsewhere = MenuText@0.10 over the fill
    private const float DimMix           = 0.45f;   // dimmed = 45% of the way from the panel to the colour
    private const float CautionWash      = 0.22f;   // Caution = #ffcc73@0.22 over the panel (Stellar-Design-Language §3)
    private const float ScopePillHeight  = 28f;
    private const float ScopePillPadX    = 12f;

    private ColorRgba AccentColor() => Menu.MenuAccent;

    // The corner radius for a ROUND bubble of height `side`: side / 2 − 1, NOT side / 2 (the 9-slice min size is
    // 2r + 2, so side / 2 drew every round control 2 px taller than its content; Maestro, measured 2026-11-06).
    private static float RoundRadius(float side) => side / 2f - 1f;

    private sealed class HoverFlag { public bool On; }

    private ColorRgba Dimmed(ColorRgba c, Func<bool>? dim) => dim != null && dim() ? Mix(PanelColor(), c, DimMix) : c;

    // Bold (TMP) labels sit ~1.5–2 px HIGH when centred; a 2 px spacer above moves the text down 1 px (Maestro measured).
    private static HudElement DropBold(HudElement label) => new ColumnElement(new HudElement[]
    {
        new SpacerElement(0f, 2f),
        label,
    }, Gap: 0.01f);

    // ── clickable on/off chip (64 × 24 in BOTH states) ───────────────────────────────────────────────────────────
    // On = bold accent label on a 25% accent wash; off = regular muted label on the neutral fill (prototype) — or, with
    // `cautionOn` (a row whose ON is risky, e.g. hiding boss telegraphs), the ON chip takes the Caution look instead:
    // bold amber label (4.5:1) on the amber wash. Two complete chips swapped by a Conditional wrapping the WHOLE chip
    // (never a Conditional inside a bubble's measured child), inside one fixed-width cell; both share one HoverFlag.
    // MaxWidth pins the width: a MaxWidth-0 bubble under a Conditional caps itself at the 100 px default
    // (WindowBuilder-Patterns, Dungeon Confirm 6b19310).
    private HudElement ToggleChip(Func<bool> on, Action toggle, Func<bool>? dim = null, bool cautionOn = false)
    {
        var hover = new HoverFlag();
        float onWash = cautionOn ? CautionWash : ChipOnWash;   // the accent itself is read live (theme can change)
        ColorRgba OnFillRaw()  => Over((cautionOn ? ChipJoin : AccentColor()) with
                                       { A = onWash + (hover.On ? ChipHoverAlpha : 0f) }, PanelColor());
        ColorRgba OffFillRaw() => hover.On ? Over(TextColor() with { A = HoverMix }, NeutralFill()) : NeutralFill();
        return new CellElement(new ConditionalElement(on,
            ChipShell(DropBold(new TextElement(() => "On",
                    Color: () => Dimmed(EnsureContrast(cautionOn ? ChipJoin : AccentColor(), OnFillRaw(), PillMinContrast),
                        dim), Width: ChipWidth, Align: TextAlign.Center, NoWrap: true, FontSize: 13) { Bold = true }),
                () => Dimmed(OnFillRaw(), dim), hover, toggle),
            ChipShell(new TextElement(() => "Off", Color: () => Dimmed(MutedColor(), dim), Width: ChipWidth,
                    Align: TextAlign.Center, NoWrap: true),
                () => Dimmed(OffFillRaw(), dim), hover, toggle)),
            Width: ChipWidth);
    }

    private HudElement ChipShell(HudElement label, Func<ColorRgba> fill, HoverFlag hover, Action onClick)
        => new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, ToggleChipHeight),   // width 0 + height > 0 = a non-flexible height strut
                label,
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fade(fill()), CornerRadius = RoundRadius(ToggleChipHeight), PaddingX = 0f, PaddingY = 0f,
            MaxWidth = ChipWidth,
        };

    // ── segmented choice pill (28 px, round) ────────────────────────────────────────────────────────────────────
    // Selected = Primary (accent), the other = Neutral; the label is bold in both, so selecting only changes colours
    // (no Conditional, no reflow). Hugs its label inside a Row. Bare selectable → the 28 px height comes from the strut
    // spacers (one Row, R4), the round hover from the fill.
    private HudElement ChoicePill(string label, Func<bool> selected, Action onClick, Func<bool>? dim = null)
    {
        var hover = new HoverFlag();
        ColorRgba FillRaw()
        {
            var f = selected() ? PrimaryFill() : NeutralFill();
            return hover.On ? Over(TextColor() with { A = HoverMix }, f) : f;
        }
        ColorRgba LabelRaw() => selected() ? EnsureContrast(White, FillRaw(), 3.0f)
                                           : EnsureContrast(TextColor(), FillRaw(), PillMinContrast);
        return new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(ScopePillPadX, ScopePillHeight),
                DropBold(new TextElement(() => label, Color: () => Dimmed(LabelRaw(), dim), NoWrap: true, FontSize: 13)
                    { Bold = true }),
                new SpacerElement(ScopePillPadX, ScopePillHeight),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fade(Dimmed(FillRaw(), dim)), CornerRadius = RoundRadius(ScopePillHeight),
            PaddingX = 0f, PaddingY = 0f,
        };
    }
}
