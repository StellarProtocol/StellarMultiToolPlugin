using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// The Auto-Accept window's "Players" roster table (header, pooled rows, ✕ remove icon, switch cells), split out of
// Plugin.InviteAccept.cs to stay under the 500-line cap. Data + switch handlers live in Plugin.InviteRoster.cs.
public sealed partial class Plugin
{
    // The ONE roster panel: caption "Players (N)" + table header + divider + rows (Name (uid) · Party · Social · ✕) +
    // one row with "Add player…" and "Clear all". Turning both switches off keeps the row (roster-only); only ✕ /
    // Clear all delete. The header is styled as a header (bold + muted, divider under it) so it doesn't read as a data
    // row; the count lives in the caption, not the header.
    private HudElement BuildPlayersSection() => SectionPanel(() => $"Players ({_invRosterView.Count})", null,
        // Header cells mirror the row cells' widths exactly (incl. the empty ✕ column + same Gap), and the two switch
        // labels are centred by the SAME inner-Row mechanism as SwitchCell — so label and capsule share one centre line.
        // The transparent bubble carries the SAME horizontal padding as each row's tile (RosterTile) so the header
        // columns stay over the row columns.
        RosterTile(new RowElement(new HudElement[]
        {
            new CellElement(InviteHeaderText(() => "Player"), Weight: 1f),
            HeaderSwitchCell("Party"),
            HeaderSwitchCell("Social"),
            new CellElement(new TextElement(() => ""), Width: InviteRemoveColW),
        }, Gap: 4f), () => Transparent),
        new SeparatorElement(),   // the themed divider under the header
        new VirtualListElement(
            Count:     () => _invRosterView.Count,
            RowHeight: InviteRowHeight,
            Pool:      BuildRosterPool(),
            OnWindow:  f => _invRosterFirst = f,
            Height:    InviteListHeight),
        // Equal halves: both pills share PillPairWidth so the weighted cells split equally (Plugin.Style.cs PillPair).
        PillPair(
            PrimaryButton(() => "Add player…", OpenInviteAddDialog, PillPairWidth),
            // Asks first — the confirm window runs the actual ClearAllInviteLists (Plugin.InviteClearConfirm.cs).
            NeutralButton(() => "Clear all", OpenInviteClearConfirm, PillPairWidth)));

    private const float InviteTilePadX   = 6f;     // roster tile inner padding (header uses the same, so columns align)

    // A roster row's raised tile (TileColor, live from the theme), or — with a transparent fill — the header's matching padding.
    private HudElement RosterTile(HudElement row, Func<ColorRgba> fill) =>
        new BubbleElement(row) { FillColorFn = () => Fade(fill()), PaddingX = InviteTilePadX, PaddingY = 0f };

    // Table-header label: bold + muted so it reads as a header, not a data row. NoWrap keeps it on the row pitch.
    private TextElement InviteHeaderText(Func<string> text) => new TextElement(text,
        Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted, Emphasis: true, NoWrap: true);

    // Header label for a switch column: same Width + inner RowElement(Justify: Center) as SwitchCell. Text-align alone
    // isn't enough — the CellElement force-expands its child, so the label's box (not its glyphs) decides the centre;
    // the Row wrapper keeps the label at its intrinsic width and centres that box, exactly like the capsule below it
    // (WindowBuilder-Patterns.md §SelectableElement-and-CellElement force-expand).
    private HudElement HeaderSwitchCell(string label) => new CellElement(
        new RowElement(new HudElement[] { InviteHeaderText(() => label) }, Justify: RowJustify.Center),
        Width: InviteSwitchColW);

    // Fixed row pool reused across logical rows (_invRosterFirst = the window's first logical index, set by OnWindow).
    // Every lambda re-resolves its uid through RosterAt(first + slot), so a pool slot never acts on a stale person.
    // That matters because rows shift: ✕ rebuilds _invRosterView synchronously inside RemoveFromInviteList, so a
    // different person slides into this slot (a switch flip no longer reorders — the stored invite_player_order keeps
    // each row where it was added, Plugin.InviteRoster.cs — but the click-time resolve stays as the guard). The framework
    // toggle is a Button whose click runs set(!get()) once — both resolve the uid at click time (the person actually
    // shown), and the per-frame binding only re-reads Get for the visual, never calls Set — so a shift can't flip the
    // next person's switch. The ✕ likewise resolves its uid at click time.
    private HudElement[] BuildRosterPool()
    {
        var pool = new HudElement[InviteUidPoolSize];
        for (int s = 0; s < InviteUidPoolSize; s++)
        {
            int slot = s;
            string? Uid() => RosterAt(_invRosterFirst + slot);
            pool[s] = RosterTile(new RowElement(new HudElement[]
            {
                // NoWrap: a long name must clip, never wrap — rows sit at a fixed RowHeight pitch, so a 2-line row would
                // overlap the next one (WindowBuilder-Patterns.md §"Hard-clip a long list row").
                new CellElement(new TextElement(() => Uid() is { } u ? InviteRowText(u) : "", NoWrap: true), Weight: 1f),
                SwitchCell(() => Uid() is { } u && InParty(u),
                           on => { if (Uid() is { } u) SetInviteSwitch(u, social: false, on); }),
                SwitchCell(() => Uid() is { } u && InSocial(u),
                           on => { if (Uid() is { } u) SetInviteSwitch(u, social: true, on); }),
                // Same inner-RowElement trick as SwitchCell so the cell doesn't stretch the icon across the column.
                new CellElement(new RowElement(new HudElement[]
                    {
                        RemoveIcon(slot, Uid, () => { if (Uid() is { } u) RemoveFromInviteList(u); }),
                    }, Justify: RowJustify.Right),
                    Width: InviteRemoveColW),
            }, Gap: 4f), TileColor);
        }
        return pool;
    }

    // Icon-only ✕: at rest just a MenuMuted glyph (no border/fill); on hover a red glyph on a soft red rounded wash.
    // Pill technique: a Bubble whose FillColorFn follows a per-slot hover flag, around a SelectableElement.Bare (Bare =
    // no tint/padding of its own, so the selectable's default hover wash can't stack with the red one). The strut row
    // pins it to ~24×24 (2×3 + glyph wide, 20 + 2×2 tall), the old ✕ button's footprint, so the columns and header don't
    // shift; the row's MiddleLeft alignment keeps it vertically centred.
    // ⚠️ Pooled row: the flag is keyed per pool SLOT and reset whenever the slot shows a different uid (a ✕ click
    // slides the next person under the pointer; they must not inherit a red hover state until the pointer re-enters).
    private static readonly ColorRgba RemoveWash  = Rgb(0xff4d4f, 0.22f);
    private static readonly ColorRgba RemoveGlyph = Rgb(0xff8a8b);
    private readonly bool[]    _invRemoveHover    = new bool[InviteUidPoolSize];
    private readonly string?[] _invRemoveHoverUid = new string?[InviteUidPoolSize];

    private bool RemoveHovered(int slot, Func<string?> uid)
    {
        var u = uid();
        if (u != _invRemoveHoverUid[slot]) { _invRemoveHoverUid[slot] = u; _invRemoveHover[slot] = false; }
        return _invRemoveHover[slot];
    }

    private HudElement RemoveIcon(int slot, Func<string?> uid, Action onClick) => new BubbleElement(
        new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(3f, 20f),
                new TextElement(() => "✕", NoWrap: true,
                    Color: () => RemoveHovered(slot, uid)
                        ? EnsureContrast(RemoveGlyph, Over(RemoveWash, TileColor()), 3f)
                        : MutedColor()),
                new SpacerElement(3f, 20f),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick)
        {
            Bare = true,
            OnHover = over => { _invRemoveHover[slot] = over; _invRemoveHoverUid[slot] = uid(); },
        })
    {
        FillColorFn = () => RemoveHovered(slot, uid) ? Fade(RemoveWash) : Transparent,   // wash on the faded tile
        CornerRadius = 4f, PaddingX = 2f, PaddingY = 2f,
    };

    // A toggle capsule centred in a fixed-width column. The inner RowElement matters: CellElement force-expands its
    // child's width, which would stretch the 30px capsule across the column; a RowElement's HLG keeps it intrinsic
    // (WindowBuilder-Patterns.md §ToggleElement / §SelectableElement-and-CellElement force-expand).
    private static HudElement SwitchCell(Func<bool> get, Action<bool> set) => new CellElement(
        new RowElement(new HudElement[] { new ToggleElement(Label: () => "", Get: get, Set: set) },
                       Justify: RowJustify.Center),
        Width: InviteSwitchColW);
}
