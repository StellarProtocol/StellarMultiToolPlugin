using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.MultiTool;

// ONE "Auto-Accept" button on the game's native player profile card (idcard popup) that opens the per-player
// auto-accept dialog (Plugin.InviteCardDialog.cs) for the carded player — Party / Social switches. Registered
// through the generic IProfileCardActions service (same mechanism as EntityInspector's "Inspect"): the framework
// injects + styles the button and calls OnClick on the main thread with the carded player's EntityId.
//
// The button is shown regardless of the auto-accept toggles — curating the roster while a section is off is
// legitimate (a switch on a disabled section is stored but inert until enabled). Pressing it always lists the player
// (EnsureInviteListed: roster-only, both switches off, if not already listed — an already-listed person is left as is)
// but never turns a rule on by itself; only flipping a switch in the dialog does, and that dialog IS the feedback (no
// toasts on this path).
public sealed partial class Plugin
{
    private readonly List<IDisposable> _cardActions = new();

    private const int CardIconPx = 176;   // 4 × the ~44px on-screen footprint — supersampled (EntityInspector precedent)

    // Must run AFTER InitInviteAccept: the dialog, the lists, the name resolver and the save paths are set up there.
    private void InitProfileCardAdd()
    {
        // Icon baked once, here, on the main thread.
        var icon = BuildPersonPlusPng();
        _cardActions.Add(_services.ProfileCardActions.Register(new ProfileCardActionSpec(
            "multitool_autoaccept", "Auto-Accept", icon, OnCardAutoAccept)));
    }

    private void DisposeProfileCardAdd()
    {
        foreach (var a in _cardActions) { try { a.Dispose(); } catch { } }
        _cardActions.Clear();
    }

    private void OnCardAutoAccept(EntityId entity)
    {
        if (!entity.IsPlayer) return;
        // EntityId.Uid (upper 48 bits) == charId — the same decimal identity the invite payloads carry and the Add
        // dialog stores (charId = uuid >> 16), so a card-opened uid matches a typed one.
        var uid = entity.Uid.ToString();

        // Seed the name up front: the carded player is on screen, so CombatLookup usually knows it, and the dialog
        // title + the new row's PushWantedNames then need no RPC. An empty/unknown name is not seeded — that would
        // poison the resolver cache and block the real async lookup — so ask the resolver for it instead. Seeded
        // BEFORE EnsureInviteListed so its SetWanted already finds the name cached.
        string? name = null;
        try { name = _services.CombatLookup.GetEntityName(entity); } catch { }
        if (!string.IsNullOrEmpty(name)) _nameResolver.Seed(uid, name!);
        else _nameResolver.Request(uid);

        EnsureInviteListed(uid);
        OpenInviteCardDialog(uid);

        // Dismiss the native card now that our dialog has taken over (uid/name were read above, before it goes). Posted
        // to the next tick so the close never runs inside the framework's click dispatch for the card's own injected
        // button (tearing the card view down mid-dispatch). Post queues every call, which is fine: the close is
        // idempotent. Fully guarded on both sides — a failed close must never throw into the tick or affect the dialog.
        try { _services.Framework.Post(CloseNativeIdCard); } catch { }
    }

    // Z.VMMgr.GetVM("idcard").CloseIdCardView() — the game's own close path for the profile card (validated in-game).
    // Lua-guarded (nil VM / missing method → no-op) and pcall'd so a Lua error can't cross the interop trampoline.
    private void CloseNativeIdCard()
    {
        try
        {
            _services.Lua.DoString(
                "pcall(function() local vm = Z.VMMgr.GetVM('idcard'); if vm and vm.CloseIdCardView then vm.CloseIdCardView() end end)");
        }
        catch { }
    }

    // ── icon ──────────────────────────────────────────────────────────────────────────────────────────────────────
    // Bake the "person + plus" glyph and return PNG bytes for ProfileCardActionSpec.IconPng (the framework rasterises
    // it back via its own icon cache). The transient texture is destroyed after encoding.
    private static byte[]? BuildPersonPlusPng()
    {
        Texture2D? tex = null;
        try
        {
            tex = BakePersonPlus();
            return ImageConversion.EncodeToPNG(tex);
        }
        catch { return null; }   // label-only button is an acceptable fallback
        finally { if (tex != null) UnityEngine.Object.Destroy(tex); }
    }

    // White glyph, alpha = coverage (the framework's RawImage tints it), soft 1.5px distance-band edge so it scales
    // cleanly. Person (head disc + shoulders half-disc) left-of-centre, a "+" in the upper right. Origin bottom-left.
    private static Texture2D BakePersonPlus()
    {
        const int n = CardIconPx;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, mipChain: false);
        var px  = new Color[n * n];

        var headC = new Vector2(n * 0.38f, n * 0.62f);
        float headR = n * 0.16f;
        var bodyC = new Vector2(n * 0.38f, n * 0.14f);   // shoulders = upper half of this disc
        float bodyR = n * 0.28f;
        var plusC = new Vector2(n * 0.78f, n * 0.66f);
        float plusL = n * 0.15f;                          // arm half-length
        float plusW = n * 0.045f;                         // arm half-width

        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            var p = new Vector2(x + 0.5f, y + 0.5f);
            float head = (p - headC).magnitude - headR;
            // Half-disc: inside the circle AND above its centre line (max of the two signed distances = intersection).
            float body = Mathf.Max((p - bodyC).magnitude - bodyR, bodyC.y - p.y);
            float d    = Mathf.Min(head, body);

            var q = p - plusC;
            float hBar = Mathf.Max(Mathf.Abs(q.x) - plusL, Mathf.Abs(q.y) - plusW);
            float vBar = Mathf.Max(Mathf.Abs(q.x) - plusW, Mathf.Abs(q.y) - plusL);
            d = Mathf.Min(d, Mathf.Min(hBar, vBar));

            px[y * n + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - d / 1.5f));
        }
        tex.SetPixels(px);
        tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
        return tex;
    }
}
