using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// Per-setting help (house help popover, Stellar-Design-Language.md §4; ported from MinimalNameplate Plugin.Help.cs,
// itself from Maestro Plugin.Help*.cs). The window framework has no hover tooltip, so every setting row ends in a round
// "?" that opens ONE shared floating popover anchored under it:
//   [32 px accent tile with a white bold "?"] [bold setting name / muted "SECTION · HELP"] … [round ✕]
//   then the help text in one raised tile, 360 px wide.
// No framework title bar (the header carries the title and the close); with ShowTitleBar = false the whole frame is the
// drag handle. Re-clicking the open "?" closes it, and so does closing the window it belongs to (_tipOwner).
// Content is registered by key at build time (HelpDot).
public sealed partial class Plugin
{
    private readonly Dictionary<string, (string Title, string Body, string Section)> _helpEntries = new();
    private readonly Dictionary<string, HoverFlag> _helpHover = new();

    private IWindowControl _tipWindow = null!;
    private IWindowControl? _tipOwner;   // the window whose "?"s open the popover; closing it closes the popover
    private string _tipKey = "";         // which help entry the popover shows; "" = closed
    private const float TipWidth    = 360f;
    private const float TipIconSize = 32f;
    private const float HelpSize    = 24f;   // the round "?" — exactly 24 × 24 (RoundRadius)
    private WindowRect _tipRect;             // where the popover should sit (re-applied for a few frames after opening)
    private int _tipRepositionTicks;
    private readonly HoverFlag _tipCloseHover = new();

    private void InitHelpTip()
    {
        _tipWindow = RegisterTipWindow();
        _services.Framework.Update += TipTick;
    }

    private void DisposeHelpTip()
    {
        try { _services.Framework.Update -= TipTick; } catch { }
    }

    // Registers help content for `key` and returns its "?". Append it as the LAST child of a row so the "?"s line up in
    // the panel's right-edge column. `section` = the panel caption, shown in the popover subtitle.
    private HudElement HelpDot(string key, string section, string title, string body)
    {
        _helpEntries[key] = (title, body, section);
        var hover = _helpHover[key] = new HoverFlag();
        return new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                // Height strut = the side; the label box sets the width. NOTHING taller than 24 may sit inside, or the
                // circle turns oval. CenterGlyphs centres the drawn "?" in its box.
                new SpacerElement(0f, HelpSize),
                new TextElement(() => "?", Color: () => NeutralText(), Width: HelpSize, Align: TextAlign.Center,
                    NoWrap: true, FontSize: 12) { Bold = true, CenterGlyphs = true },
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { })                       // real work is in OnClickWithRect (needs the button's rect)
            {
                Bare = true,
                OnHover = over => hover.On = over,
                OnClickWithRect = r => ToggleTip(key, r),
            })
        {
            FillColorFn = () => Fade(_tipKey == key && _tipWindow.IsShown ? Over(AccentColor() with { A = 0.35f }, NeutralFill())
                              : hover.On ? Over(TextColor() with { A = HoverMix }, NeutralFill())
                              : NeutralFill()),   // window opacity (Plugin.Style.cs Fade)
            CornerRadius = RoundRadius(HelpSize), PaddingX = 0f, PaddingY = 0f,
        };
    }

    private void ToggleTip(string key, WindowRect r)
    {
        if (_tipKey == key && _tipWindow.IsShown) { CloseTip(); return; }
        _tipKey = key;
        // OnClickWithRect reports SCREEN PIXELS; SetRect takes CANVAS units — scale first, or the popover drifts off its
        // "?" whenever the UI scale isn't 1 (WindowBuilder-Patterns.md, click-to-open tooltip).
        var fw = _services.Framework;
        float k = fw.ScreenWidth > 0 && fw.CanvasWidth > 1f ? fw.CanvasWidth / fw.ScreenWidth : 1f;
        r = new WindowRect(r.X * k, r.Y * k, r.Width * k, r.Height * k);
        float canvasH = fw.CanvasHeight > 1f ? fw.CanvasHeight : fw.ScreenHeight;
        // Below the "?", right-aligned to it, so it opens to the LEFT of the "?" column instead of covering the setting.
        float x = Math.Max(10f, r.X + r.Width - TipWidth);
        float y = Math.Min(r.Y + r.Height + 4f, canvasH - 40f);
        _tipRect = new WindowRect(x, y, TipWidth, 0f);
        _tipWindow.SetVisible(true);
        _tipWindow.SetRect(_tipRect);
        // Always front: every MultiTool open fronts (ZFront is sticky — a never-fronted popover would sit UNDER the
        // fronted tool window; CLAUDE.md Rule 5 exception), and switching "?"s re-opens an already-visible window.
        _tipWindow.BringToFront();
        _tipWindow.MarkDirty();      // re-pull title / body for the new key this frame
        // First-ever open mounts the window and applies its DefaultRect AFTER this SetRect → re-apply the anchored rect
        // for the next few frames so it snaps to the "?" on first open too.
        _tipRepositionTicks = 4;
    }

    private void CloseTip()
    {
        _tipKey = "";
        _tipWindow.SetVisible(false);
    }

    // Framework.Update: early-out unless the popover is open. Re-asserts the position for a few frames after opening,
    // and closes the popover once its owner window is gone.
    private void TipTick(float _)
    {
        if (_tipKey.Length == 0) return;
        if (_tipOwner != null && !_tipOwner.IsShown) { CloseTip(); return; }
        if (_tipRepositionTicks <= 0) return;
        _tipRepositionTicks--;
        if (_tipWindow.IsShown) _tipWindow.SetRect(_tipRect);
    }

    private string TipTitle() => _helpEntries.TryGetValue(_tipKey, out var e) ? e.Title : "";
    private string TipBody()  => _helpEntries.TryGetValue(_tipKey, out var e) ? e.Body  : "";
    private string TipSubtitle() => _helpEntries.TryGetValue(_tipKey, out var e) && e.Section.Length > 0
        ? (e.Section + " · Help").ToUpperInvariant()
        : "HELP";

    private IWindowControl RegisterTipWindow()
    {
        IWindowControl w = null!;
        w = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "multitool.helptip",
                Title:       "Help",
                // A constant (not ScreenWidth-derived; see RegisterWindow). Every open re-anchors it under its "?".
                DefaultRect: new WindowRect(2540f - TipWidth, 20f, TipWidth, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = true, StartVisible = false, ShowTitleBar = false,
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                   && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            Root: BuildTipRoot(),
            OnClose: () => { _tipKey = ""; w!.SetVisible(false); }));
        _windows.Add(w);   // hidden on logout + removed on dispose with the rest
        return w;
    }

    private HudElement BuildTipRoot() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            TipIconTile(),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(TipTitle, Color: () => TextColor(), NoWrap: true, FontSize: 14) { Bold = true, Ellipsize = true },
                new TextElement(TipSubtitle, Color: () => MutedColor(), NoWrap: true, FontSize: 11) { Bold = true, Ellipsize = true },
            }, Gap: 1f), Weight: 1f),
            TipCloseButton(),
        }, Gap: 10f),
        // The help text: one raised tile (house Tile colour, theme-live), wrapping inside the 360 px popover.
        new BubbleElement(new TextElement(TipBody, Color: () => TextColor()))
        {
            FillColorFn = () => Fade(TileColor()), CornerRadius = 6f, PaddingX = 10f, PaddingY = 8f,
        },
    }, Gap: 10f);

    // 32 px accent tile with a white bold "?" (the IconBox look, drawn with text instead of a PNG).
    private HudElement TipIconTile() => new CellElement(new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, TipIconSize),
            new TextElement(() => "?", Color: () => White, Width: TipIconSize, Align: TextAlign.Center, NoWrap: true,
                FontSize: 18) { Bold = true, CenterGlyphs = true },
        }, Gap: 0.01f, Justify: RowJustify.Center))
    {
        FillColorFn = () => Fade(AccentColor()), CornerRadius = MathF.Round(TipIconSize * 0.22f), PaddingX = 0f, PaddingY = 0f,
    }, Width: TipIconSize);

    // Icon-only ✕ (plain text — bold ✕ can draw blank): a round 24 px hover wash around a Bare selectable.
    private HudElement TipCloseButton() => new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, 24f),
            new TextElement(() => "✕", Color: () => _tipCloseHover.On ? TextColor() : MutedColor(), Width: 24f,
                Align: TextAlign.Center, NoWrap: true),
        }, Gap: 0.01f, Justify: RowJustify.Center),
        OnClick: CloseTip) { Bare = true, OnHover = over => _tipCloseHover.On = over })
    {
        // A translucent wash over the faded window: the factor goes on its own alpha (Fade(Transparent) stays clear).
        FillColorFn = () => _tipCloseHover.On ? Fade(Menu.MenuText with { A = HoverMix }) : Transparent,
        CornerRadius = RoundRadius(24f), PaddingX = 0f, PaddingY = 0f,
    };
}
