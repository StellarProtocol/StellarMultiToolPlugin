using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// Bounded "re-centre until laid out" for a hidden, auto-height dialog window. Begin() right after SetVisible(true);
// it then centres the window on a target point for a short per-tick window and stops by itself. Used by the
// per-player card dialog (screen centre) and the Clear-all confirm (centre of the main Auto-Accept window).
//
// Manual centre, NOT WindowAnchor.Center: the framework's anchor centres on the DECLARED DefaultRect height, and these
// are auto-height (Height 0) windows, so it would pin the top at the target's middle. Centre on the window's actual
// laid-out win.Rect instead, in CANVAS units (IFramework.CanvasWidth/Height — ScreenWidth is physical px and is off by
// the UI scaleFactor away from native res). WindowBuilder-Patterns.md §"Centering a plugin window correctly".
//
// Why a short per-tick window instead of one SetRect: right after SetVisible(true) the rect can be 0 / stale (not laid
// out yet), and the height can change a tick later (e.g. a title wrapping once a name resolves). So keep re-centring
// while the computed target rect moves (own size OR the target point), and stop once it has held for StableTicks (or
// the budget runs out). Bounded on purpose: SetRect auto-persists, so it must not run every tick for the dialog's
// lifetime (KB §"A Draggable window = Anchor + PersistIfSettled"), and after it stops the user's drag is left alone
// until the next Begin().
internal sealed class WindowCenterer
{
    private const int Budget      = 30;   // give up after this many ticks (layout normally settles in 1–3)
    private const int StableTicks = 3;    // done once the centred rect has held this many ticks

    private readonly IFramework     _fw;
    private readonly IWindowControl _win;
    // Target centre point in CANVAS units, or null when not resolvable this tick (skip, keep trying). Receives the
    // current canvas dims so a screen-centre target needs no extra lookups.
    private readonly Func<float, float, (float X, float Y)?> _target;

    private int   _ticks, _held;
    private float _lastX = float.NaN, _lastY = float.NaN, _lastW, _lastH;
    private bool  _subscribed;
    private bool  _seenShown;   // IsShown has been true at least once since Begin() (i.e. the window has mounted)

    public WindowCenterer(IFramework fw, IWindowControl win, Func<float, float, (float X, float Y)?> target)
    {
        _fw = fw; _win = win; _target = target;
    }

    // Screen (canvas) centre — the default target.
    public static (float X, float Y)? CanvasCentre(float cw, float ch) => (cw / 2f, ch / 2f);

    public void Begin()
    {
        _ticks = Budget;
        _held  = 0;
        _lastX = _lastY = float.NaN;   // force a centre even if the rect matches the previous open
        _seenShown = false;
        if (!_subscribed) { _fw.Update += OnTick; _subscribed = true; }
    }

    public void Stop()
    {
        if (_subscribed) { try { _fw.Update -= OnTick; } catch { } }
        _subscribed = false;
        _ticks = 0;
    }

    private void OnTick(float _)
    {
        if (--_ticks < 0) { Stop(); return; }

        // IsShown = "mounted AND Visible". On a window's FIRST open it is not mounted yet: SetVisible(true) only sets
        // the flag, and WindowService mounts it in the framework's Band 3 (global-rate) tick — which runs AFTER the
        // plugin Update band (Band 2) in the same beat. So the first OnTick(s) after Begin() see IsShown == false for
        // a window that IS opening. Stopping on that (the old `|| !IsShown`) killed the centerer before it ever ran →
        // the window stayed at its DefaultRect / saved rect. Later opens dodged it only because a hidden window stays
        // mounted (parked), and SetRect auto-persists the centred pose. So: wait (within the budget) until it mounts,
        // and only treat "not shown" as "closed mid-settle → stop" once it has actually been seen shown.
        if (!_win.IsShown) { if (_seenShown) Stop(); return; }
        _seenShown = true;

        var r = _win.Rect;
        if (r.Width <= 1f || r.Height <= 1f) return;                     // not laid out yet
        float cw = _fw.CanvasWidth, ch = _fw.CanvasHeight;
        if (cw <= 1f || ch <= 1f) return;                                // canvas metrics not ready
        if (_target(cw, ch) is not { } c) return;                        // target not resolvable yet

        // Clamp to the canvas: low bound last so a window larger than the canvas pins to the top-left (same as the
        // old Max(0, …) screen centre).
        float x = MathF.Max(0f, MathF.Min(c.X - r.Width  / 2f, cw - r.Width));
        float y = MathF.Max(0f, MathF.Min(c.Y - r.Height / 2f, ch - r.Height));

        if (x == _lastX && y == _lastY && r.Width == _lastW && r.Height == _lastH)
        {
            if (++_held >= StableTicks) Stop();
            return;
        }
        _win.SetRect(new WindowRect(x, y, r.Width, r.Height));
        _lastX = x; _lastY = y; _lastW = r.Width; _lastH = r.Height;
        _held = 0;
    }
}
