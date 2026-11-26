using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// Add Player › "Find player" results area (approved mockup multitool-addplayer-name.html). ONE fixed height in every
// state, so the window never jumps: the UID card (digits-only search), the name-search rows, or one centred muted message
// (empty · Searching… · No player named "<q>" · Search failed · try again).
//
// Name rows = a VirtualList of raised tiles (38 px + a 4 px gap — house R15: the VirtualList slot stretches its child, so
// the gap is part of the pool element): 30 px avatar · bold name over a muted "UID <showId> · <class> · Lv <n>" (both
// ellipsized) · a Primary "Add" pill, or a green "Listed" when the player is already on the roster. Pooled rows read
// cached strings only (InvMatch, built once per answer in Plugin.InviteFind.cs).
public sealed partial class Plugin
{
    private const float InvFindRowH = 38f, InvFindRowGap = 4f, InvFindRowPitch = InvFindRowH + InvFindRowGap;
    private const float InvFindListH = 150f;                 // the mockup's list height (3 rows + a peek of the 4th)
    private const int   InvFindPool = 7;                     // visible rows + headroom
    private const float InvFindAvatar = 30f;
    private const float InvFindAddW = 34f;                   // "Add" label box → a 58 px pill (34 + 2×12)
    private const float InvFindAddCellW = InvFindAddW + 24f;
    private const float InvLookupCardH = 72f;                // the UID card: 56 px avatar + 2×8 tile padding

    private int _invFindOffset;

    private HudElement BuildInviteFindResults() => new ConditionalElement(
        () => _invFindMode == InvFindMode.Uid && LookupActive(),
        // UID card at the top of the same box: the strut under it makes up the rest of the list height.
        new ColumnElement(new HudElement[]
        {
            BuildLookupCard(),
            new SpacerElement(0f, InvFindListH - InvLookupCardH),
        }, Gap: 0.01f),
        new ConditionalElement(() => _invFindMode == InvFindMode.Name && _invMatches.Count > 0,
            BuildInviteFindList(),
            BuildInviteFindMessage()));

    private HudElement BuildInviteFindList()
    {
        var pool = new HudElement[InvFindPool];
        for (int i = 0; i < pool.Length; i++) pool[i] = InvFindRow(i);
        return new VirtualListElement(Count: () => _invMatches.Count, RowHeight: InvFindRowPitch, Pool: pool,
            OnWindow: first => _invFindOffset = first, Height: InvFindListH)
        {
            ResetScroll = () => { if (!_invFindScrollReset) return false; _invFindScrollReset = false; return true; },
        };
    }

    // The message box: a faint panel-dark tile of the list's height with one centred muted line.
    private HudElement BuildInviteFindMessage() => new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, InvFindListH),   // the list's height
            new TextElement(() => _invFindMode == InvFindMode.Name ? _invFindMsg : InvFindMsgEmpty,
                Color: () => MutedColor(), Align: TextAlign.Center, NoWrap: true) { Ellipsize = true },
        }, Gap: 0.01f, Justify: RowJustify.Center))
    {
        // Slightly lighter than the panel (the mockup's message box), but not a raised tile: it is not a control.
        FillColorFn = () => Fade(Over(TextColor() with { A = 0.03f }, PanelColor())), CornerRadius = 6f, PaddingX = 12f, PaddingY = 0f,
    };

    private InvMatch? InvFindRowAt(int i)
    {
        int k = _invFindOffset + i;
        return k >= 0 && k < _invMatches.Count ? _invMatches[k] : null;
    }

    private bool InvFindRowListed(int i) => InvFindRowAt(i) is { } m && IsListed(m.CharId);

    private void InvFindAddRow(int i)
    {
        if (InvFindRowAt(i) is { } m) InvFindAdd(m);   // Plugin.InviteFind.cs
    }

    // Avatar: a faint tile while there is no texture yet (still downloading / default face not decoded), then the photo.
    private HudElement InvFindAvatarCell(int i) => new CellElement(new BubbleElement(
        new GameTextureElement(() => InvMatchAvatar(InvFindRowAt(i)), (int)InvFindAvatar, (int)InvFindAvatar)
            { CornerRadius = 6 })
        {
            FillColorFn = () => InvMatchAvatar(InvFindRowAt(i)) != null ? Transparent : Fade(MutedColor() with { A = 0.15f }),
            CornerRadius = 6f, PaddingX = 0f, PaddingY = 0f,
        }, Width: InvFindAvatar);

    // Pool element = Column[38 px tile, 4 px spacer]. The 38 px height strut sits in the pill's cell beside the 28 px pill.
    private HudElement InvFindRow(int i) => new ColumnElement(new HudElement[]
    {
        new BubbleElement(new RowElement(new HudElement[]
        {
            InvFindAvatarCell(i),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(() => InvFindRowAt(i)?.Name ?? "", Color: () => TextColor(), NoWrap: true, FontSize: 13)
                    { Bold = true, Ellipsize = true },
                new TextElement(() => InvFindRowAt(i)?.Sub ?? "", Color: () => MutedColor(), NoWrap: true, FontSize: 11)
                    { Ellipsize = true },
            }, Gap: 1f), Weight: 1f),
            new CellElement(new RowElement(new HudElement[]
                {
                    new SpacerElement(0f, InvFindRowH),
                    new ConditionalElement(() => InvFindRowListed(i),
                        new TextElement(() => "Listed", Color: () => StatusOn, NoWrap: true, FontSize: 12),
                        PrimaryButton(() => "Add", () => InvFindAddRow(i), InvFindAddW)),
                }, Gap: 0.01f, Justify: RowJustify.Center),
                Width: InvFindAddCellW),
        }, Gap: 8f))
        {
            FillColorFn = () => Fade(TileColor()), CornerRadius = 6f, PaddingX = 6f, PaddingY = 0f,
        },
        new SpacerElement(0f, InvFindRowGap),
    }, Gap: 0.01f);
}
