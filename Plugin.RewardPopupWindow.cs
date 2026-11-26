using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// UI for the Daily Rewards tool (mechanism: Plugin.RewardPopup.cs). Built to the approved prototype (option A,
// _scratch\prototypes\multitool-reward-popup-placement.html): a "Daily Rewards" hub card (last) + this window =
// ToolHeader (gift icon, title, live On/Off), one MONTHLY CARD POPUP panel (muted description + a ToggleElement switch
// left of "Close it automatically"), then a full-width Neutral "Close".
public sealed partial class Plugin
{
    private IWindowControl _rewardPopupWindow = null!;

    private static byte[]? DailyRewardsIconPng() => CachedPng("icon-dailyrewards.png");

    private HudElement BuildRewardPopupRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader(DailyRewardsIconPng, "Daily Rewards", RewardPopupWindowStatus()),
        SectionPanel(() => "Monthly card popup", null,
            new TextElement(() => "Your daily reward is already given when you log in. This closes the reward popup " +
                                  "for you and marks it as seen, so it doesn't come back.",
                Color: () => MutedColor()),
            // Switch LEFT of the label (prototype): the shared capsule row (Plugin.InviteAccept.cs InviteToggleRow =
            // ToggleElement + label, Gap 6, the Band Mute "Enable" pattern). One save per click (SetAutoCloseRewardPopup).
            InviteToggleRow("Close it automatically", () => AutoCloseRewardPopup, SetAutoCloseRewardPopup)),
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
