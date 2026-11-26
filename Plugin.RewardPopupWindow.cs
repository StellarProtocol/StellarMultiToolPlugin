using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// UI for the Daily Rewards tool (mechanism: Plugin.RewardPopup.cs). Built to the approved prototype (option A,
// _scratch\prototypes\multitool-reward-popup-placement.html): a "Daily Rewards" hub card (last) + this window =
// ToolHeader (gift icon, title, live On/Off), one MONTHLY CARD POPUP panel (muted description + a settings row
// "Close it automatically" with the house on/off chip), then a full-width Neutral "Close".
// The prototype drew a switch; the house toggle is the clickable On/Off chip (Plugin.Style.Toggle.cs ToggleChip), placed
// right of the label like the Performance window's rows.
public sealed partial class Plugin
{
    private IWindowControl _rewardPopupWindow = null!;

    private const float RpRowHeight = 28f;   // the settings-row height of the Performance window (EffRow)

    private static byte[]? DailyRewardsIconPng() => CachedPng("icon-dailyrewards.png");

    private HudElement BuildRewardPopupRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader(DailyRewardsIconPng, "Daily Rewards", RewardPopupWindowStatus()),
        SectionPanel(() => "Monthly card popup", null,
            new TextElement(() => "Your daily reward is already given when you log in. This closes the reward popup " +
                                  "for you and marks it as seen, so it doesn't come back.",
                Color: () => MutedColor()),
            // [strut | label (fills, ellipsizes) | chip] — the strut pins the 28 px row, rows align MiddleLeft so the
            // 24 px chip stays centred. One save per click (SetAutoCloseRewardPopup).
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, RpRowHeight),
                new CellElement(new TextElement(() => "Close it automatically", Color: () => TextColor(), NoWrap: true)
                    { Ellipsize = true }, Weight: 1f),
                ToggleChip(() => AutoCloseRewardPopup, () => SetAutoCloseRewardPopup(!AutoCloseRewardPopup)),
            }, Gap: 8f)),
        NeutralButton(() => "Close", () => _rewardPopupWindow.SetVisible(false)),
    }, Gap: 8f);

    // Hub card status: "Auto-close popup: On" (green) / "Auto-close popup: Off" (muted). Constant strings — the Funcs are
    // polled every refresh, so nothing is built per poll.
    private HudElement RewardPopupHubStatus() => new TextElement(
        () => _rpOn ? "Auto-close popup: On" : "Auto-close popup: Off",
        Color: () => _rpOn ? StatusOn : MutedColor(), NoWrap: true);

    // Window header status: "On" (green) / "Off" (muted), same as PowerSaveWindowStatus.
    private HudElement RewardPopupWindowStatus() => new TextElement(() => _rpOn ? "On" : "Off",
        Color: () => _rpOn ? StatusOn : MutedColor(), NoWrap: true);
}
