using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// UI for the Game UI tool (mechanism: Plugin.RewardPopup.cs). Built to the approved prototype (option A,
// _scratch\prototypes\multitool-reward-popup-placement.html), then renamed from "Daily Rewards" to the generic
// "Game UI" so later game-UI tweaks (more popup auto-closes…) land here as extra sections: a "Game UI" hub card (last)
// + this window = ToolHeader (icon, title, live "N tweak(s) on"/"All off"), one MONTHLY CARD REWARD panel (muted
// description + a ToggleElement switch left of "Close it automatically"), a WARDROBE panel of the same shape
// (Plugin.WardrobeMerge.cs), then a full-width Neutral "Close". The window auto-sizes (height 0), so no height tuning.
// The window id "multitool.dailyrewards", config key and icon file keep their old names so saved state survives.
public sealed partial class Plugin
{
    private IWindowControl _rewardPopupWindow = null!;

    private static byte[]? DailyRewardsIconPng() => CachedPng("icon-dailyrewards.png");

    private HudElement BuildRewardPopupRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader(DailyRewardsIconPng, "Game UI", GameUiStatus()),
        SectionPanel(() => "Monthly card reward", null,
            new TextElement(() => "Your daily reward is already given when you log in. This closes the reward popup " +
                                  "for you and marks it as seen, so it doesn't come back.",
                Color: () => MutedColor()),
            // Switch LEFT of the label (prototype): the shared capsule row (Plugin.InviteAccept.cs InviteToggleRow =
            // ToggleElement + label, Gap 6, the Band Mute "Enable" pattern). One save per click (SetAutoCloseRewardPopup).
            InviteToggleRow("Close it automatically", () => AutoCloseRewardPopup, SetAutoCloseRewardPopup)),
        // Game UI tweak #2 (Plugin.WardrobeMerge.cs): same panel shape — muted description + switch row.
        SectionPanel(() => "Wardrobe", null,
            new TextElement(() => "Shows clothes and accessories side by side in one tab, so you don't have to " +
                                  "switch tabs. Applies the next time you open the Wardrobe.",
                Color: () => MutedColor()),
            InviteToggleRow("Merge clothes and accessory tabs", () => WardrobeMergeTabs, SetWardrobeMergeTabs)),
        NeutralButton(() => "Close", () => _rewardPopupWindow.SetVisible(false)),
    }, Gap: 8f);

    // Number of Game UI tweaks switched on. A new tweak adds its own `+ (flag ? 1 : 0)` term here, and
    // GameUiTweakText below grows with it.
    private int GameUiTweaksOn() => (_rpOn ? 1 : 0) + (_wmOn ? 1 : 0);

    // Labels precomputed once, indexed by the on-count: the status Funcs are polled every refresh, so nothing is built
    // per poll. 0 = "All off" (muted); 1 = "1 tweak on"; N>1 = "N tweaks on" (green). 16 slots is far more tweaks
    // than the card will ever hold; a larger count clamps to the last label.
    private static readonly string[] GameUiTweakText = BuildGameUiTweakText(16);

    private static string[] BuildGameUiTweakText(int max)
    {
        var t = new string[max + 1];
        t[0] = "All off";
        for (var n = 1; n <= max; n++) t[n] = n == 1 ? "1 tweak on" : $"{n} tweaks on";
        return t;
    }

    // Shared by the hub card and the window header (same wording + colour in both places).
    private HudElement GameUiStatus() => new TextElement(
        () => GameUiTweakText[Math.Min(GameUiTweaksOn(), GameUiTweakText.Length - 1)],
        Color: () => GameUiTweaksOn() > 0 ? StatusOn : MutedColor(), NoWrap: true);
}
