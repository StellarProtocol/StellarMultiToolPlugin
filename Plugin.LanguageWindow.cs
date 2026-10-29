using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// UI for the Language Unlock (mechanism: Plugin.Language.cs + LanguageUnlockPatch). House style (Plugin.Style.cs):
// header (icon + title + the hub card's live status chips), a muted description, then one panel per list with its
// toggle and a live status line. Both show lists are read once at boot, so a toggle can't act live; the status says
// "restart" until the game is restarted.
public sealed partial class Plugin
{
    private IWindowControl _languageWindow = null!;

    // Semantic (fixed) colours, like the Party/Social/Join chips: chips = text blue / voice violet; status lines = ok
    // green (StatusOn), info blue, error red. "Off" text is MutedColor (theme-live).
    private static readonly ColorRgba ChipText   = Rgb(0x9cc8ff);
    private static readonly ColorRgba ChipVoice  = Rgb(0xb385ff);
    private static readonly ColorRgba LangInfo   = Rgb(0x9cc8ff);
    private static readonly ColorRgba LangErr    = Rgb(0xff4d4f);

    private static byte[]? LanguageIconPng() => CachedPng("icon-language.png");

    private bool LangRestartPending => _langUnlock != _langUnlockAtBoot || _voiceUnlock != _voiceUnlockAtBoot;

    private HudElement BuildLanguageRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader(LanguageIconPng, "Language Unlock", LanguageStatus()),
        new TextElement(() => "Shows every language this client ships in the game's Settings > Language. Changes apply after restarting the game.",
            Color: () => (ColorRgba?)MutedColor()),

        SectionPanel(() => "Text languages", null,
            // Same toggle-row builder as Auto-Accept (Plugin.InviteAccept.cs), so the rows line up across tools.
            InviteToggleRow("Unlock all text languages", () => _langUnlock, OnLangUnlockToggle),
            new TextElement(() => TextLangStatus().Text, Color: () => (ColorRgba?)TextLangStatus().Color)),

        SectionPanel(() => "Voice languages", null,
            InviteToggleRow("Unlock extra voice languages", () => _voiceUnlock, OnVoiceUnlockToggle),
            new TextElement(() => VoiceLangStatus().Text, Color: () => (ColorRgba?)VoiceLangStatus().Color)),
    }, Gap: 8f);

    // Live status, shared by the hub card and this window's header (one builder → they can't drift): a chip per list,
    // lit when that unlock actually added languages THIS session, then the count or a restart hint.
    private HudElement LanguageStatus() => new RowElement(new HudElement[]
    {
        StatusChip("Text",  ChipText,  () => _langUnlockAtBoot  && LanguageUnlockPatch.State      == LanguageUnlockPatch.Outcome.Added),
        StatusChip("Voice", ChipVoice, () => _voiceUnlockAtBoot && LanguageUnlockPatch.VoiceState == LanguageUnlockPatch.Outcome.Added),
        new TextElement(LanguageStatusTail, Color: () => MutedColor(), NoWrap: true),
    }, Gap: 4f);

    private string LanguageStatusTail()
    {
        if (LangRestartPending) return "· restart to apply";
        // Shown = the game's final show list (native ∪ ours, lang_exclude already applied) — the real Settings count.
        if (_langUnlockAtBoot && LanguageUnlockPatch.State == LanguageUnlockPatch.Outcome.Added)
            return $"· {LanguageUnlockPatch.Shown.Length} languages";
        return "";
    }

    private (string Text, ColorRgba Color) TextLangStatus()
    {
        if (_langUnlock != _langUnlockAtBoot) return ("Takes effect after restarting the game", LangInfo);
        if (!_langUnlockAtBoot)               return ("Off. The game's own language list.", MutedColor());
        return LanguageUnlockPatch.State switch
        {
            LanguageUnlockPatch.Outcome.Added        => ($"{LanguageUnlockPatch.Shown.Length} languages available. Choose in Settings > Language.", StatusOn),
            LanguageUnlockPatch.Outcome.NothingToAdd => ("All languages already available on this client", LangInfo),
            LanguageUnlockPatch.Outcome.NoData       => ("No extra language data on this client", LangErr),
            // CheckFailed, and Unknown (patch install failed / show list never read). Detail is in our log.
            _                                        => ("Couldn't read this client's language data.", LangErr),
        };
    }

    private (string Text, ColorRgba Color) VoiceLangStatus()
    {
        if (_voiceUnlock != _voiceUnlockAtBoot) return ("Takes effect after restarting the game", LangInfo);
        if (!_voiceUnlockAtBoot)                return ("Off. The game's own voice list.", MutedColor());
        return LanguageUnlockPatch.VoiceState switch
        {
            LanguageUnlockPatch.Outcome.Added        => ($"{string.Join(", ", LanguageUnlockPatch.VoiceUnlockedNames)} voice added. Choose in Settings > Language.", StatusOn),
            LanguageUnlockPatch.Outcome.NothingToAdd => ("No extra voice on this client", LangInfo),
            _                                        => ("Couldn't read this client's voice data.", LangErr),
        };
    }

    // One save per click (MultiTool-Plugin.md §4.1).
    private void OnLangUnlockToggle(bool on)
    {
        _langUnlock = on;
        _cfg.Set<bool>("lang_unlock", on);
        _cfg.Save();
    }

    private void OnVoiceUnlockToggle(bool on)
    {
        _voiceUnlock = on;
        _cfg.Set<bool>("voice_unlock_enabled", on);
        _cfg.Save();
    }
}
