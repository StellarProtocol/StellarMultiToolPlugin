using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// Language unlock — exposes every UI language whose string pool ships on this client (lanTypes_[i] + ".bytes" exists),
// minus the user's `lang_exclude`, on clients whose language_setting.txt lists fewer (JP shows only ja/ko). The headline
// case is SEA's English (LanguageType.en_TH = 11, englishsea.bytes). The game's language list is pure data: static
// LocalizationMgr.GetLanguageSetting parses the shipped file into showLangs_, and at boot GetLanguageId(false) DISCARDS a
// saved index that isn't in that list — so a bare SetLanguage(i) would reset to ja every launch. Putting the index into
// the show list fixes both the boot discard and the Settings row (which only appears with >1 entry). See
// Knowledge Base\Localization-Language-Selection.md.
internal static partial class LanguageUnlockPatch
{
    internal const int    EnglishSeaIndex = 11;                  // LanguageType.en_TH — target of the in-place swap fallback
    private  const string MgrTypeName     = "Panda.Utility.Localization.LocalizationMgr";
    private  const string ResLoaderName   = "ZResource.Loader.ZResLoader";

    // Outcome of an unlock pass (text: State, voice: VoiceState) — what the menu's status lines show.
    //   Added        = we extended the list this session (sticky: later idempotent passes find nothing new, that's fine)
    //   NothingToAdd = the client already lists everything we could add (or the rest is in lang_exclude)
    //   NoData       = there are hidden indices, but none of them has a pool file
    //   CheckFailed  = list/pool read failed or the extend didn't stick; Unknown = never ran (patch not installed)
    internal enum Outcome { Unknown, Added, NothingToAdd, NoData, CheckFailed }

    internal static bool    Enabled { get; set; }
    internal static Outcome State   { get; private set; } = Outcome.Unknown;

    // Never downgrade an Added — the idempotent re-runs (first world tick) see the extended list and add nothing.
    private static Outcome Settle(Outcome cur, Outcome next) => cur == Outcome.Added ? cur : next;

    internal static int[] Exclude  { get; set; } = Array.Empty<int>();          // from config "lang_exclude"
    internal static int[] Unlocked { get; private set; } = Array.Empty<int>();  // indices WE added this session (future UI)
    internal static int[] Shown    { get; private set; } = Array.Empty<int>();  // last read-back of showLangs_ (native ∪ ours)

    // Set by the plugin once its startup reconcile is done. Until then the game's own boot-time language calls (e.g. a
    // fallback to ja because the saved 11 was discarded) must NOT overwrite the user's remembered choice.
    internal static bool         RecordArmed { get; set; }
    internal static Action<int>? OnLanguageSet;

    private static Action<string>? _log;
    private static Type?   _mgrType;
    private static bool?[]? _pools;       // per-index pool-file existence; cached once the loader was available

    internal static bool Install(Harmony harmony, Action<string> log)
    {
        _log = log;
        _mgrType = StellarInterop.FindType(MgrTypeName);
        if (_mgrType == null) { log("[Lang] LocalizationMgr type not found — unlock inactive"); return false; }

        int patched = 0;
        try
        {
            // Private in the game, public in interop. Matched by param TYPE (bool) — the only GetLanguageId overload.
            var gli = _mgrType.GetMethod("GetLanguageId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(bool) }, null);
            if (gli != null)
            {
                harmony.Patch(gli, prefix: new HarmonyMethod(typeof(LanguageUnlockPatch), nameof(PrefixGetLanguageId)));
                log("[Lang] GetLanguageId prefix patched");
                patched++;
            }
            else log("[Lang] GetLanguageId(bool) not found");
        }
        catch (Exception ex) { log($"[Lang] GetLanguageId patch failed: {ex.Message}"); }

        try
        {
            // Matched by param TYPES — SetLanguageVoice(int) is a neighbour with a similar name.
            var sl = _mgrType.GetMethod("SetLanguage", BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(int), typeof(bool) }, null);
            if (sl != null)
            {
                harmony.Patch(sl, postfix: new HarmonyMethod(typeof(LanguageUnlockPatch), nameof(PostfixSetLanguage)));
                log("[Lang] SetLanguage postfix patched");
                patched++;
            }
            else log("[Lang] SetLanguage(int,bool) not found");
        }
        catch (Exception ex) { log($"[Lang] SetLanguage patch failed: {ex.Message}"); }

        patched += InstallVoice(harmony);   // SetLanguageVoice postfix (LanguageUnlockPatch.Voice.cs)

        return patched > 0;
    }

    // InitAsync stores showLangs_ and THEN calls GetLanguageId(false), which validates the saved index against it — so
    // this is the last point to extend it before a saved unlocked choice would be discarded. How it's extended
    // (game-allocated only) is documented on ExposeOnInstance.
    private static void PrefixGetLanguageId(object __instance, bool __0)
    {
        if (__0) { if (VoiceEnabled) ExposeVoiceOnInstance(__instance, "GetLanguageId(audio)"); return; }   // isAudio → voice list
        if (!Enabled) return;
        ExposeOnInstance(__instance, "GetLanguageId");
    }

    private static void PostfixSetLanguage(int __0)
    {
        if (!RecordArmed) return;
        try { OnLanguageSet?.Invoke(__0); } catch { }
    }

    // ── Live-instance access (late-load path: the plugin was constructed AFTER LocalizationMgr.InitAsync) ──

    // ⚠️ ZSingleton<T>.Instance lazily creates — calling it before the game's own init hijacks the singleton. Seen in-game
    // on JP: GetSingleton(LocalizationMgr) from the plugin ctor (before InitAsync) created a bare `new LocalizationMgr()`
    // that became Instance; the game initialized a different object (the one our GetLanguageId prefix saw), so Lua's
    // Z.LocalizationMgr:GetShowLans() returned nil and Settings threw at basemodule_view.lua:52 — however the show list
    // was written. So EVERY singleton read here first checks the static ZSingleton<T>.IsCreated, and treats
    // "false / unreadable" as "not there yet" without touching Instance.
    internal static bool? IsCreated(Type? t)
    {
        try
        {
            var p = t?.GetProperty("IsCreated", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            return p?.GetValue(null) is bool b ? b : null;
        }
        catch { return null; }
    }

    internal static bool? MgrIsCreated() => IsCreated(_mgrType);

    // GetSingleton only when IsCreated is definitely true — never the lazily-creating path.
    private static object? GetCreatedSingleton(Type? t) =>
        t != null && IsCreated(t) == true ? StellarInterop.GetSingleton(t) : null;

    // Returns the singleton only once it exists AND InitAsync has populated it — lanTypes_ is null on a bare instance.
    internal static object? GetInitializedMgr()
    {
        try
        {
            if (_mgrType == null) return null;
            var mgr = GetCreatedSingleton(_mgrType);
            if (mgr == null) return null;
            var types = ReadProp(mgr, "lanTypes_") as Il2CppStringArray;
            return types is { Length: > 0 } ? mgr : null;
        }
        catch { return null; }
    }

    // Adds every unlockable language (pool file present, not in lang_exclude) to the instance's show list.
    // ⚠️ NEVER assign a managed-allocated array into a game field. Two approaches were tried on JP before the real cause (the
    // singleton hijack above) was found: (a) replacing the `out showLangs` array in a GetLanguageSetting postfix,
    // (b) a `new Il2CppStructArray<int>` via the interop property setter. Both are unnecessary risk now, so the GAME
    // allocates: re-run its own parser with an extended cfg; if that's unavailable, swap one entry in place. Idempotent.
    internal static void ExposeOnInstance(object mgr, string where = "instance showLangs_")
    {
        if (!Enabled) return;
        try
        {
            var show  = ReadShow(mgr);
            var types = ReadProp(mgr, "lanTypes_") as Il2CppStringArray;
            var names = ReadProp(mgr, "lanNames_") as Il2CppStringArray;
            if (show == null)  { State = Settle(State, Outcome.CheckFailed); _log?.Invoke($"[Lang] {where}: show list is null — skipped"); return; }
            if (types == null || types.Length == 0) { State = Settle(State, Outcome.CheckFailed); _log?.Invoke($"[Lang] {where}: lanTypes_ empty — skipped"); return; }

            var pools = ScanPools(types, names);
            if (pools == null)
            {
                State = Settle(State, Outcome.CheckFailed);
                _log?.Invoke($"[Lang] !!! {where}: pool-file check deferred (ZResLoader not created / check failed) — nothing exposed yet, retried later");
                return;
            }

            // Unlock set = pool file present AND not excluded. Only ever ADDS — a natively shown language stays shown.
            var want = new SortedSet<int>();
            for (int i = 0; i < show.Length; i++) want.Add(show[i]);
            var added = new List<int>();
            for (int i = 0; i < pools.Length; i++)
            {
                if (pools[i] != true || Array.IndexOf(Exclude, i) >= 0) continue;
                if (want.Add(i)) added.Add(i);
            }

            if (added.Count == 0)
            {
                Shown = ToArray(show);
                State = Settle(State, ClassifyNothingAdded(show, pools));
                _log?.Invoke($"[Lang] {where}: nothing to add ({State}) — show list [{Join(show)}]");
                return;
            }

            // Names: element write on the game's own array, English only (other empty names are just logged by ScanPools).
            if (added.Contains(EnglishSeaIndex) && names != null && names.Length > EnglishSeaIndex
                && string.IsNullOrEmpty(names[EnglishSeaIndex]))
                names[EnglishSeaIndex] = "English";

            if (ReparseNatively(mgr, show, want, added, where))
                Unlocked = added.ToArray();
            else if (added.Contains(EnglishSeaIndex) && SwapInPlace(mgr, where))
                Unlocked = new[] { EnglishSeaIndex };   // the in-place fallback can't grow the array: English only
            else
            {
                State = Settle(State, Outcome.CheckFailed);
                _log?.Invoke($"[Lang] !!! {where}: could not extend the show list — nothing exposed");
                return;
            }

            State = Outcome.Added;
            var back = ReadShow(mgr);
            if (back != null) Shown = ToArray(back);
            _log?.Invoke($"[Lang] {where}: unlocked [{string.Join(",", Unlocked)}] — showLangs_ read-back = " +
                (back == null ? "NULL" : $"[{Join(back)}]"));
        }
        catch (Exception ex)
        {
            State = Settle(State, Outcome.CheckFailed);
            _log?.Invoke($"[Lang] instance expose failed: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    // Why a pass added nothing, judged on the indices the show list does NOT contain:
    //   any has a pool (so it's only hidden by lang_exclude) → NothingToAdd; else any pool check failed → CheckFailed;
    //   else some are hidden but poolless → NoData; nothing hidden at all (SEA-style full list) → NothingToAdd.
    private static Outcome ClassifyNothingAdded(Il2CppStructArray<int> show, bool?[] pools)
    {
        bool anyPool = false, anyUnknown = false, anyHidden = false;
        for (int i = 0; i < pools.Length; i++)
        {
            if (Contains(show, i)) continue;
            anyHidden = true;
            if (pools[i] == true) anyPool = true;
            else if (pools[i] == null) anyUnknown = true;
        }
        if (anyPool)    return Outcome.NothingToAdd;
        if (anyUnknown) return Outcome.CheckFailed;
        return anyHidden ? Outcome.NoData : Outcome.NothingToAdd;
    }

    internal static int GetCurrentLanguage(object mgr)
    {
        try
        {
            var m = _mgrType?.GetMethod("GetCurrentLanguage", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            return m != null ? (int)m.Invoke(mgr, null)! : -1;
        }
        catch { return -1; }
    }

    internal static bool SetLanguage(object mgr, int index, bool fireWorldEvent)
    {
        try
        {
            var m = _mgrType?.GetMethod("SetLanguage", BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(int), typeof(bool) }, null);
            if (m == null) return false;
            m.Invoke(mgr, new object[] { index, fireWorldEvent });
            return true;
        }
        catch (Exception ex) { _log?.Invoke($"[Lang] SetLanguage({index}) failed: {ex.InnerException?.Message ?? ex.Message}"); return false; }
    }

    // ── Core ──

    // Primary: feed the game's own instance parser AnalysisDefaultAndShowList(string cfg, bool isAudio) the wanted list
    // (sorted ascending) in language_setting.txt's `idx=flag|…` form (flag "1" = shown), so the il2cpp side allocates and
    // stores showLangs_ itself. NOT the static (string, out int[]) overload — that one doesn't touch the field.
    private static bool ReparseNatively(object mgr, Il2CppStructArray<int> original, SortedSet<int> want, List<int> added,
        string where, bool isAudio = false)
    {
        const BindingFlags inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var parse = _mgrType?.GetMethod("AnalysisDefaultAndShowList", inst, null, new[] { typeof(string), typeof(bool) }, null);
        if (parse == null) { _log?.Invoke($"[Lang] {where}: AnalysisDefaultAndShowList(string,bool) not found — trying in-place swap"); return false; }

        // The parser is "default AND show list" — it may also rewrite defaultLang_. Our cfg carries no default intent, so
        // put the game's default back afterwards (an int field: plain value write, no allocation).
        var defProp = _mgrType!.GetProperty(isAudio ? "defaultAudioLang_" : "defaultLang_", inst);
        object? def = SafeGet(defProp, mgr);
        string extended = BuildCfg(want);
        try
        {
            parse.Invoke(mgr, new object[] { extended, isAudio });
            RestoreDefault(defProp, mgr, def, where);
            var after = ReadList(mgr, isAudio);
            bool ok = after != null && after.Length == want.Count;
            if (ok) foreach (int a in added) if (!Contains(after!, a)) { ok = false; break; }
            if (ok)
            {
                _log?.Invoke($"[Lang] {where}: re-parsed show list natively with \"{extended}\"");
                return true;
            }
            _log?.Invoke($"[Lang] {where}: native re-parse of \"{extended}\" gave [{(after == null ? "NULL" : Join(after))}] — reverting");
        }
        catch (Exception ex) { _log?.Invoke($"[Lang] {where}: native re-parse failed: {ex.InnerException?.Message ?? ex.Message} — reverting"); }

        // Never leave the field broken: re-parse the game's original list (game-allocated again); if even that doesn't
        // reproduce it, reattach the game's OWN original array object (game-allocated; our wrapper's GC handle kept it alive).
        try
        {
            string orig = BuildCfg(new SortedSet<int>(ToArray(original)));
            parse.Invoke(mgr, new object[] { orig, isAudio });
            RestoreDefault(defProp, mgr, def, where);
            var restored = ReadList(mgr, isAudio);
            if (restored == null || restored.Length != original.Length)
            {
                _mgrType.GetProperty(isAudio ? "showAudioLangs_" : "showLangs_", inst)?.SetValue(mgr, original);
                _log?.Invoke($"[Lang] !!! {where}: re-parse of \"{orig}\" didn't restore the list — reattached the original array");
            }
        }
        catch (Exception ex) { _log?.Invoke($"[Lang] !!! {where}: revert failed: {ex.InnerException?.Message ?? ex.Message}"); }
        return false;
    }

    // Fallback, no allocation: overwrite the LAST entry that is neither the current nor the default language with 11
    // (JP [2,4] → [2,11] — Korean drops out of Settings). Needs ≥2 entries so the row stays visible and ja stays pickable.
    private static bool SwapInPlace(object mgr, string where)
    {
        var show = ReadShow(mgr);
        if (show == null || show.Length < 2) { _log?.Invoke($"[Lang] {where}: in-place swap needs ≥2 entries"); return false; }

        int cur = GetCurrentLanguage(mgr);
        int def = SafeGet(_mgrType?.GetProperty("defaultLang_", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), mgr) is int d ? d : -1;
        for (int i = show.Length - 1; i >= 0; i--)
        {
            int old = show[i];
            if (old == cur || old == def) continue;
            show[i] = EnglishSeaIndex;   // writes straight into the game's own array memory
            _log?.Invoke($"[Lang] {where}: in-place swap — replaced language {old} with {EnglishSeaIndex} (it is no longer listed in Settings)");
            return true;
        }
        _log?.Invoke($"[Lang] {where}: in-place swap found no replaceable entry (cur={cur}, default={def})");
        return false;
    }

    private static string BuildCfg(SortedSet<int> want)
    {
        var sb = new StringBuilder();
        foreach (int i in want) { if (sb.Length > 0) sb.Append('|'); sb.Append(i).Append("=1"); }
        return sb.ToString();
    }

    private static void RestoreDefault(PropertyInfo? defProp, object mgr, object? def, string where)
    {
        if (defProp == null || def is not int want) return;
        if (SafeGet(defProp, mgr) is int now && now != want)
        {
            defProp.SetValue(mgr, want);
            _log?.Invoke($"[Lang] {where}: parser changed {defProp.Name} {want}→{now} — restored {want}");
        }
    }

    // ── Pool-file scan ──

    // ZSingleton<ZResLoader>.Instance.ExistFile(ENativeFileType.Bytes = 1, lanTypes_[i] + ".bytes") for every index — the
    // same check GetLanguageSetting itself uses for language_setting.txt. Per entry: true/false, null = that call failed.
    // Returns null when the loader isn't created yet (deferred, retried on the next call); cached once it ran.
    private static bool?[]? ScanPools(Il2CppStringArray types, Il2CppStringArray? names)
    {
        if (_pools != null && _pools.Length == types.Length) return _pools;
        try
        {
            var t = StellarInterop.FindType(ResLoaderName);
            var loader = GetCreatedSingleton(t);   // not created yet → defer; never create it ourselves
            if (t == null || loader == null) return null;

            MethodInfo? exist = null;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "ExistFile" || m.ReturnType != typeof(bool)) continue;
                var ps = m.GetParameters();
                if (ps.Length == 2 && ps[0].ParameterType.IsEnum && ps[1].ParameterType == typeof(string)) { exist = m; break; }
            }
            if (exist == null) { _log?.Invoke("[Lang] ZResLoader.ExistFile(ENativeFileType,string) not found"); return null; }
            var bytesType = Enum.ToObject(exist.GetParameters()[0].ParameterType, (byte)1);   // ENativeFileType.Bytes

            var pools = new bool?[types.Length];
            var files = new StringBuilder();
            var label = new StringBuilder();
            for (int i = 0; i < types.Length; i++)
            {
                string stem = types[i];
                if (string.IsNullOrEmpty(stem)) pools[i] = false;
                else
                {
                    try { pools[i] = (bool)exist.Invoke(loader, new[] { bytesType, (object)(stem + ".bytes") })!; }
                    catch { pools[i] = null; }
                }
                if (i > 0) { files.Append(", "); label.Append(", "); }
                files.Append(i).Append(' ').Append(string.IsNullOrEmpty(stem) ? "(none)" : stem).Append('=')
                     .Append(pools[i] == true ? 'Y' : pools[i] == false ? 'N' : '?');
                string? n = names != null && i < names.Length ? names[i] : null;
                label.Append(i).Append(' ').Append(string.IsNullOrEmpty(n) ? "(empty)" : $"\"{n}\"");
            }
            _log?.Invoke($"[Lang] pool files: {files}");
            _log?.Invoke($"[Lang] lanNames: {label}");
            _pools = pools;
            return pools;
        }
        catch (Exception ex) { _log?.Invoke($"[Lang] pool-file scan failed: {ex.InnerException?.Message ?? ex.Message}"); return null; }
    }

    // ── Small helpers ──

    private static Il2CppStructArray<int>? ReadShow(object mgr) => ReadProp(mgr, "showLangs_") as Il2CppStructArray<int>;

    private static Il2CppStructArray<int>? ReadList(object mgr, bool isAudio) =>
        ReadProp(mgr, isAudio ? "showAudioLangs_" : "showLangs_") as Il2CppStructArray<int>;

    private static bool Contains(Il2CppStructArray<int> a, int v)
    {
        for (int i = 0; i < a.Length; i++) if (a[i] == v) return true;
        return false;
    }

    private static int[] ToArray(Il2CppStructArray<int> a)
    {
        var r = new int[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = a[i];
        return r;
    }

    private static string Join(Il2CppStructArray<int> a) => string.Join(",", ToArray(a));

    private static object? SafeGet(PropertyInfo? p, object obj)
    {
        try { return p?.GetValue(obj); } catch { return null; }
    }

    private static object? ReadProp(object obj, string name)
    {
        try
        {
            return _mgrType?.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(obj);
        }
        catch { return null; }
    }
}
