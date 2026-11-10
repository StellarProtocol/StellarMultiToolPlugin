using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// UI for the Effect Filter (state + rule: Plugin.EffectFilter.cs / EffectFilterPatch.cs). Built to the approved
// prototype (2026-11-11): ToolHeader with the live status, then three panels — HIDE (the master "Other players' skill
// effects" + pets & summons), ALWAYS SHOW (two locked "Always" rows + two keep toggles), WHERE (Everywhere / Dungeons &
// raids pills) — and a muted footer. Every setting row = [label … chip  ?]. While the master is off every row below it
// dims (still visible and clickable).
public sealed partial class Plugin
{
    private IWindowControl _effectWindow = null!;

    // Wider than the 360 px mock: the in-game plain text is a fixed 14 px (the mock used 13 px), and the longest label
    // ("Boss & monster effects, telegraphs") must fit beside the 64 px chip and the "?" without wrapping.
    private const float EffWindowWidth = 420f;
    private const float EffRowHeight   = 28f;

    private static byte[]? EffectFilterIconPng() => CachedPng("icon-effectfilter.png");

    private bool EffDim() => !_effOn;

    private HudElement BuildEffectFilterRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader(EffectFilterIconPng, "Effect Filter", EffectFilterWindowStatus()),
        BuildEffHidePanel(),
        BuildEffAlwaysPanel(),
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
        EffRow("Other players' skill effects", null,
            ToggleChip(() => _effOn, ToggleEffMaster),
            HelpDot("eff.master", "Hide", "Other players' skill effects",
                "Hides the skill effects that other players cast, so busy fights run smoother. Boss attacks, " +
                "telegraphs and anything a monster puts on a player always stay. The caster decides, not the target.")),
        EffRow("Their pets & summons", EffDim,
            ToggleChip(() => _effPets, ToggleEffPets, EffDim),
            HelpDot("eff.pets", "Hide", "Their pets & summons",
                "Also hides effects from other players' pets, summons and the projectiles they fire. " +
                "Your own pets and summons are never hidden.")));

    private HudElement BuildEffAlwaysPanel() => SectionPanel(() => "Always show", () => "raid mechanics stay",
        EffRow("Boss & monster effects, telegraphs", EffDim,
            LockedChip("Always", EffDim),
            HelpDot("eff.monster", "Always show", "Boss & monster effects, telegraphs",
                "Effects from bosses, monsters and the area itself are never hidden. Every raid mechanic and danger " +
                "zone stays visible, even when it lands on another player.")),
        EffRow("My own effects", EffDim,
            LockedChip("Always", EffDim),
            HelpDot("eff.mine", "Always show", "My own effects",
                "Everything you cast is always shown, including your pets and summons.")),
        EffRow("Effects that land on me", EffDim,
            ToggleChip(() => _effKeepOnMe, ToggleEffKeepOnMe, EffDim),
            HelpDot("eff.onme", "Always show", "Effects that land on me",
                "Keeps another player's effect when it lands on you, such as a heal or a shield. " +
                "Turn this off to hide those too.")),
        EffRow("Heal & buff zones", EffDim,
            ToggleChip(() => _effKeepBuffs, ToggleEffKeepBuffs, EffDim),
            HelpDot("eff.buff", "Always show", "Heal & buff zones",
                "Keeps the healing circles and support zones other players place, so you can see where to stand. " +
                "Turn this off to hide them too.")));

    // [label (fills, ellipsizes) | control | ?], pinned to the prototype's 28 px row by a zero-width strut. Rows align
    // MiddleLeft, so the 24 px chip and "?" stay vertically centred.
    private HudElement EffRow(string label, Func<bool>? dim, HudElement control, HudElement help) => new RowElement(
        new HudElement[]
        {
            new SpacerElement(0f, EffRowHeight),
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
        Color: () => _effOn ? StatusOn : MutedColor(), NoWrap: true);
}
