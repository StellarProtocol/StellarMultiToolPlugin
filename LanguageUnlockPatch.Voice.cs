using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Stellar.MultiTool;

// Voice-language unlock — the audio counterpart of the text unlock. language_setting.txt ships `audio_language:0=0|2=1`
// on JP and SEA alike (only ja voice shown; 0 = Chinese), parsed into showAudioLangs_. Same mechanism as text: at boot
// GetLanguageId(true) validates the saved voice against that list, so we extend it natively in the isAudio prefix via
// AnalysisDefaultAndShowList(cfg, true) — game-allocated, defaultAudioLang_ restored, reverted on mismatch.
// ⚠️ We can NOT check whether a voice bank actually ships (unlike text pools), so the unlock set is the user's configured
// `voice_unlock` list only, filtered to indices that have a voice name. Selected voice is logged to match audio errors.
internal static partial class LanguageUnlockPatch
{
    internal static bool  VoiceEnabled  { get; set; }
    internal static int[] VoiceUnlock   { get; set; } = Array.Empty<int>();          // from config "voice_unlock"
    internal static int[] VoiceUnlocked { get; private set; } = Array.Empty<int>();  // indices WE added this session
    internal static int[] VoiceShown    { get; private set; } = Array.Empty<int>();  // last read-back of showAudioLangs_
    internal static string[] VoiceUnlockedNames { get; private set; } = Array.Empty<string>();   // display names of VoiceUnlocked
    internal static Outcome  VoiceState { get; private set; } = Outcome.Unknown;     // NoData is never set: banks can't be checked
    internal static Action<int>? OnVoiceSet;

    private static bool _voiceNamesLogged;

    private static int InstallVoice(Harmony harmony)
    {
        try
        {
            var m = _mgrType!.GetMethod("SetLanguageVoice", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
            if (m == null) { _log?.Invoke("[Lang] SetLanguageVoice(int) not found"); return 0; }
            harmony.Patch(m, postfix: new HarmonyMethod(typeof(LanguageUnlockPatch), nameof(PostfixSetLanguageVoice)));
            _log?.Invoke("[Lang] SetLanguageVoice postfix patched");
            return 1;
        }
        catch (Exception ex) { _log?.Invoke($"[Lang] SetLanguageVoice patch failed: {ex.Message}"); return 0; }
    }

    // Always logs (diagnostic: match against audio-engine errors in Player.log); records only once the reconcile armed it.
    private static void PostfixSetLanguageVoice(object __instance, int __0)
    {
        try
        {
            _log?.Invoke($"[Lang] voice selected: {__0} \"{VoiceName(__instance, __0) ?? "?"}\"");
            if (RecordArmed) OnVoiceSet?.Invoke(__0);
        }
        catch { }
    }

    internal static void ExposeVoiceOnInstance(object mgr, string where = "instance showAudioLangs_")
    {
        if (!VoiceEnabled) return;
        try
        {
            var show = ReadList(mgr, isAudio: true);
            LogVoiceDiagnostics(mgr, show);
            if (show == null) { VoiceState = Settle(VoiceState, Outcome.CheckFailed); _log?.Invoke($"[Lang] {where}: voice show list is null — skipped"); return; }

            var want  = new SortedSet<int>();
            for (int i = 0; i < show.Length; i++) want.Add(show[i]);
            var added = new List<int>();
            foreach (int i in VoiceUnlock)
            {
                if (string.IsNullOrEmpty(VoiceName(mgr, i))) { _log?.Invoke($"[Lang] {where}: voice {i} has no name — not unlocked"); continue; }
                if (want.Add(i)) added.Add(i);
            }

            if (added.Count == 0)
            {
                VoiceShown = ToArray(show);
                VoiceState = Settle(VoiceState, Outcome.NothingToAdd);
                _log?.Invoke($"[Lang] {where}: no voice to add — voice show list [{Join(show)}]");
                return;
            }

            // No in-place fallback for voice: with only one native entry there is nothing safe to overwrite.
            if (!ReparseNatively(mgr, show, want, added, where, isAudio: true))
            {
                VoiceState = Settle(VoiceState, Outcome.CheckFailed);
                _log?.Invoke($"[Lang] !!! {where}: could not extend the voice show list — nothing exposed");
                return;
            }

            VoiceUnlocked = added.ToArray();
            // Menu text is English, so label from the English AudioLanguageNames field (0 Chinese, 1 English, 2 Japanese,
            // 3 Korean) rather than GetAudioLanNames, which may be client-localized.
            VoiceUnlockedNames = added.ConvertAll(i =>
                ReadProp(mgr, "AudioLanguageNames") is Il2CppStringArray en && i < en.Length && !string.IsNullOrEmpty(en[i])
                    ? en[i] : VoiceName(mgr, i) ?? i.ToString()).ToArray();
            VoiceState = Outcome.Added;
            var back = ReadList(mgr, isAudio: true);
            if (back != null) VoiceShown = ToArray(back);
            _log?.Invoke($"[Lang] {where}: voice unlocked [{string.Join(",", VoiceUnlocked)}] — showAudioLangs_ read-back = " +
                (back == null ? "NULL" : $"[{Join(back)}]"));
        }
        catch (Exception ex)
        {
            VoiceState = Settle(VoiceState, Outcome.CheckFailed);
            _log?.Invoke($"[Lang] voice expose failed: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    internal static int GetCurrentVoice(object mgr)
    {
        try
        {
            var m = _mgrType?.GetMethod("GetCurrentLanguageVoice", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            return m != null ? (int)m.Invoke(mgr, null)! : -1;
        }
        catch { return -1; }
    }

    internal static bool SetVoice(object mgr, int index)
    {
        try
        {
            var m = _mgrType?.GetMethod("SetLanguageVoice", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
            if (m == null) return false;
            m.Invoke(mgr, new object[] { index });
            return true;
        }
        catch (Exception ex) { _log?.Invoke($"[Lang] SetLanguageVoice({index}) failed: {ex.InnerException?.Message ?? ex.Message}"); return false; }
    }

    // The Settings voice row labels entries from GetAudioLanNames() (basemodule_view.initLangVoiceItem) — prefer that,
    // fall back to the raw fields. Which field backs it isn't known, so the diagnostics log both.
    private static string? VoiceName(object mgr, int i)
    {
        if (i < 0) return null;
        string? n = null;
        try
        {
            var m = _mgrType?.GetMethod("GetAudioLanNames", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (m?.Invoke(mgr, null) is Il2CppStringArray a && i < a.Length) n = a[i];
        }
        catch { }
        if (string.IsNullOrEmpty(n) && ReadProp(mgr, "audioLanNames_") is Il2CppStringArray b && i < b.Length) n = b[i];
        if (string.IsNullOrEmpty(n) && ReadProp(mgr, "AudioLanguageNames") is Il2CppStringArray c && i < c.Length) n = c[i];
        return n;
    }

    private static void LogVoiceDiagnostics(object mgr, Il2CppStructArray<int>? show)
    {
        if (_voiceNamesLogged) return;
        _voiceNamesLogged = true;
        var a = ReadProp(mgr, "audioLanNames_") as Il2CppStringArray;
        var b = ReadProp(mgr, "AudioLanguageNames") as Il2CppStringArray;
        var defA = ReadProp(mgr, "defaultAudioLang_");
        _log?.Invoke($"[Lang] voice: shown=[{(show == null ? "NULL" : Join(show))}] default={defA ?? "?"} " +
            $"audioLanNames_={NamesLine(a)} AudioLanguageNames={NamesLine(b)}");
    }

    private static string NamesLine(Il2CppStringArray? a)
    {
        if (a == null) return "NULL";
        var sb = new StringBuilder($"(len {a.Length}) ");
        for (int i = 0; i < a.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            string? n = a[i];
            sb.Append(i).Append(' ').Append(string.IsNullOrEmpty(n) ? "(empty)" : $"\"{n}\"");
        }
        return sb.ToString();
    }
}
