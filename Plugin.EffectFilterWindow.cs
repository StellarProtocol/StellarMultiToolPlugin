using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// UI for the Effect Filter (state + rule: Plugin.EffectFilter.cs / EffectFilterPatch.cs). Built to the approved
// prototype (2026-11-11), then reworked the same day (user decision: every row is a toggle, nothing is locked):
// ToolHeader with the live status, then three panels — HIDE (the master "Other players' skill effects" + pets &
// summons), SHOW (five On/Off rows, On = shown), WHERE (Everywhere / Dungeons & raids pills) — and a muted footer.
// Every setting row = [label … chip  ?]. Rows are independent, so none dims; only WHERE dims while no setting can hide.
public sealed partial class Plugin
{
    private IWindowControl _effectWindow = null!;

    // Wider than the 360 px mock: the in-game plain text is a fixed 14 px (the mock used 13 px), and the longest label
    // ("Telegraphs & important markers") must fit beside the 64 px chip and the "?" without wrapping.
    private const float EffWindowWidth = 420f;
    private const float EffRowHeight   = 28f;

    private static byte[]? EffectFilterIconPng() => CachedPng("icon-effectfilter.png");

    private bool EffDim() => !EffAnyHide;

    private HudElement BuildEffectFilterRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader(EffectFilterIconPng, "Effect Filter", EffectFilterWindowStatus()),
        BuildEffHidePanel(),
        BuildEffShowPanel(),
        SectionPanel(() => "Where", null,
            new RowElement(new HudElement[]
            {
                ChoicePill("Everywhere",       () => _effEverywhere,  () => SetEffEverywhere(true),  EffDim),
                ChoicePill("Dungeons & raids", () => !_effEverywhere, () => SetEffEverywhere(false), EffDim),
            }, Gap: 6f)),
        new TextElement(() => "Hidden effects are skipped before they load (saves FPS) and are silent.",
            Color: () => MutedColor()),
    }, Gap: 8f);

    private HudElement BuildEffHidePanel() => SectionPanel(() => "Hide", () => "caster decides, not target",
        EffRow("Other players' skill effects",
            ToggleChip(() => _effOn, ToggleEffMaster),
            HelpDot("eff.master", "Hide", "Other players' skill effects",
                "Hides the skill effects that other players cast, so busy fights run smoother. The caster decides, " +
                "not the target: a boss attack on another player still shows.")),
        EffRow("Their pets & summons",
            ToggleChip(() => _effPets, ToggleEffPets),
            HelpDot("eff.pets", "Hide", "Their pets & summons",
                "Also hides effects from other players' pets, summons and the projectiles they fire, while " +
                "'Other players' skill effects' is on. Your own pets and summons follow 'My own effects'.")));

    // Each row is On = shown. Rows are checked top to bottom (EffectFilterPatch.Rule): telegraphs first, then "Effects
    // that land on me" and "Heal & buff zones" keep an effect even when its caster's row would hide it.
    private HudElement BuildEffShowPanel() => SectionPanel(() => "Show", () => "turn off to hide",
        EffRow("Telegraphs & important markers",
            // Off hides boss warnings → the off chip uses the Caution (amber) look so the risk stays visible.
            ToggleChip(() => _effShowMarkers, ToggleEffShowMarkers, cautionOff: true),
            HelpDot("eff.markers", "Show", "Telegraphs & important markers",
                "Boss attack warnings, danger zones, aiming circles and other markers the game flags as important. " +
                "Turning this off can hide boss attack warnings — raids may become unplayable.")),
        EffRow("Boss & monster effects",
            ToggleChip(() => _effShowMonsters, ToggleEffShowMonsters),
            HelpDot("eff.monster", "Show", "Boss & monster effects",
                "Hides the boss's and monsters' own skill visuals. Their telegraphs stay while " +
                "'Telegraphs & important markers' is on.")),
        EffRow("My own effects",
            ToggleChip(() => _effShowMine, ToggleEffShowMine),
            HelpDot("eff.mine", "Show", "My own effects",
                "Hides your own skill visuals and their sounds. Aiming circles and other important markers stay " +
                "while that row is on.")),
        EffRow("Effects that land on me",
            ToggleChip(() => _effKeepOnMe, ToggleEffKeepOnMe),
            HelpDot("eff.onme", "Show", "Effects that land on me",
                "Keeps an effect that lands on you, such as a heal or a shield, even when another row would hide it. " +
                "Turn this off to let the other rows decide.")),
        EffRow("Heal & buff zones",
            ToggleChip(() => _effKeepBuffs, ToggleEffKeepBuffs),
            HelpDot("eff.buff", "Show", "Heal & buff zones",
                "Keeps healing circles and support zones, so you can see where to stand, even when another row " +
                "would hide them. Turn this off to let the other rows decide.")));

    // [label (fills, ellipsizes) | control | ?], pinned to the prototype's 28 px row by a zero-width strut. Rows align
    // MiddleLeft, so the 24 px chip and "?" stay vertically centred.
    private HudElement EffRow(string label, HudElement control, HudElement help) => new RowElement(
        new HudElement[]
        {
            new SpacerElement(0f, EffRowHeight),
            new CellElement(new TextElement(() => label, Color: () => TextColor(), NoWrap: true)
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
