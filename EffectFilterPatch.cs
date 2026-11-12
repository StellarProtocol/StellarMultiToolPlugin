using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

/// <summary>
/// Effect Filter mechanism: skips loading the skill effects OTHER PLAYERS cast (KB Performance-Levers.md "Effects by
/// OWNER RELATION"). Effects are ~4.5 ms/frame of the main thread in raids; skipping one BEFORE it loads saves that CPU
/// (the game's FX-layer cull only saves GPU and also hides telegraphs).
/// <list type="bullet">
/// <item>Hook: Harmony PREFIX on <c>Panda.ZEffect.ZEffect.LoadEffect()</c> (no params). Returning false skips the load;
/// the effect stays registered and the game's own life check removes it. Skipped effects are also silent (audio plays
/// after load).</item>
/// <item>Rule — the CASTER decides, not the target; every switch means HIDE (user-approved 2026-11-11), first match
/// wins (Rule):
/// 0. raid/dungeon ground marks (the leader's "punctuate" marks, <c>Context.Addr</c> ∈ the 6 <c>p_fx_dungeonsmask0N</c>
/// asset hashes) → ALWAYS kept, no switch: a teammate's mark has FromUuid = that player, so without this it would fall
/// under "Other players' effects" (KB Marker-and-Raid-Coordination.md);
/// a. telegraph (Type = Warning) or IsImportant → hidden iff "Telegraphs &amp; important markers";
/// b. cast by me / my pet, summon or bullet (incl. what I cast on myself) → hidden iff "My own effects";
/// c. cast by another player (<c>UuidToEntType(FromUuid) == Char</c>) or — top summoner (<c>AttrTopSummonerId=91</c>,
/// fallback 90) — their Pet/Bullet/ClientBullet (only while "…their pets &amp; summons" is on; else kept) → hidden iff
/// "Other players' effects" is on, except a buff-sourced one is kept while "…their heals &amp; buffs" is off and one
/// that lands on me (BelongUuid = me) is kept while "…their effects on me" is off;
/// d. anything else (monster/NPC/scene object/zone/unknown caster) → hidden iff "Boss &amp; monster effects".</item>
/// <item>Accessors are the Experiment census's (validated in game), bound once into typed delegates
/// (<see cref="EffectFilterBind"/>).</item>
/// </list>
/// Perf (Plugin-Performance-Rules.md §7) — fires per effect load, hundreds/s in a raid:
/// <list type="bullet">
/// <item>Inactive (nothing can hide / outside the chosen scope / my uuid unknown) = one static bool read.</item>
/// <item>The decision is cached per effect uid (the game may call LoadEffect again for the same uid → same answer,
/// O(1), no context read), and the uid is read BEFORE the context so a repeat skips the context wrapper.</item>
/// <item>The owner lookup (GetEntity + GetAttr) runs only for Pet/Bullet casters whose answer can change the outcome
/// (see Relation), once per caster uuid (cached, misses too). Char and Monster casters decide from the uuid alone.</item>
/// <item>Caches clear on scene change, on a rule change and when my uuid changes; capped at 16384 entries.</item>
/// <item>No strings or Invoke on the hot path; timing only when <see cref="StellarDiagnostics"/> is on.</item>
/// </list>
/// Installed on the first IN-WORLD tick after enable — never at construction / title screen / loading, which broke world
/// entry (see <see cref="WorldReady"/>) — and never unpatched at runtime (state-gated; runtime unpatch of a hot method risks the
/// trampoline crash — Framework-Interop-Foundation.md "LAZY-INSTALL"); IHarmonyHost unpatches on plugin dispose.
/// Main thread only (ZEffectManager.LateUpdate drives loads).
/// </summary>
internal static class EffectFilterPatch
{
    // ── Knobs (Plugin.EffectFilter.cs) ───────────────────────────────────────────────────────────────────────────
    public static bool Active;               // something can hide && in scope && my uuid known — the ONE read when off
    // Phase == World && IsWorldActive && !Loading (plugin: 10 Hz tick + SceneChanged/Logout). Outside it the prefix keeps
    // every effect untouched — loading screens / scene switches are never filtered (Game-Phases-and-Tick-Gating.md
    // "Patching ZEffect.LoadEffect before world entry breaks login").
    public static bool WorldReady;
    // Every knob is a HIDE switch (true = hidden), mirroring the window rows.
    public static bool HideOthers;           // "Other players' effects"
    public static bool HidePets     = true;  // "…their pets & summons"   (only while HideOthers)
    public static bool HideBuffs;            // "…their heals & buffs"    (only while HideOthers)
    public static bool HideOnMe;             // "…their effects on me"    (only while HideOthers)
    public static bool HideMine;             // "My own effects"
    public static bool HideMonsters;         // "Boss & monster effects"
    public static bool HideMarkers;          // "Telegraphs & important markers"
    public static long LocalUuid;            // CombatSnapshot.LocalEntityId, refreshed by the plugin's 1 s tick
    public static int  HiddenThisFight;      // first-time hides (a retried uid isn't recounted); reset per fight/scene
    public static bool Installed  { get; private set; }
    public static bool HardFailed { get; private set; }   // types found but binding/patching failed → never retried

    // EEntityType (dump.cs 698772); EffectContext.EEffectType; EBattleDataType.
    private const int EntBullet = 6, EntClientBullet = 7, EntPet = 8, EntChar = 10;
    private const int TypeWarning = 2;
    private const int SrcBuff = 1;
    private const int MaxCache = 16384;

    private static Action<string>? _log;

    // ── Bound accessors ──────────────────────────────────────────────────────────────────────────────────────────
    private static Func<object, object?>? _ctxOf;            // ZEffect.Context
    private static Func<object, long>? _effUid;              // ZEffect.Uid
    private static Func<object, long>? _from, _belong, _type, _src;
    private static Func<object, bool>? _important;
    private static Func<object, long>? _addr;                 // EffectContext.Addr (uint asset hash) — optional
    private static Func<long, long>? _entType;               // ZEntity.UuidToEntType (static, pure uuid decode)
    private static Func<object?>? _mgrInstance;               // ZEntityMgr.Instance
    private static Func<object, long, object?>? _getEntity;   // ZEntityMgr.GetEntity(long)
    private static Func<object, long>? _attrTop, _attrSum;    // GetAttr<long>(91 / 90, true)
    private static object? _mgr;                              // refreshed at 1 Hz (RefreshManager)

    // ── Caches (scene-scoped) ────────────────────────────────────────────────────────────────────────────────────
    private static readonly Dictionary<long, bool> _decided = new();   // effect uid → hide
    private static readonly Dictionary<long, long> _ownerOf = new();   // pet/bullet uuid → top summoner (0 = none)

    // ── Raid/dungeon ground marks (rule 0) ───────────────────────────────────────────────────────────────────────
    // Global.lua FlagSkillEffectAddr (= ZIndicatorMgr.FlagSkillAddrPrefix + p_fx_dungeonsmask0N), slot N = index + 1.
    // In game (2026-09-30) Addr was slot1 = 0x99B514CD, …D1 (5), …D2 (6) = 0x99B514CC + slot.
    private const string MarkAddrPrefix = "effect/common_new/tips/p_fx_dungeonsmask0";
    private const uint MarkSlot1Seen = 0x99B514CD;
    private static readonly uint[] _marks = new uint[6];               // filled once at bind, never resized
    private static uint _markLo, _markHi;                               // bounds → one range compare for non-marks
    private static bool _markLogged;                                    // first kept mark logged (reset per scene)

    // ── Diagnostics (STELLAR_DIAGNOSTICS=1 only) ─────────────────────────────────────────────────────────────────
    private static bool _diag;
    private static int  _calls, _hides, _errors, _errLogged, _ownerLookups;
    private static long _costTicks, _costMax;

    public static bool Install(Harmony harmony, Action<string> log)
    {
        if (Installed || HardFailed) return Installed;
        _log  = log;
        _diag = StellarDiagnostics.IsEnabled;
        try
        {
            var effT = StellarInterop.FindType("Panda.ZEffect.ZEffect");
            var ctxT = StellarInterop.FindType("Panda.ZGame.EffectContext");
            var entT = StellarInterop.FindType("Panda.ZGame.ZEntity");
            if (effT == null || ctxT == null || entT == null) return false;   // not loaded yet → the plugin retries

            var load = BindCore(effT, ctxT, entT);
            BindOwner(entT);
            BindMarks(log);
            log($"[EffFilter] bind: load={load != null} ctx={_ctxOf != null} uid={_effUid != null} from={_from != null} " +
                $"belong={_belong != null} type={_type != null} src={_src != null} imp={_important != null} " +
                $"entType={_entType != null} mgr={_mgrInstance != null} getEnt={_getEntity != null} " +
                $"attrTop={_attrTop != null} attrSum={_attrSum != null} addr={_addr != null}");
            if (load == null || _ctxOf == null || _from == null || _belong == null || _type == null || _src == null
                || _important == null || _entType == null)
            { HardFailed = true; log("[EffFilter] essential accessor missing — not patching"); return false; }

            harmony.Patch(load, prefix: new HarmonyMethod(typeof(EffectFilterPatch), nameof(Prefix)));
            Installed = true;
            log($"[EffFilter] patched ZEffect.LoadEffect (prefix) diag={_diag}");
            return true;
        }
        catch (Exception ex)
        {
            HardFailed = true;
            log($"[EffFilter] install failed: {ex.InnerException?.Message ?? ex.Message}");
            return false;
        }
    }

    // The decision's essentials: the hook target and the effect/context reads. Returns LoadEffect() or null.
    private static MethodInfo? BindCore(Type effT, Type ctxT, Type entT)
    {
        MethodInfo? load = null;
        foreach (var m in effT.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            if (m.Name == "LoadEffect" && m.GetParameters().Length == 0 && !m.IsGenericMethodDefinition) { load = m; break; }

        _ctxOf     = EffectFilterBind.RefProp(effT, "Context");
        _effUid    = EffectFilterBind.NumProp(effT, "Uid");
        _from      = EffectFilterBind.NumProp(ctxT, "FromUuid");
        _belong    = EffectFilterBind.NumProp(ctxT, "BelongUuid");
        _type      = EffectFilterBind.NumProp(ctxT, "Type");
        _src       = EffectFilterBind.NumProp(ctxT, "Source");
        _important = EffectFilterBind.BoolProp(ctxT, "IsImportant");
        _addr      = EffectFilterBind.NumProp(ctxT, "Addr");

        foreach (var m in entT.GetMethods(BindingFlags.Public | BindingFlags.Static))
            if (m.Name == "UuidToEntType" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(long))
            { _entType = EffectFilterBind.StaticNum(m); break; }
        return load;
    }

    // Optional owner chain for pets/summons/bullets: ZEntityMgr.Instance → GetEntity(uuid) → GetAttr<long>(91 / 90).
    // Missing → pets & summons are simply never hidden (kept), the rest of the filter still works.
    private static void BindOwner(Type entT)
    {
        const BindingFlags pi = BindingFlags.Public | BindingFlags.Instance;
        var mgrT = StellarInterop.FindType("Panda.ZGame.ZEntityMgr");
        var attrEnum = StellarInterop.FindType("Zproto.EAttrType");
        if (mgrT != null)
        {
            _mgrInstance = EffectFilterBind.StaticRef(mgrT.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetGetMethod());
            foreach (var m in mgrT.GetMethods(pi))
                if (m.Name == "GetEntity" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(long))
                { _getEntity = EffectFilterBind.Ref1Long(m); break; }
        }
        if (attrEnum == null) return;
        foreach (var m in entT.GetMethods(pi))
        {
            if (m.Name != "GetAttr" || !m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            if (ps.Length != 2 || ps[0].ParameterType != attrEnum || ps[1].ParameterType != typeof(bool)) continue;
            var gl = m.MakeGenericMethod(typeof(long));
            _attrTop = EffectFilterBind.AttrLong(gl, Enum.ToObject(attrEnum, 91));   // AttrTopSummonerId
            _attrSum = EffectFilterBind.AttrLong(gl, Enum.ToObject(attrEnum, 90));   // AttrSummonerId
            break;
        }
    }

    // The 6 mark Addr values, once. Preferred source = the game's own hash (ZUtil.ZMisc.Hash33(string), the function
    // that produced the in-game values) over the Global.lua asset paths — trusted only if slot 1 reproduces the value
    // seen in game, so a different hash input (extension / case) can't silently yield a wrong set. Otherwise the
    // literal in-game range. Invoke is fine here: bind time, 6 calls, never on the hot path.
    private static void BindMarks(Action<string> log)
    {
        bool hashed = false;
        try
        {
            MethodInfo? h = null;
            var misc = StellarInterop.FindType("ZUtil.ZMisc");
            if (misc != null)
                foreach (var m in misc.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (m.Name == "Hash33" && m.ReturnType == typeof(uint)
                        && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(string)) { h = m; break; }
            if (h != null)
            {
                var v = new uint[6];
                for (int i = 0; i < 6; i++) v[i] = (uint)h.Invoke(null, new object[] { MarkAddrPrefix + (i + 1) })!;
                if (v[0] == MarkSlot1Seen) { Array.Copy(v, _marks, 6); hashed = true; }
                else log($"[EffectFilter] raid marks: Hash33 slot1=0x{v[0]:X8} != 0x{MarkSlot1Seen:X8} seen in game — not trusted");
            }
        }
        catch (Exception ex) { log($"[EffectFilter] raid marks: Hash33 failed: {ex.InnerException?.Message ?? ex.Message}"); }
        if (!hashed) for (int i = 0; i < 6; i++) _marks[i] = MarkSlot1Seen + (uint)i;
        _markLo = uint.MaxValue; _markHi = 0;
        foreach (var a in _marks) { if (a < _markLo) _markLo = a; if (a > _markHi) _markHi = a; }
        log($"[EffectFilter] raid marks: {(hashed ? "hash33" : "literal")} 0x{_marks[0]:X8}-0x{_marks[5]:X8}" +
            (_addr == null ? " (EffectContext.Addr unbound — marks NOT protected)" : ""));
    }

    // Range compare first (every non-mark effect stops there); the 6-entry scan only runs inside the bounds.
    private static bool IsMark(uint a)
    {
        if (a < _markLo || a > _markHi) return false;
        var m = _marks;
        for (int i = 0; i < m.Length; i++) if (m[i] == a) return true;
        return false;
    }

    // Diagnostic: the first kept mark per scene (one string, once) so it can be confirmed in game.
    private static void LogMark(object ctx, uint addr)
    {
        _markLogged = true;
        long from = _from!(ctx);
        _log?.Invoke($"[EffectFilter] raid mark kept addr=0x{addr:X8} from={EntTypeOf(from)} " +
                     $"belong={(LocalUuid != 0 && _belong!(ctx) == LocalUuid ? "me" : "other")} src={_src!(ctx)} " +
                     $"important={_important!(ctx)}");
    }

    /// <summary>1 Hz from the plugin: re-fetch the entity manager wrapper so owner lookups never fetch it per call.</summary>
    public static void RefreshManager()
    {
        try { _mgr = _mgrInstance?.Invoke(); } catch { _mgr = null; }
    }

    /// <summary>Scene change / rule change / new local uuid: forget every cached decision and owner.</summary>
    public static void ResetCaches()
    {
        _decided.Clear();
        _ownerOf.Clear();
        _markLogged = false;
    }

    /// <summary>Caster type of my own uuid (10 = Char expected) — logged once per new uuid to verify the uuid source.</summary>
    public static long EntTypeOf(long uuid) => uuid != 0 && _entType != null ? _entType(uuid) : -1;

    // ── Hook ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // false = skip LoadEffect. Any error keeps the effect (fail visible, never fail hidden).
    private static bool Prefix(object __instance)
    {
        if (!Active || !WorldReady) return true;
        long t0 = _diag ? Stopwatch.GetTimestamp() : 0;
        bool hide = false;
        try { hide = Decide(__instance); }
        catch (Exception ex)
        {
            _errors++;
            if (_errLogged++ < 5) _log?.Invoke($"[EffFilter] decide error: {ex.InnerException?.Message ?? ex.Message}");
        }
        if (_diag)
        {
            long dt = Stopwatch.GetTimestamp() - t0;
            _calls++;
            if (hide) _hides++;
            _costTicks += dt;
            if (dt > _costMax) _costMax = dt;
        }
        return !hide;
    }

    private static bool Decide(object eff)
    {
        long uid = _effUid != null ? _effUid(eff) : 0;
        if (uid != 0 && _decided.TryGetValue(uid, out bool cached)) return cached;

        var ctx = _ctxOf!(eff);
        if (ctx == null) return false;   // not cached: a retry may carry the context

        bool hide = Rule(ctx);
        if (uid != 0)
        {
            if (_decided.Count >= MaxCache) _decided.Clear();
            _decided[uid] = hide;
        }
        if (hide) HiddenThisFight++;
        return hide;
    }

    // First match wins (class summary 0, a–d). Cheapest reads first: mark addr, type/important, then the caster; source
    // and target are read only for another player's effect that the master would hide.
    private static bool Rule(object ctx)
    {
        // 0. Ground marks are never hidden — before the Warning/Important check, so even "Telegraphs & important
        // markers" can't hide them.
        if (_addr != null)
        {
            uint a = (uint)_addr(ctx);
            if (IsMark(a)) { if (!_markLogged) LogMark(ctx, a); return false; }
        }
        if ((int)_type!(ctx) == TypeWarning || _important!(ctx)) return HideMarkers;
        long me = LocalUuid;
        switch (Relation(_from!(ctx), me))
        {
            case RelMine:        return HideMine;
            case RelOther:       return HideOthersEffect(ctx, me);
            case RelOtherSummon: return HidePets && HideOthersEffect(ctx, me);
            default:             return HideMonsters;
        }
    }

    // Another player's effect: hidden by the master unless a sub-row that is OFF keeps it (heals/buffs, on me).
    private static bool HideOthersEffect(object ctx, long me)
    {
        if (!HideOthers) return false;
        if (!HideBuffs && (int)_src!(ctx) == SrcBuff) return false;
        if (!HideOnMe && _belong!(ctx) == me) return false;
        return true;
    }

    private const int RelWorld = 0, RelMine = 1, RelOther = 2, RelOtherSummon = 3;

    // Who cast it. A pet/bullet's owner is looked up only when the answer can matter: while mine, other players'
    // summons and monsters are all kept, every owner class keeps it, so it is reported as World without the lookup.
    // An owner that can't be resolved (entity not live) also counts as World — a monster's bullet, most often.
    private static int Relation(long from, long me)
    {
        if (from == 0) return RelWorld;
        if (from == me) return RelMine;
        int t = (int)_entType!(from);
        if (t == EntChar) return RelOther;
        if (t != EntPet && t != EntBullet && t != EntClientBullet) return RelWorld;
        if (!(HideOthers && HidePets) && !HideMine && !HideMonsters) return RelWorld;
        long owner = OwnerOf(from);
        if (owner == 0) return RelWorld;
        if (owner == me) return RelMine;
        return (int)_entType!(owner) == EntChar ? RelOtherSummon : RelWorld;
    }

    // Top summoner of a pet/bullet, read once per uuid (0 = none / entity not live — cached too, so a bullet that never
    // becomes a live entity costs one GetEntity, not one per effect).
    private static long OwnerOf(long uuid)
    {
        if (_ownerOf.TryGetValue(uuid, out long o)) return o;
        if (_ownerOf.Count >= MaxCache) _ownerOf.Clear();
        o = 0;
        var mgr = _mgr;
        if (mgr != null && _getEntity != null && _attrTop != null)
        {
            if (_diag) _ownerLookups++;
            var ent = _getEntity(mgr, uuid);
            if (ent != null)
            {
                o = _attrTop(ent);
                if (o == 0 && _attrSum != null) o = _attrSum(ent);
            }
        }
        _ownerOf[uuid] = o;
        return o;
    }

    // ── Diagnostics flush (plugin tick, every 10 s; nothing when diagnostics are off or nothing loaded) ──────────
    public static void FlushDiag(double windowSec)
    {
        if (!_diag || _calls == 0 || _log == null) return;
        double us = 1_000_000.0 / Stopwatch.Frequency;
        _log($"[EffFilter] {windowSec:0}s: calls={_calls} hid={_hides} cost avg={_costTicks * us / _calls:0.00}µs " +
             $"max={_costMax * us:0.0}µs ownerLookups={_ownerLookups} decided={_decided.Count} owners={_ownerOf.Count} " +
             $"errors={_errors} me={LocalUuid} fight={HiddenThisFight}");
        _calls = _hides = _errors = _ownerLookups = 0;
        _costTicks = _costMax = 0;
    }
}
