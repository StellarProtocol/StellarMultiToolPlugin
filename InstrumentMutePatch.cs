using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Serialization;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

/// <summary>
/// One entry in the band-mute rule set, and the unit of persistence — the config API serialises whatever type it's
/// handed via System.Text.Json, so this round-trips as a readable JSON object rather than a packed string.
///
/// Both identifiers are kept. The uuid makes a restored pick bite on the very first note after a relaunch, with no
/// lookup needed; the name is the durable fallback that re-binds if a uuid ever doesn't survive, and the only half
/// a human can recognise. Either may be missing until the other resolves.
///
/// Public with public properties because System.Text.Json ignores fields and can't construct inaccessible types.
/// </summary>
public sealed class BandMutePick
{
    public string Name { get; set; } = "";
    public long   Uuid { get; set; }

    /// <summary>Backoff for the name back-fill. Runtime-only — never persisted.</summary>
    [JsonIgnore] public DateTime NameTriedUtc { get; set; }
}

/// <summary>
/// Per-player mute for OTHER players' band instruments (Season-3 Musician free-play).
///
/// Every note you hear from another player arrives as a networked <c>InstrumentPlayEvent</c> and is funnelled through
/// <c>InstrumentService.onInstrumentPlayEvent</c> → <c>processInstrumentSyncData</c> →
/// <c>EntityInstrumentPlayNote(entity, note, isLocal:false)</c>. That last call is the single choke point for remote
/// instrument audio, so a prefix there that returns false silences one specific performer and nothing else — the
/// entity stays fully alive (animation, instrument model, nameplate, network state all untouched; only the note
/// doesn't reach the audio engine).
///
/// Deliberately does NOT patch <c>EntityInstrumentReleaseNote</c>: releases must keep flowing, or a note being held
/// at the moment you hit mute would ring forever. A release for a note that never started is a harmless no-op, so
/// letting them all through costs nothing and removes the whole class of stuck-note bugs.
///
/// Two separate collections, deliberately:
///   • <b>heard</b> — transient, auto-populated from incoming notes: who is audible right now.
///   • <b>picks</b> — the durable rule set (blocklist, or allow-list when <see cref="MuteAllOthers"/> is on),
///     persisted to config and ALWAYS listable. Merging the two would mean a picked player who stops playing has
///     no row to untick, i.e. a mute you can't undo.
///
/// See Knowledge Base\Band-Instrument-Playback.md.
/// </summary>
internal static class InstrumentMutePatch
{
    private const string TargetType    = "Panda.ZGame.InstrumentService";
    private const long   PlayerMarker  = 640;   // uuid low-16 == 640 → a player entity (same marker OtherPlayerHider uses)
    private const double LocalUuidTtlS = 2.0;   // re-read our own uuid at most this often (the prefix is per-note)

    // ── knobs, driven from the UI ──────────────────────────────────────────────────────────────────────────────
    internal static bool Enabled;          // master switch — off = the prefix is a pure pass-through
    internal static bool MuteAllOthers;    // true = mute everyone EXCEPT the picked players (picks = allow-list)

    // ── the rule set ──────────────────────────────────────────────────────────────────────────────────────────
    private static readonly List<BandMutePick> _picks  = new();
    private static readonly HashSet<long>   _pickUuids = new();                                   // fast path for the prefix
    private static readonly HashSet<string> _pickNames = new(StringComparer.OrdinalIgnoreCase);   // fast path for re-binding

    // ── who we have heard playing ─────────────────────────────────────────────────────────────────────────────
    internal sealed class Performer
    {
        public long     Uuid;
        public string   Name = "";
        public int      Notes;
        public DateTime LastHeardUtc;
        public int      Blocked;      // notes we silenced, so the UI can show the mute is actually biting
        public DateTime NameTriedUtc; // last on-demand name lookup, so out-of-view performers aren't retried per frame
        public int      Seq;          // order first heard — the list's sort key, see Snapshot()
    }

    private static readonly Dictionary<long, Performer> _heard = new();
    private static int _seqNext;

    // ── install ───────────────────────────────────────────────────────────────────────────────────────────────
    private static Action<string>? _log;
    private static Func<long>?     _localUuidSource;
    private static PropertyInfo?   _piUuid;
    private static object?         _service;      // captured InstrumentService instance (for StopAllNotes)
    private static MethodInfo?     _miStopAll;

    internal static bool Install(Harmony harmony, Action<string> log, Func<long> localUuid)
    {
        _log             = log;
        _localUuidSource = localUuid;

        var t = StellarInterop.FindType(TargetType);
        if (t is null) { log("[BandMute] InstrumentService not found — patch skipped"); return false; }

        var play = StellarInterop.FindMethod(t, "EntityInstrumentPlayNote", 3);
        if (play is null) { log("[BandMute] EntityInstrumentPlayNote not found — patch skipped"); return false; }

        _miStopAll = StellarInterop.FindMethod(t, "EntityInstrumentStopAllNotes", 2);

        var entType = StellarInterop.FindType("Panda.ZGame.ZEntity");
        _piUuid = entType?.GetProperty("Uuid", BindingFlags.Instance | BindingFlags.Public);
        if (_piUuid is null) { log("[BandMute] ZEntity.Uuid not found — patch skipped"); return false; }

        try
        {
            harmony.Patch(play, prefix: new HarmonyMethod(typeof(InstrumentMutePatch), nameof(PrefixPlayNote)));
            log($"[BandMute] installed (stopAll={_miStopAll != null}, picks={_picks.Count})");
            return true;
        }
        catch (Exception ex) { log($"[BandMute] patch failed: {ex.Message}"); return false; }
    }

    internal static void Uninstall()
    {
        // Harmony teardown is owned by IHarmonyHost, which auto-unpatches every instance on plugin dispose —
        // so we must NOT unpatch here (that would double-unpatch). Only reset our own transient state.
        _service = null;
        _heard.Clear();
    }

    // ── the choke point ───────────────────────────────────────────────────────────────────────────────────────
    // __0 = ZEntity (the performer). Positional injection rather than by-name: the proven-reliable shape for
    // IL2CPP targets (see Knowledge Base\Harmony-Rewired-Input-Patching.md). Returning false skips the note.
    private static bool PrefixPlayNote(Il2CppObjectBase __instance, Il2CppObjectBase __0)
    {
        if (!Enabled) return true;
        try
        {
            _service ??= __instance;

            long uuid = Convert.ToInt64(_piUuid!.GetValue(__0));
            if (uuid == 0 || uuid == LocalUuid()) return true;   // never touch our own instrument audio

            var p = Track(uuid);
            if (!IsMuted(uuid)) return true;
            p.Blocked++;
            return false;                                        // ← the mute
        }
        catch (Exception ex)
        {
            // A throw here would break the game's audio for everyone, so failures always fall through to "audible".
            _log?.Invoke($"[BandMute] prefix error: {ex.InnerException?.Message ?? ex.Message}");
            return true;
        }
    }

    // Registers/updates the performer and lazily binds the display name.
    private static Performer Track(long uuid)
    {
        if (!_heard.TryGetValue(uuid, out var p))
        {
            p = new Performer { Uuid = uuid, Seq = _seqNext++ };
            _heard[uuid] = p;
        }
        // Naming is left to ResolveNames() on the plugin's 1s timer — this runs once per received note, which is
        // far too hot for a reflection round-trip into the HUD.
        p.Notes++;
        p.LastHeardUtc = DateTime.UtcNow;
        return p;
    }

    // Learning a performer's name lets a name-only pick (restored from an older config, or saved before we ever saw
    // their plate) attach to a live uuid — and back-fills the name on a uuid-only pick so it stays recognisable.
    /// <summary>Set when a pick gained a name or a uuid, so the plugin knows to re-save. Learning either half
    /// happens asynchronously (on the resolve timer), long after the click that created the pick — without this
    /// the completed entry would live only in memory and be lost on exit.</summary>
    internal static bool PicksDirty { get; private set; }

    internal static void ClearPicksDirty() => PicksDirty = false;

    private static void BindName(Performer p, string name)
    {
        p.Name = name;

        foreach (var pick in _picks)
        {
            if (pick.Uuid == p.Uuid && pick.Name.Length == 0)
            {
                pick.Name = name;
                _pickNames.Add(name);
                PicksDirty = true;
            }
            else if (pick.Uuid == 0 && pick.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                pick.Uuid = p.Uuid;
                _pickUuids.Add(p.Uuid);
                PicksDirty = true;
            }
        }
    }

    // ── mute state ────────────────────────────────────────────────────────────────────────────────────────────
    internal static bool IsPicked(long uuid) => _pickUuids.Contains(uuid);

    /// <summary>Normal mode: only picked players are muted. Mute-all mode: everyone except the picked.</summary>
    internal static bool IsMuted(long uuid) => MuteAllOthers ? !_pickUuids.Contains(uuid) : _pickUuids.Contains(uuid);

    internal static IReadOnlyList<BandMutePick> Picks() => _picks;

    /// <summary>Adds/removes a heard performer to/from the rule set. Returns the new picked state.</summary>
    internal static bool TogglePick(long uuid)
    {
        _heard.TryGetValue(uuid, out var perf);
        string name = perf?.Name ?? "";

        int at = _picks.FindIndex(p => p.Uuid == uuid
                                    || (uuid == 0 && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        if (at >= 0) { RemovePickAt(at); return false; }

        _picks.Add(new BandMutePick { Uuid = uuid, Name = name });
        _pickUuids.Add(uuid);
        if (name.Length > 0) _pickNames.Add(name);

        if (IsMuted(uuid)) StopAllNotes(uuid);   // silence whatever is ringing right now, don't wait for its release
        return true;
    }

    internal static void RemovePickAt(int index)
    {
        if (index < 0 || index >= _picks.Count) return;
        var pick = _picks[index];
        _picks.RemoveAt(index);
        RebuildPickIndex();

        // In mute-all mode, dropping an allow-list entry mutes them — kill anything they have ringing.
        if (pick.Uuid != 0 && IsMuted(pick.Uuid)) StopAllNotes(pick.Uuid);
    }

    internal static void ClearPicks()
    {
        _picks.Clear();
        RebuildPickIndex();
    }

    private static void RebuildPickIndex()
    {
        _pickUuids.Clear();
        _pickNames.Clear();
        foreach (var p in _picks)
        {
            if (p.Uuid != 0)        _pickUuids.Add(p.Uuid);
            if (p.Name.Length > 0)  _pickNames.Add(p.Name);
        }
    }

    // ── persistence ───────────────────────────────────────────────────────────────────────────────────────────
    // The rule set is stored as a plain JSON array of BandMutePick — the config API round-trips any type through
    // System.Text.Json, so there's no reason to hand-pack it into a string. Readable and hand-editable in
    // stellar.multitool.config.json, and immune to whatever characters a player name contains.
    internal static void LoadPicks(IEnumerable<BandMutePick>? picks)
    {
        _picks.Clear();
        if (picks != null)
            foreach (var p in picks)
            {
                if (p is null) continue;
                string name = p.Name ?? "";
                if (name.Length == 0 && p.Uuid == 0) continue;   // neither identifier — nothing to restore
                _picks.Add(new BandMutePick { Name = name, Uuid = p.Uuid });
            }
        RebuildPickIndex();
    }

    /// <summary>
    /// Reads the pre-JSON format (one <c>name TAB uuid</c> per line, or a bare name in the very first version).
    /// Only used to migrate an existing config forward — the caller re-saves as JSON immediately after.
    /// </summary>
    internal static void LoadLegacyNames(string? raw)
    {
        _picks.Clear();
        if (!string.IsNullOrEmpty(raw))
            foreach (var line in raw!.Split('\n'))
            {
                if (line.Length == 0) continue;
                var    parts = line.Split('\t');
                string name  = parts[0];
                long   uuid  = parts.Length > 1 && long.TryParse(parts[1], out var u) ? u : 0;
                if (name.Length == 0 && uuid == 0) continue;
                _picks.Add(new BandMutePick { Name = name, Uuid = uuid });
            }
        RebuildPickIndex();
    }

    // ── name resolution ───────────────────────────────────────────────────────────────────────────────────────
    private const double NameRetryS = 1.0;   // per-performer backoff for the on-demand name lookup

    /// <summary>
    /// Resolves display names for performers we only know by uuid, and re-binds any that match a pick restored
    /// from config.
    ///
    /// Must run on a timer, NOT only from the UI: a pick restored by name alone stays dormant until its name
    /// resolves, so if this only ran while the mute window was open, such a mute would silently do nothing until
    /// you opened that window. Names can't be relied on arriving via the nameplate postfix either — that only fires
    /// when the game builds or rebuilds a plate — so we ask the renderer directly, on a backoff (a performer out of
    /// view has no plate at all and will simply keep failing until they come back).
    /// </summary>
    internal static void ResolveNames()
    {
        var now = DateTime.UtcNow;
        foreach (var p in _heard.Values)
        {
            if (p.Name.Length > 0 || (now - p.NameTriedUtc).TotalSeconds < NameRetryS) continue;
            p.NameTriedUtc = now;
            var n = PlayerNameResolver.TryResolve(p.Uuid);
            if (!string.IsNullOrEmpty(n)) BindName(p, n!);
        }

        // Back-fill names on picks directly too. A pick saved by uuid alone (its owner's name hadn't resolved at
        // click time) is otherwise stuck showing "#1234…" forever: the loop above only covers players we've HEARD,
        // and a muted player who isn't currently performing never appears there.
        foreach (var pick in _picks)
        {
            if (pick.Uuid == 0 || pick.Name.Length > 0) continue;
            if ((now - pick.NameTriedUtc).TotalSeconds < NameRetryS) continue;
            pick.NameTriedUtc = now;
            var n = PlayerNameResolver.TryResolve(pick.Uuid);
            if (string.IsNullOrEmpty(n)) continue;
            pick.Name = n!;
            _pickNames.Add(n!);
            PicksDirty = true;
        }
    }

    // ── UI feed ───────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Performers we've heard, in the order we first heard them. Deliberately NOT sorted by "most recent": while
    /// several people play at once their last-heard times keep overtaking each other, so the rows reshuffle every
    /// refresh — and since a row is picked by index, one could swap under the cursor and hit the wrong player.
    /// First-heard order never changes, so new performers only ever append at the bottom.
    /// </summary>
    internal static List<Performer> Snapshot()
    {
        ResolveNames();   // also runs on the plugin's timer; the per-performer backoff makes the extra call free
        var list = new List<Performer>(_heard.Values);
        list.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        return list;
    }

    /// <summary>Clears the transient heard list. The rule set is a separate collection and is unaffected.</summary>
    internal static void ForgetAll()
    {
        _heard.Clear();
        _seqNext = 0;
    }

    // ── immediate silence ─────────────────────────────────────────────────────────────────────────────────────
    // Resolves the entity fresh from ZEntityMgr rather than caching the ZEntity we saw in the prefix — entities are
    // pooled/recycled, so a stale reference is a crash waiting to happen.
    private static void StopAllNotes(long uuid)
    {
        if (_service == null || _miStopAll == null) return;
        try
        {
            var ent = ResolveEntity(uuid);
            if (ent != null) _miStopAll.Invoke(_service, new object?[] { ent, false });
        }
        catch (Exception ex) { _log?.Invoke($"[BandMute] stopAll error: {ex.InnerException?.Message ?? ex.Message}"); }
    }

    private static MethodInfo? _miGetEntity;
    private static bool        _entMgrResolved;

    private static object? ResolveEntity(long uuid)
    {
        if (!_entMgrResolved)
        {
            _entMgrResolved = true;
            var mgrType = StellarInterop.FindType("Panda.ZGame.ZEntityMgr");
            if (mgrType != null)
                foreach (var m in mgrType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (m.Name == "GetEntity" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(long))
                    { _miGetEntity = m; break; }
        }
        var mgr = StellarInterop.GetSingleton("Panda.ZGame.ZEntityMgr");
        return mgr == null || _miGetEntity == null ? null : _miGetEntity.Invoke(mgr, new object[] { uuid });
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────
    private static long     _localUuid;
    private static DateTime _localUuidAt;

    // Throttled: the prefix runs once per received note, and the snapshot lookup isn't free.
    private static long LocalUuid()
    {
        var now = DateTime.UtcNow;
        if (_localUuid == 0 || (now - _localUuidAt).TotalSeconds > LocalUuidTtlS)
        {
            try { _localUuid = _localUuidSource?.Invoke() ?? 0; } catch { }
            _localUuidAt = now;
        }
        return _localUuid;
    }

    internal static bool IsPlayerUuid(long uuid) => (uuid & 0xFFFF) == PlayerMarker;
}
