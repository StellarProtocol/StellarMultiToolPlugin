using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

/// <summary>
/// Resolves a player entity uuid → their displayed name from the game's overhead nameplate.
///
/// <c>Panda.Hud.HudTitleRender.Instance.GetTitle(titleId)</c> returns that plate's <c>HudTitle</c>; its
/// <c>titleEntryDic_</c> (<c>ZDictionary&lt;EHudTitleType, HudTextBaseEntry&gt;</c>) holds one entry per slot, and
/// the <c>EPlayerName</c> (0) entry carries the name in <c>ValidText</c>. Only player plates have that slot, so
/// this is player-scoped by construction.
///
/// VERIFIED: the titleId key space IS the entity uuid — a muted performer's uuid was found among the live
/// <c>hudTitleDict_</c> keys (see <see cref="DumpTitleKeys"/>).
///
/// The dictionary is looked up BY KEY via its <c>Item</c> indexer rather than by walking <c>Values</c>: fewer
/// moving parts, and it can't be defeated by one unreadable sibling entry. Walking remains as a fallback.
///
/// Pull-based on purpose. Postfixing the game's plate-build only fires when a plate is BUILT or REBUILT, so a
/// player already standing there when the feature was switched on would never be captured; asking the renderer
/// directly works whenever the plate exists.
///
/// Returns null when the plate isn't registered (player out of view) — callers keep their own fallback.
/// </summary>
internal static class PlayerNameResolver
{
    private const int SlotPlayerName = 0;   // EHudTitleType.EPlayerName

    private static readonly Dictionary<long, string> _cache = new();

    private static IPluginServices? _services;

    /// <summary>Supplies the framework services used by the primary lookup path. Without this only the HUD
    /// fallback is available.</summary>
    internal static void Init(IPluginServices services) => _services = services;

    private static bool          _resolved, _resolveFailed;
    private static Type?         _renderType;         // HudTitleRender (read live via StellarInterop.GetSingleton)
    private static bool          _hasRenderInstance;  // HudTitleRender exposes a static Instance accessor
    private static MethodInfo?   _miGetTitle;         // HudTitleRender.GetTitle(long)
    private static MethodInfo?   _miContainsTitle;    // HudTitle.ContainsTitle(EHudTitleType)
    private static PropertyInfo? _piEntryDic;         // HudTitle.titleEntryDic_
    private static FieldInfo?    _fiEntryDic;
    private static Type?         _textEntryType;      // HudTextBaseEntry
    private static MethodInfo?   _miTryCastText;      // Il2CppObjectBase.TryCast<HudTextBaseEntry>()
    private static PropertyInfo? _piTitleType, _piValidText;
    private static FieldInfo?    _fiValidText;
    private static object?       _slotPlayerNameBoxed;

    private const BindingFlags AnyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    internal static string? TryResolve(long uuid)
    {
        if (_cache.TryGetValue(uuid, out var cached) && cached.Length > 0) return cached;
        var name = ReadName(uuid, null);
        if (!string.IsNullOrEmpty(name)) _cache[uuid] = name!;
        return name;
    }

    internal static void ClearCache() => _cache.Clear();

    // Core lookup. `diag` (when non-null) records what happened at each stage so a failure can be diagnosed from
    // the log instead of guessed at — the failure modes ("no plate", "no name slot", "slot present but empty")
    // are indistinguishable from the UI, where all three just leave a "#1234…" row.
    private static string? ReadName(long uuid, StringBuilder? diag)
    {
        // ── primary: framework services (the path StellarMinimalNameplatePlugin uses, known to work) ──
        var s = _services;
        if (s == null) diag?.Append("noServices ");
        else
        {
            try
            {
                if (uuid == s.CombatSnapshot.LocalEntityId.Value)
                {
                    var self = s.PlayerState.Name;
                    if (!string.IsNullOrEmpty(self)) { diag?.Append("src=self"); return self; }
                }
            }
            catch (Exception ex) { diag?.Append("self=err:").Append(Short(ex)).Append(' '); }

            try
            {
                long charId = uuid >> 16;   // a player entity uuid is charId<<16 | typeMarker(640)
                foreach (var m in s.PartyRoster.Members)
                    if (m.CharId == charId && !string.IsNullOrEmpty(m.Name))
                    { diag?.Append("src=party"); return m.Name; }
            }
            catch (Exception ex) { diag?.Append("party=err:").Append(Short(ex)).Append(' '); }

            try
            {
                var el = s.CombatLookup.GetEntityName(new EntityId(uuid));
                if (!string.IsNullOrEmpty(el)) { diag?.Append("src=combatLookup"); return el; }
                diag?.Append("combatLookup=<empty> ");
            }
            catch (Exception ex) { diag?.Append("combatLookup=err:").Append(Short(ex)).Append(' '); }
        }

        // ── fallback: read the overhead nameplate directly ──
        if (!Resolve()) { diag?.Append("hud unavailable: ").Append(Status()); return null; }
        try
        {
            var render = StellarInterop.GetSingleton(_renderType);
            if (render == null) { diag?.Append("HudTitleRender.Instance=null"); return null; }

            var title = _miGetTitle!.Invoke(render, new object[] { uuid });
            if (title == null) { diag?.Append("GetTitle=null (no plate registered for this uuid)"); return null; }
            diag?.Append("title=").Append(title.GetType().Name);

            if (_miContainsTitle != null && _slotPlayerNameBoxed != null)
                try { diag?.Append(" hasNameSlot=").Append(_miContainsTitle.Invoke(title, new[] { _slotPlayerNameBoxed })); }
                catch (Exception ex) { diag?.Append(" hasNameSlot=err:").Append(Short(ex)); }

            var dic = _piEntryDic != null ? _piEntryDic.GetValue(title) : _fiEntryDic?.GetValue(title);
            if (dic == null) { diag?.Append(" titleEntryDic_=null"); return null; }
            var dicType = dic.GetType();
            diag?.Append(" dic=").Append(dicType.Name);
            try { diag?.Append(" count=").Append(dicType.GetProperty("Count")?.GetValue(dic)); } catch { }

            // 1) direct key lookup
            if (_slotPlayerNameBoxed != null)
            {
                try
                {
                    var item = dicType.GetProperty("Item", AnyInst);
                    if (item == null) diag?.Append(" noItemProp");
                    else
                    {
                        var entry = item.GetValue(dic, new[] { _slotPlayerNameBoxed });
                        if (entry == null) diag?.Append(" item=null");
                        else
                        {
                            var text = ReadEntryText(entry);
                            diag?.Append(" item.text=").Append(Show(text));
                            if (!string.IsNullOrEmpty(text)) return text;
                        }
                    }
                }
                catch (Exception ex) { diag?.Append(" item=err:").Append(Short(ex)); }
            }

            // 2) fallback: walk every entry
            var values = dicType.GetProperty("Values", AnyInst)?.GetValue(dic);
            if (values == null) { diag?.Append(" Values=null"); return null; }

            int n = 0;
            string? found = null;
            foreach (var entry in WalkIl2Cpp(values))
            {
                n++;
                try
                {
                    int?    slot = ReadEntrySlot(entry);
                    string? text = ReadEntryText(entry);
                    diag?.Append(' ').Append(entry.GetType().Name).Append('[').Append(slot?.ToString() ?? "?")
                        .Append("]=").Append(Show(text));
                    if (slot == SlotPlayerName && !string.IsNullOrEmpty(text)) found ??= text;
                }
                catch (Exception ex) { diag?.Append(" entryErr:").Append(Short(ex)); }
            }
            if (n == 0) diag?.Append(" walked=0");
            return found;
        }
        catch (Exception ex) { diag?.Append("err:").Append(Short(ex)); return null; }
    }

    private static string? ReadEntryText(object entry)
    {
        var et = entry.GetType();
        var pi = et.GetProperty("ValidText", AnyInst);
        if (pi != null) return pi.GetValue(entry) as string;
        var fi = et.GetField("ValidText", AnyInst);
        if (fi != null) return fi.GetValue(entry) as string;

        // Entry surfaced as a base-type wrapper — re-cast to the concrete text entry.
        var typed = TryCastText(entry);
        if (typed == null) return null;
        return (_piValidText != null ? _piValidText.GetValue(typed) : _fiValidText?.GetValue(typed)) as string;
    }

    private static int? ReadEntrySlot(object entry)
    {
        var et  = entry.GetType();
        var raw = et.GetProperty("TitleType", AnyInst)?.GetValue(entry);
        if (raw == null)
        {
            var typed = TryCastText(entry);
            if (typed != null) raw = _piTitleType?.GetValue(typed);
        }
        return raw == null ? null : Convert.ToInt32(raw);
    }

    private static object? TryCastText(object entry)
    {
        try { return _miTryCastText?.Invoke(entry, null); } catch { return null; }
    }

    private static bool Resolve()
    {
        if (_resolved) return !_resolveFailed;
        _resolved = true;

        _renderType    = StellarInterop.FindType("Panda.Hud.HudTitleRender");
        var titleType  = StellarInterop.FindType("Panda.Hud.HudTitle");
        _textEntryType = StellarInterop.FindType("Panda.Hud.HudTextBaseEntry");
        var enumType   = StellarInterop.FindType("Panda.Hud.EHudTitleType");
        if (_renderType == null || titleType == null) { _resolveFailed = true; return false; }

        _hasRenderInstance = StellarInterop.FindPropertyUp(_renderType, "Instance") != null;
        foreach (var m in _renderType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (m.Name == "GetTitle" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(long))
            { _miGetTitle = m; break; }

        foreach (var m in titleType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (m.Name == "ContainsTitle" && m.GetParameters().Length == 1) { _miContainsTitle = m; break; }

        _piEntryDic = titleType.GetProperty("titleEntryDic_", AnyInst);
        if (_piEntryDic == null) _fiEntryDic = titleType.GetField("titleEntryDic_", AnyInst);

        if (enumType != null)
            try { _slotPlayerNameBoxed = Enum.ToObject(enumType, SlotPlayerName); } catch { }

        if (_textEntryType != null)
        {
            _piTitleType = _textEntryType.GetProperty("TitleType", AnyInst);
            _piValidText = _textEntryType.GetProperty("ValidText", AnyInst);
            if (_piValidText == null) _fiValidText = _textEntryType.GetField("ValidText", AnyInst);

            var tryCast = typeof(Il2CppObjectBase).GetMethod("TryCast", BindingFlags.Public | BindingFlags.Instance);
            if (tryCast != null && tryCast.IsGenericMethodDefinition)
                try { _miTryCastText = tryCast.MakeGenericMethod(_textEntryType); } catch { }
        }

        _resolveFailed = !_hasRenderInstance || _miGetTitle == null
                         || (_piEntryDic == null && _fiEntryDic == null);
        return !_resolveFailed;
    }

    /// <summary>Diagnostic string for the install log — which bits of the HUD API we found.</summary>
    internal static string Status()
    {
        Resolve();
        return $"inst={_hasRenderInstance} getTitle={_miGetTitle != null} " +
               $"entryDic={_piEntryDic != null || _fiEntryDic != null} contains={_miContainsTitle != null} " +
               $"slotBox={_slotPlayerNameBoxed != null} textEntry={_textEntryType != null} cast={_miTryCastText != null}";
    }

    /// <summary>Full stage-by-stage trace of one lookup, for the log.</summary>
    internal static string Describe(long uuid)
    {
        var sb = new StringBuilder();
        var name = ReadName(uuid, sb);
        return $"#{uuid}: {(string.IsNullOrEmpty(name) ? "NO NAME" : "name='" + name + "'")} | {sb}";
    }

    /// <summary>
    /// Dumps the keys of <c>HudTitleRender.hudTitleDict_</c> — the titleIds the game actually registered plates
    /// under. Comparing them against the uuids we mute is what proved the two key spaces are the same.
    /// </summary>
    internal static string DumpTitleKeys(int max = 24)
    {
        if (!Resolve()) return $"resolver unavailable ({Status()})";
        try
        {
            var render = StellarInterop.GetSingleton(_renderType);
            if (render == null) return "no HudTitleRender instance";

            var rt   = render.GetType();
            var dict = rt.GetProperty("hudTitleDict_", AnyInst)?.GetValue(render)
                       ?? rt.GetField("hudTitleDict_", AnyInst)?.GetValue(render);
            if (dict == null) return "hudTitleDict_ not reachable";

            var keys = dict.GetType().GetProperty("Keys")?.GetValue(dict);
            if (keys == null) return "hudTitleDict_.Keys not reachable";

            var sb = new StringBuilder();
            int n = 0;
            foreach (var k in WalkIl2Cpp(keys))
            {
                if (n++ >= max) { sb.Append('…'); break; }
                sb.Append(k).Append(' ');
            }
            return n == 0 ? "hudTitleDict_ is EMPTY (no plates registered)" : $"{n} key(s): {sb}";
        }
        catch (Exception ex) { return $"dump failed: {Short(ex)}"; }
    }

    private static string Show(string? s) => s == null ? "<null>" : s.Length == 0 ? "<empty>" : "'" + s + "'";
    private static string Short(Exception ex) => (ex.InnerException ?? ex).Message;

    // Walks an Il2Cpp enumerable (KeyCollection/ValueCollection/etc.) via GetEnumerator/MoveNext/Current.
    private static List<object> WalkIl2Cpp(object collection)
    {
        var res = new List<object>();
        MethodInfo? getEnum = null;
        foreach (var m in collection.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (m.Name == "GetEnumerator" && m.GetParameters().Length == 0) { getEnum = m; break; }
        var en = getEnum?.Invoke(collection, null);
        if (en == null) return res;

        var enT = en.GetType();
        MethodInfo? move = null;
        foreach (var m in enT.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (m.Name == "MoveNext" && m.GetParameters().Length == 0) { move = m; break; }
        var cur = enT.GetProperty("Current");
        if (move == null || cur == null) return res;

        while ((bool)move.Invoke(en, null)!)
        {
            var v = cur.GetValue(en);
            if (v != null) res.Add(v);
        }
        return res;
    }
}
