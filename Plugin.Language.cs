using System;
using Stellar.Abstractions.Domain;

namespace Stellar.MultiTool;

// Language unlock — lets a client pick every UI language whose data it ships (e.g. SEA's English on JP) in the game's own
// Settings > Language. Moved here from MahiruUtility (2026-10-29); mechanism, config keys and defaults are unchanged.
// The mechanism lives in LanguageUnlockPatch; this partial holds the config, install wiring, and the startup reconcile.
// Takes effect at the next game start (the show list is read at boot), so config is read only here.
// Config: "lang_unlock" (bool, default on), "lang_exclude" (comma-separated indices to keep hidden, default ""),
// "lang_index" (remembered choice, written by us); voice: "voice_unlock_enabled" (default on), "voice_unlock" (indices,
// default "0" = Chinese), "voice_index" (remembered voice). Voice mechanism: LanguageUnlockPatch.Voice.cs.
// First run: the six keys are imported once from MahiruUtility's config (Plugin.LanguageImport.cs). Window/status:
// Plugin.LanguageWindow.cs.
//
// We don't know whether plugins are constructed before or after LocalizationMgr.InitAsync, so both orders are covered:
//   • early: a GetLanguageId(false) prefix extends showLangs_ just before boot validates the saved index → it is kept.
//   • late:  boot already discarded the saved index (→ ja); we extend the instance's showLangs_ and re-apply it ONCE
//            from our own remembered choice ("lang_index", recorded from every SetLanguage after the reconcile).
public sealed partial class Plugin
{
    private bool _langUnlock;            // live toggle value (what the window shows / config holds)
    private bool _langUnlockAtBoot;      // what this session actually installed with — the toggle only applies on restart
    private bool _voiceUnlock;           // same pair for the voice toggle ("voice_unlock_enabled")
    private bool _voiceUnlockAtBoot;
    private int  _langSaved = -1;
    private int  _voiceSaved = -1;
    private bool _langReconciled;
    private bool _langAppliedPreWorld;   // re-applied before world → flush Lua data caches on the first world tick
    // True while InitLanguageUnlock runs inside the constructor: a late-load reconcile there may record the current
    // language, and that must STAGE (→ the ctor's single _cfgLoadDirty save) rather than save on its own (MultiTool
    // lesson: exactly one save per action, MultiTool-Plugin.md §4.1).
    private bool _langInCtor;

    // Runs in the constructor, BEFORE the hub is built and before the ctor's single config save: the unlock only works
    // if the patch is in place when the game boots its language list.
    private void InitLanguageUnlock()
    {
        _langInCtor = true;
        try { InitLanguageUnlockCore(); }
        finally { _langInCtor = false; }
        _languageWindow = RegisterWindow("multitool.language", "MultiTool: Language Unlock", 6, BuildLanguageRoot());
    }

    private void InitLanguageUnlockCore()
    {
        ImportLanguageSettingsFromMahiru();   // one-time, staged only (Plugin.LanguageImport.cs)

        _langUnlock = _cfg.Get<bool>("lang_unlock", true);
        _langUnlockAtBoot = _langUnlock;
        _langSaved  = _cfg.Get<int>("lang_index", -1);
        _voiceSaved = _cfg.Get<int>("voice_index", -1);
        bool voice  = _voiceUnlock = _voiceUnlockAtBoot = _cfg.Get<bool>("voice_unlock_enabled", true);
        if (!_langUnlock && !voice) { _services.Log.Info("[Lang] text + voice unlock disabled in config — not installed"); return; }

        try
        {
            LanguageUnlockPatch.Exclude       = ParseIndexList(_cfg.Get<string>("lang_exclude", ""), "lang_exclude");
            if (LanguageUnlockPatch.Exclude.Length > 0)
                _services.Log.Info($"[Lang] lang_exclude = [{string.Join(",", LanguageUnlockPatch.Exclude)}]");
            LanguageUnlockPatch.Enabled       = _langUnlock;
            LanguageUnlockPatch.OnLanguageSet = RecordLanguage;
            LanguageUnlockPatch.VoiceEnabled  = voice;
            LanguageUnlockPatch.VoiceUnlock   = voice ? ParseIndexList(_cfg.Get<string>("voice_unlock", "0"), "voice_unlock") : Array.Empty<int>();
            LanguageUnlockPatch.OnVoiceSet    = RecordVoice;
            _services.Log.Info($"[Lang] text unlock={_langUnlock}, voice unlock={voice} voice_unlock=[{string.Join(",", LanguageUnlockPatch.VoiceUnlock)}]");
            // The created Harmony handle auto-unpatches on plugin dispose (owned by IHarmonyHost), so it is not stored.
            if (!LanguageUnlockPatch.Install(_services.Harmony.Create(), _services.Log.Info)) return;

            // IsCreated is checked BEFORE any Instance read — the lazy getter would otherwise hijack the singleton.
            _services.Log.Info($"[Lang] ctor: ZSingleton<LocalizationMgr>.IsCreated = {LanguageUnlockPatch.MgrIsCreated()?.ToString() ?? "unreadable"}");
            var mgr = LanguageUnlockPatch.GetInitializedMgr();
            if (mgr == null)
            {
                _services.Log.Info("[Lang] LocalizationMgr not initialized yet — boot will read the patched show list");
            }
            else
            {
                _services.Log.Info("[Lang] LocalizationMgr already initialized — patching live show list");
                LanguageUnlockPatch.ExposeOnInstance(mgr);
                LanguageUnlockPatch.ExposeVoiceOnInstance(mgr);
                // Pre-world there's no live UI to rebuild, so switch right away via C#: the earliest, least visible point.
                if (_services.ClientState.Phase != GamePhase.World) ReconcileLanguage(mgr, inWorld: false);
            }
        }
        catch (Exception ex) { _services.Log.Warning($"[Lang] init failed: {ex.Message}"); }

        // Framework.Update only ticks in-world (IsWorldActive gate), so this is the first-world-tick fallback/finisher.
        _services.Framework.Update += OnLanguageUpdate;
    }

    private void DisposeLanguageUnlock()
    {
        try { _services.Framework.Update -= OnLanguageUpdate; } catch { }
        // Static hooks outlive the plugin instance — drop them so a disposed plugin is never called back.
        // (Harmony itself auto-unpatches on dispose via IHarmonyHost.)
        LanguageUnlockPatch.OnLanguageSet = null;
        LanguageUnlockPatch.OnVoiceSet    = null;
    }

    private void OnLanguageUpdate(float dt)
    {
        if (_langReconciled && !_langAppliedPreWorld)
        {
            try { _services.Framework.Update -= OnLanguageUpdate; } catch { }
            return;
        }

        try
        {
            if (_langAppliedPreWorld)
            {
                if (!_services.Lua.Ready) return;
                _langAppliedPreWorld = false;
                // Mirrors what the Settings view does after a switch; data models built during login cached ja strings.
                _services.Lua.DoString("pcall(function() Z.DataMgr.OnLanguageChange() end)");
                return;
            }

            var mgr = LanguageUnlockPatch.GetInitializedMgr();
            if (mgr == null) return;
            LanguageUnlockPatch.ExposeOnInstance(mgr);   // idempotent; covers an InitAsync that bypassed the prefix
            LanguageUnlockPatch.ExposeVoiceOnInstance(mgr);
            ReconcileLanguage(mgr, inWorld: true);
        }
        catch (Exception ex)
        {
            _langReconciled = true;
            LanguageUnlockPatch.RecordArmed = true;
            _services.Log.Warning($"[Lang] reconcile failed: {ex.Message}");
        }
    }

    // Runs once per session. Restores the user's last choice only if it is in the (extended) show list and boot didn't
    // keep it. A saved index that isn't listed (excluded since, or its data vanished) is left alone, not overwritten.
    private void ReconcileLanguage(object mgr, bool inWorld)
    {
        if (_langReconciled) return;
        _langReconciled = true;
        try
        {
            int cur   = LanguageUnlockPatch.GetCurrentLanguage(mgr);
            int saved = _langSaved;
            _services.Log.Info($"[Lang] text state={LanguageUnlockPatch.State}, voice state={LanguageUnlockPatch.VoiceState}, unlocked=[{string.Join(",", LanguageUnlockPatch.Unlocked)}], " +
                $"shown=[{string.Join(",", LanguageUnlockPatch.Shown)}]");

            if (saved < 0)
            {
                _services.Log.Info($"[Lang] no saved choice yet, current={cur} — remembering it");
                // Sync our memory to what the game is actually using (e.g. picked at the title screen before the recorder
                // was armed). Safe: a discarded saved choice is never < 0, so it can't be clobbered here.
                if (cur >= 0) RecordLanguage(cur);
            }
            else if (Array.IndexOf(LanguageUnlockPatch.Shown, saved) < 0)
                _services.Log.Warning($"[Lang] saved choice {saved} is not in the show list — no restore (current={cur})");
            else if (cur == saved)
                _services.Log.Info($"[Lang] boot already on saved choice {saved} — kept natively");
            // Lua is only Ready post-login; in world without it, fall through to the plain C# switch below.
            else if (inWorld && _services.Lua.Ready)
            {
                // Same sequence as basemodule_view.setLangItemSelect: switch, flush data caches, rebuild the main view.
                _services.Lua.DoString(
                    "pcall(function() Z.LocalizationMgr:SetLanguage(" + saved + ") end) " +
                    "pcall(function() Z.DataMgr.OnLanguageChange() end) " +
                    "pcall(function() Z.UIMgr:DeActiveAll(false, \"setting\") Z.UIMgr:OpenView(Z.ConstValue.MainViewName) end) " +
                    "pcall(function() Z.EventMgr:Dispatch(Z.ConstValue.LanguageChange) end)");
                _services.Log.Info($"[Lang] re-applied language {saved} in world via Lua (boot had {cur})");
            }
            else
            {
                // Pre-world: no world to notify yet, so skip the ZWorld event; Lua data caches are flushed on the first world tick.
                bool ok = LanguageUnlockPatch.SetLanguage(mgr, saved, fireWorldEvent: inWorld);
                _langAppliedPreWorld = ok && !inWorld;
                _services.Log.Info(ok
                    ? $"[Lang] re-applied language {saved} via SetLanguage (boot had {cur}, inWorld={inWorld})"
                    : $"[Lang] re-apply of language {saved} FAILED (boot had {cur})");
            }
            ReconcileVoice(mgr);
        }
        catch (Exception ex) { _services.Log.Warning($"[Lang] reconcile failed: {ex.Message}"); }
        finally { LanguageUnlockPatch.RecordArmed = true; }
    }

    // Voice counterpart, run inside the same once-per-session reconcile. The game saves its own voice key; once the index
    // is in showAudioLangs_ (extended in the isAudio prefix) GetLanguageId(true) should keep it — so this restores ONLY
    // if the native key didn't survive, and logs which case happened (the open question for the late-load order).
    private void ReconcileVoice(object mgr)
    {
        if (!LanguageUnlockPatch.VoiceEnabled) return;
        int cur   = LanguageUnlockPatch.GetCurrentVoice(mgr);
        int saved = _voiceSaved;
        _services.Log.Info($"[Lang] voice: unlocked=[{string.Join(",", LanguageUnlockPatch.VoiceUnlocked)}], " +
            $"shown=[{string.Join(",", LanguageUnlockPatch.VoiceShown)}], saved={saved}, current={cur}");

        if (saved < 0) { if (cur >= 0) RecordVoice(cur); }
        else if (Array.IndexOf(LanguageUnlockPatch.VoiceShown, saved) < 0)
            _services.Log.Warning($"[Lang] saved voice {saved} is not in the voice show list — no restore");
        else if (cur == saved)
            _services.Log.Info($"[Lang] voice: native saved key survived (boot kept {saved}) — no restore needed");
        else
        {
            bool ok = LanguageUnlockPatch.SetVoice(mgr, saved);
            _services.Log.Info(ok
                ? $"[Lang] voice: native key did NOT survive (boot had {cur}) — restored {saved} via SetLanguageVoice"
                : $"[Lang] voice: restore of {saved} FAILED (boot had {cur})");
        }
    }

    // SetLanguageVoice postfix (armed after the reconcile) — remembers the user's voice choice for the next boot.
    private void RecordVoice(int index)
    {
        if (index == _voiceSaved) return;
        _voiceSaved = index;
        _cfg.Set<int>("voice_index", index);
        SaveLanguageConfig();
        _services.Log.Info($"[Lang] remembered voice choice {index}");
    }

    // Postfix on SetLanguage (armed after the reconcile) — remembers the user's in-game choice for the next boot.
    private void RecordLanguage(int index)
    {
        if (index == _langSaved) return;
        _langSaved = index;
        _cfg.Set<int>("lang_index", index);
        SaveLanguageConfig();
        _services.Log.Info($"[Lang] remembered language choice {index}");
    }

    // Outside the constructor: one save per recorded choice, as before. Inside it: stage for the ctor's single save.
    private void SaveLanguageConfig()
    {
        if (_langInCtor) _cfgLoadDirty = true;
        else _cfg.Save();
    }

    // "4, 7,12" → [4,7,12]; junk entries are skipped (logged), so a typo can't disable the whole unlock.
    private int[] ParseIndexList(string? csv, string key)
    {
        var list = new System.Collections.Generic.List<int>();
        foreach (var part in (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out int i) && i >= 0) { if (!list.Contains(i)) list.Add(i); }
            else _services.Log.Warning($"[Lang] {key}: ignoring \"{part}\"");
        }
        return list.ToArray();
    }
}
