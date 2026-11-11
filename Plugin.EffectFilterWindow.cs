using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// UI for the Performance tool (was "Effect Filter"; effect state + rule: Plugin.EffectFilter.cs / EffectFilterPatch.cs;
// party list engine: Plugin.PartyThrottle.cs). Built to the approved prototype (option A, user-approved 2026-11-11).
// ToolHeader with the live status, then TWO COLUMNS:
//   left  — HIDE (seven On/Off rows, On = hidden: "Other players' effects" with three indented "…their" sub-rows, then
//           mine, boss & monster, telegraphs), exactly as the Effect Filter had it;
//   right — PARTY LIST ("Reduce party list updates" + an indented "Update rate" 5 / 10 per sec pair + a muted foot), WHERE
//           (Everywhere / Dungeons & raids — shared by BOTH features) and the muted effects footer.
// Every setting row = [label … control  ?]. Sub-rows dim while their parent toggle is off (they only refine it); WHERE
// dims while neither feature is on. The window id stays `multitool.effectfilter` so saved positions survive.
public sealed partial class Plugin
{
    private IWindowControl _effectWindow = null!;

    // Left column = the old 420 px window's content width (420 − 2 × 12 body padding), so the HIDE rows lay out exactly as
    // before: the in-game plain text is a fixed 14 px and "Telegraphs & important markers" must fit beside the 64 px chip
    // and the "?" without wrapping. Right column = room for the "Update rate" row: 16 indent + label + the two 28 px
    // pills + "?" (rule 18: widen, don't cram). Window = 12 + left + 8 + right + 12.
    private const float PerfLeftColW  = 396f;
    private const float PerfRightColW = 332f;
    private const float PerfColGap    = 8f;
    private const float EffWindowWidth = 24f + PerfLeftColW + PerfColGap + PerfRightColW;   // 760
    private const float EffRowHeight   = 28f;
    private const float EffSubIndent   = 16f;   // sub-row indent (the row Gap adds the usual 8 after it)

    private static byte[]? PerformanceIconPng() => CachedPng("icon-performance.png");

    private bool EffDim() => !EffAnyHide && !_ptOn;   // WHERE: neither feature is on
    private bool EffSubDim() => !_effOn;
    private bool PtSubDim() => !_ptOn;

    private HudElement BuildEffectFilterRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader(PerformanceIconPng, "Performance", PerformanceWindowStatus()),
        new RowElement(new HudElement[]
        {
            PerfColumn(PerfLeftColW, BuildEffHidePanel()),
            PerfColumn(PerfRightColW,
                BuildPartyListPanel(),
                SectionPanel(() => "Where", () => "both features",
                    new RowElement(new HudElement[]
                    {
                        ChoicePill("Everywhere",       () => _effEverywhere,  () => SetEffEverywhere(true),  EffDim),
                        ChoicePill("Dungeons & raids", () => !_effEverywhere, () => SetEffEverywhere(false), EffDim),
                    }, Gap: 6f)),
                new TextElement(() => "Hidden effects are skipped before they load (saves FPS) and are silent.",
                    Color: () => MutedColor())),
        }, Gap: PerfColGap),
    }, Gap: 8f);

    // A fixed-width column (Maestro Plugin.Settings.cs SettingsColumn): the always-true Fill Conditional is the one
    // element that reports flexibleHeight, so the Row grows both cells to the row height and the shorter column sits at
    // the TOP instead of being centred by the row's MiddleLeft alignment.
    private static HudElement PerfColumn(float width, params HudElement[] items) => new CellElement(
        new ConditionalElement(() => true, new ColumnElement(items, Gap: 8f), Fill: true),
        Width: width);

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

    // PARTY LIST: the toggle row, the indented rate sub-row (two segmented pills, dimmed while the toggle is off, like the
    // effect sub-rows) and a muted foot whose delay follows the chosen rate (same length either way → nothing moves).
    private HudElement BuildPartyListPanel() => SectionPanel(() => "Party list", () => "fewer redraws",
        EffRow("Reduce party list updates",
            ToggleChip(() => _ptOn, TogglePartyThrottle),
            HelpDot("pt.reduce", "Party list", "Reduce party list updates",
                "Redraws the party list's HP bars and buff icons at most a few times per second instead of on every " +
                "change. Saves a little CPU in big groups like raids. Buff and debuff icons still show, just up to a " +
                "moment later.")),
        EffRow("Update rate",
            new RowElement(new HudElement[]
            {
                ChoicePill("5 / sec",  () => _ptRate == 5,  () => SetPartyRate(5),  PtSubDim),
                ChoicePill("10 / sec", () => _ptRate != 5, () => SetPartyRate(10), PtSubDim),
            }, Gap: 6f),
            HelpDot("pt.rate", "Party list", "Update rate",
                "10 / sec: almost no visible delay. 5 / sec: saves a bit more, HP bars may lag up to 0.2 s."),
            PtSubDim),
        new TextElement(() => _ptRate == 5
                ? "HP bars may lag up to 0.2 s. Buff icons still show."
                : "HP bars may lag up to 0.1 s. Buff icons still show.",
            Color: () => MutedColor()));

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

    // Window header + hub card status: ONE text source each (Plugin.EffectFilter.cs "status text"). Green only while a
    // feature is actually working (header) / switched on (hub card, like the other cards).
    private HudElement PerformanceWindowStatus() => new TextElement(PerfWindowStatusText,
        Color: () => EffFiltering || PtShowsActive ? StatusOn : MutedColor(), NoWrap: true);

    private HudElement PerformanceHubStatus() => new TextElement(PerfHubStatusText,
        Color: () => EffAnyHide || _ptOn ? StatusOn : MutedColor(), NoWrap: true);
}
