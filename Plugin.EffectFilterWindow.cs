using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// UI for the Effect Filter (state + rule: Plugin.EffectFilter.cs / EffectFilterPatch.cs). Built to the approved
// prototype (2026-11-11), then reworked twice the same day by user decision — the latest: EVERY switch means HIDE.
// ToolHeader with the live status, then two panels — HIDE (seven On/Off rows, On = hidden: "Other players' effects"
// with three indented "…their" sub-rows, then mine, boss & monster, telegraphs) and WHERE (Everywhere / Dungeons &
// raids pills) — and a muted footer. Every setting row = [label … chip  ?]. The sub-rows dim while "Other players'
// effects" is off (they only refine what it hides); WHERE dims while no setting can hide.
public sealed partial class Plugin
{
    private IWindowControl _effectWindow = null!;

    // Wider than the 360 px mock: the in-game plain text is a fixed 14 px (the mock used 13 px), and the longest label
    // ("Telegraphs & important markers") must fit beside the 64 px chip and the "?" without wrapping.
    private const float EffWindowWidth = 420f;
    private const float EffRowHeight   = 28f;
    private const float EffSubIndent   = 16f;   // sub-row indent (the row Gap adds the usual 8 after it)

    private static byte[]? EffectFilterIconPng() => CachedPng("icon-effectfilter.png");

    private bool EffDim() => !EffAnyHide;
    private bool EffSubDim() => !_effOn;

    private HudElement BuildEffectFilterRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader(EffectFilterIconPng, "Effect Filter", EffectFilterWindowStatus()),
        BuildEffHidePanel(),
        SectionPanel(() => "Where", null,
            new RowElement(new HudElement[]
            {
                ChoicePill("Everywhere",       () => _effEverywhere,  () => SetEffEverywhere(true),  EffDim),
                ChoicePill("Dungeons & raids", () => !_effEverywhere, () => SetEffEverywhere(false), EffDim),
            }, Gap: 6f)),
        new TextElement(() => "Hidden effects are skipped before they load (saves FPS) and are silent.",
            Color: () => MutedColor()),
    }, Gap: 8f);

    // Rows are checked top to bottom in EffectFilterPatch.Rule, except telegraphs, which are checked FIRST: a marker
    // follows only its own row, whoever cast it.
    private HudElement BuildEffHidePanel() => SectionPanel(() => "Hide", () => "On = hidden",
        EffRow("Other players' effects",
            ToggleChip(() => _effOn, ToggleEffMaster),
            HelpDot("eff.others", "Hide", "Other players' effects",
                "Hides the skill effects other players cast, so busy fights run smoother. It goes by who cast the " +
                "skill: a boss attack on another player is a boss effect, not theirs. The three rows below fine-tune it.")),
        EffRow("…their pets & summons",
            ToggleChip(() => _effPets, ToggleEffPets, EffSubDim),
            HelpDot("eff.pets", "Hide", "…their pets & summons",
                "On: also hides effects from other players' pets, summons and the projectiles they fire. " +
                "Off: those stay visible. Only matters while 'Other players' effects' is on."),
            EffSubDim),
        EffRow("…their heals & buffs",
            ToggleChip(() => _effOthersBuffs, ToggleEffOthersBuffs, EffSubDim),
            HelpDot("eff.buff", "Hide", "…their heals & buffs",
                "Off: other players' healing circles and support zones stay visible, so you can see where to stand. " +
                "On: they are hidden too. Only matters while 'Other players' effects' is on."),
            EffSubDim),
        EffRow("…their effects on me",
            ToggleChip(() => _effOthersOnMe, ToggleEffOthersOnMe, EffSubDim),
            HelpDot("eff.onme", "Hide", "…their effects on me",
                "Off: effects other players put on you, such as a heal or a shield, stay visible. On: they are " +
                "hidden too. Only matters while 'Other players' effects' is on."),
            EffSubDim),
        EffRow("My own effects",
            ToggleChip(() => _effHideMine, ToggleEffHideMine),
            HelpDot("eff.mine", "Hide", "My own effects",
                "Hides your own skill visuals and their sounds, including those of your pets and summons and what " +
                "you cast on yourself. Aiming circles and other markers follow 'Telegraphs & important markers'.")),
        EffRow("Boss & monster effects",
            ToggleChip(() => _effHideMonsters, ToggleEffHideMonsters),
            HelpDot("eff.monster", "Hide", "Boss & monster effects",
                "Hides the boss's and monsters' own skill visuals, including their attacks on you. Their warnings " +
                "follow 'Telegraphs & important markers'.")),
        EffRow("Telegraphs & important markers",
            // On hides boss warnings → the on chip uses the Caution (amber) look so the risk stays visible.
            ToggleChip(() => _effHideMarkers, ToggleEffHideMarkers, cautionOn: true),
            HelpDot("eff.markers", "Hide", "Telegraphs & important markers",
                "Boss attack warnings, danger zones, aiming circles and other markers the game flags as important. " +
                "Careful: turning this on hides boss attack warnings — raids may become unplayable.")));

    // [indent? | label (fills, ellipsizes) | control | ?], pinned to the prototype's 28 px row by the leading strut
    // (width 0 = a non-flexible height strut; a sub-row's strut is also its indent). Rows align MiddleLeft, so the 24 px
    // chip and "?" stay vertically centred. `dim` greys the label like its chip (a setting with no effect right now).
    private HudElement EffRow(string label, HudElement control, HudElement help, Func<bool>? dim = null) => new RowElement(
        new HudElement[]
        {
            new SpacerElement(dim != null ? EffSubIndent : 0f, EffRowHeight),
            new CellElement(new TextElement(() => label, Color: () => Dimmed(TextColor(), dim), NoWrap: true)
                { Ellipsize = true }, Weight: 1f),
            control,
            help,
        }, Gap: 8f);

    // Window header status: live count while filtering, else why nothing is hidden. Green only while it is filtering.
    private HudElement EffectFilterWindowStatus() => new TextElement(EffWindowStatusText,
        Color: () => EffFiltering ? StatusOn : MutedColor(), NoWrap: true);

    // Hub card status: on/off + the chosen scope (like the other cards: the whole line green while on).
    private HudElement EffectFilterHubStatus() => new TextElement(EffHubStatusText,
        Color: () => EffAnyHide ? StatusOn : MutedColor(), NoWrap: true);
}
