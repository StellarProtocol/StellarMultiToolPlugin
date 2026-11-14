using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

/// <summary>
/// Instant Dialog ("No text animation", Language &amp; Text window): completes the NPC talk typewriter on its FIRST tick.
/// Ported from the in-game-validated Experiment patch (Knowledge Base\NPC-Dialog-Typewriter.md), minus its Lua half.
/// Speed alone can never make it instant — <c>Panda.ZUi.ZTMPText.UpdateWidget(float)</c> (called once per frame by
/// <c>ZUiRoot.Update</c> for registered updaters, returning true unregisters) resets its char timer on every advance, so
/// it reveals AT MOST one character per frame whatever the speed/timescale.
/// <para>
/// Two Harmony PREFIXES (plain value/reference params — no <c>in</c> struct, so no trampoline crash):
/// <list type="bullet">
/// <item><c>DoTextByPreSec(string, int preSecCount, uint token)</c> — marks the label (native pointer) when
/// <c>preSecCount</c> is 40, the talk dialog's stock CONTENT_PLAYBACK_SPEED (<c>talk_dialog_window_view.lua</c>).
/// <c>noticetip_captions_view.lua</c> types at 20 → never marked, and any other speed un-marks a reused label. Those
/// are the only two Lua callers in release_3.7.</item>
/// <item><c>UpdateWidget(float)</c> — for a marked label with <c>State == Outputting &amp;&amp; openTypewrite_</c> (the
/// same guard the native completion uses): <c>State = Completed</c>, then the game's own private
/// <c>OnOutputEnd(true)</c> (all char alphas 255 + visible count, openTypewrite_ = 0, typeScale = 1, fires
/// typerCompleteEvent_ → the dialog's next-arrow + EPFlow show-end + auto-play, exactly once), and return true so
/// ZUiRoot unregisters the updater. With State/openTypewrite_ cleared the native completion can't fire it again.</item>
/// </list>
/// Dead ends (NPC-Dialog-Typewriter.md): <c>OpenTypewrite = false</c> is overwritten; <c>FadeRange = 0</c> = invisible
/// text; calling OnOutputEnd from a DoTextByPreSec postfix is too early (the async OutputCharactersFading assigns the
/// text + zeroes alphas ~2 frames later).
/// </para>
/// Patched ONCE at plugin start regardless of the setting, so the toggle applies live on the next line; the setting only
/// flips <see cref="Enabled"/>. Perf (Plugin-Performance-Rules.md §7): UpdateWidget only runs for a ZTMPText that is
/// currently typing (registered updater) — not a whole-UI per-frame hook — and the prefix's first statement is the
/// <see cref="Enabled"/> check, so Off costs one static bool read. On is O(1): one HashSet lookup on the native pointer,
/// then compiled expression-tree accessors bound once at install (no Invoke/GetValue, no boxing). DoTextByPreSec runs
/// once per typed line (cold). IHarmonyHost unpatches on plugin dispose.
/// </summary>
internal static class InstantDialogPatch
{
    private const int TalkSpeed = 40;   // talk_dialog_window_view.lua CONTENT_PLAYBACK_SPEED (captions use 20)
    private const int StateCompleted = 1, StateOutputting = 2;   // Panda.ZUi.TypewriterState

    /// <summary>The user setting ("instant_dialog_enabled"); read first thing in the UpdateWidget prefix.</summary>
    public static volatile bool Enabled;
    /// <summary>Both prefixes are in place and the private members bound — false = "Unavailable" in the UI.</summary>
    public static bool Available { get; private set; }

    // Native ZTMPText pointers whose current DoTextByPreSec is a talk-dialog line. Pointers are stable (IL2CPP's GC is
    // non-moving); a label is re-marked/un-marked on every DoTextByPreSec, and un-marked when we complete it.
    private static readonly HashSet<IntPtr> _marked = new();

    private static Func<object, int>?    _getState;
    private static Action<object, int>?  _setState;
    private static Func<object, bool>?   _getOpen;
    private static Action<object, bool>? _onOutputEnd;
    private static Action<string>? _log;
    private static bool _installed, _loggedFirst, _broken;

    public static bool Install(Harmony harmony, Action<string> log)
    {
        if (_installed) return Available;
        _installed = true;
        _log = log;

        var t = StellarInterop.FindType("Panda.ZUi.ZTMPText");
        if (t == null) { log("[InstantDialog] ZTMPText not found — unavailable"); return false; }
        if (!Bind(t)) return false;

        var doText = StellarInterop.FindMethod(t, "DoTextByPreSec", 3);
        var update = StellarInterop.FindMethod(t, "UpdateWidget", 1);
        if (doText == null || update == null)
        {
            log($"[InstantDialog] method not found (DoTextByPreSec={doText != null}, UpdateWidget={update != null}) — unavailable");
            return false;
        }
        try
        {
            harmony.Patch(doText, prefix: new HarmonyMethod(typeof(InstantDialogPatch), nameof(PrefixDoTextByPreSec)));
            harmony.Patch(update, prefix: new HarmonyMethod(typeof(InstantDialogPatch), nameof(PrefixUpdateWidget)));
            Available = true;
            log("[InstantDialog] patched ZTMPText.DoTextByPreSec + UpdateWidget (prefix)");
            return true;
        }
        catch (Exception ex)
        {
            log($"[InstantDialog] patch failed: {ex.InnerException?.Message ?? ex.Message} — unavailable");
            return false;
        }
    }

    // Il2CppInterop surfaces the private fields State / openTypewrite_ as properties and the private OnOutputEnd as a
    // method of the same name; reflect them once and compile typed accessors (expression trees work for IL2CPP proxy
    // members — Plugin-Performance-Rules.md). The raw openTypewrite_ field is read rather than the OpenTypewrite
    // property so no getter logic runs.
    private static bool Bind(Type t)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var state = t.GetProperty("State", F);
        var open  = t.GetProperty("openTypewrite_", F);
        MethodInfo? end = null;
        foreach (var m in t.GetMethods(F))
            if (m.Name == "OnOutputEnd" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(bool)) { end = m; break; }
        if (state == null || open == null || end == null || !state.CanWrite)
        {
            _log?.Invoke($"[InstantDialog] member not found (State={state != null}/{state?.CanWrite}, openTypewrite_={open != null}, OnOutputEnd={end != null}) — unavailable");
            return false;
        }
        try
        {
            var o = Expression.Parameter(typeof(object), "o");
            var inst = Expression.Convert(o, t);
            var i = Expression.Parameter(typeof(int), "i");
            var b = Expression.Parameter(typeof(bool), "b");
            _getState = Expression.Lambda<Func<object, int>>(
                Expression.Convert(Expression.Property(inst, state), typeof(int)), o).Compile();
            _setState = Expression.Lambda<Action<object, int>>(
                Expression.Assign(Expression.Property(inst, state), Expression.Convert(i, state.PropertyType)), o, i).Compile();
            _getOpen = Expression.Lambda<Func<object, bool>>(Expression.Property(inst, open), o).Compile();
            _onOutputEnd = Expression.Lambda<Action<object, bool>>(Expression.Call(inst, end, b), o, b).Compile();
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[InstantDialog] accessor bind failed: {ex.Message} — unavailable");
            return false;
        }
    }

    // __1 = preSecCount. Runs once per typed line (cold). Marks even while Off, so turning the setting on mid-line
    // completes the line already typing.
    private static void PrefixDoTextByPreSec(object __instance, int __1)
    {
        if (_broken || __instance is not Il2CppObjectBase ob) return;
        var p = ob.Pointer;
        if (__1 == TalkSpeed)
        {
            if (_marked.Count > 64) _marked.Clear();   // safety valve: destroyed labels that never typed again
            _marked.Add(p);
        }
        else _marked.Remove(p);
    }

    // Runs once per frame per TYPING ZTMPText. Not yet Outputting (the async OutputCharactersFading hasn't set State 2)
    // → let the native tick run and keep the mark for the next frame.
    private static bool PrefixUpdateWidget(object __instance, ref bool __result)
    {
        if (!Enabled) return true;   // Off: O(1) exit before any lookup
        if (_marked.Count == 0 || _broken || __instance is not Il2CppObjectBase ob) return true;
        var p = ob.Pointer;
        if (!_marked.Contains(p)) return true;
        try
        {
            if (_getState!(__instance) != StateOutputting || !_getOpen!(__instance)) return true;
            _marked.Remove(p);
            _setState!(__instance, StateCompleted);   // BEFORE OnOutputEnd: it fires typerCompleteEvent_ with the current State
            _onOutputEnd!(__instance, true);
            __result = true;                          // unregister the updater, like the native completion
            if (!_loggedFirst) { _loggedFirst = true; _log?.Invoke("[InstantDialog] first dialog line completed instantly"); }
            return false;
        }
        catch (Exception ex)
        {
            // Never break the game's typer: on any failure stop intervening for the session; native typing continues.
            _broken = true;
            Available = false;
            _marked.Clear();
            _log?.Invoke($"[InstantDialog] UpdateWidget prefix failed, turned off for this session: {ex.InnerException?.Message ?? ex.Message}");
            return true;
        }
    }
}
