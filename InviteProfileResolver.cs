using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.MultiTool;

/// <summary>
/// Resolves a full player PROFILE — display name, level, and avatar photo — for a character uid, to back the
/// Add-player dialog's player-info card (the avatar + name + "Lv.60" layout the game's own profile popup shows).
/// This is the richer sibling of <see cref="InviteNameResolver"/>: that one feeds the compact list rows with just a
/// name; this one also pulls the level and downloads the avatar png. See Player-Name-Resolution.md.
///
/// Like the name resolver, there is no synchronous profile cache in the game and no "profile updated" event: the data
/// comes from the async <c>social</c> VM, delivered as a coroutine's return value. The profile needs TWO social calls
/// because name+level and the avatar url live under different data masks:
///   • <c>AsyncGetSocialDataTypeBasic(uid, token)</c> → <c>basicData.name</c> (string) + <c>basicData.level</c> (int).
///   • <c>AsyncGetAvatarInfo(uid, token)</c>          → <c>avatarInfo.profile.url</c> (plain https .png url; "" when
///     the player uses no custom photo).
/// Both run in one injected coroutine fn that appends a "<uid>\31name\31level\31url\30" record to a flush global this
/// C# side polls, parses and clears — the same inject-once / poll / clear pattern as InviteNameResolver.
///
/// The avatar url is then downloaded off-thread (a shared HttpClient on a Task) and the png bytes stored back into the
/// cached profile; the card reads <c>AvatarPng</c> on the main thread (may still be null while the download runs or if
/// the player has no custom photo). Nothing here ever throws onto the game thread.
///
/// ⚠️ The uid is int64 and can exceed 2^53. We inject it as a BARE INTEGER LITERAL (digits only — validated) so Lua
/// 5.3 keeps full precision; never quote or tonumber it. Every WRITE to a global goes through <c>rawset(_G, …)</c>
/// because the game guards <c>_G</c> with a strict <c>__newindex</c> that silently drops plain writes.
/// </summary>
internal sealed class InviteProfileResolver
{
    /// <summary>A resolved player profile. <see cref="AvatarPng"/> may still be null after <see cref="Resolved"/> is
    /// true — while the image downloads, or permanently when <see cref="Url"/> is empty (no custom photo).</summary>
    internal sealed class Profile
    {
        public string Name = "";
        public int    Level;
        public string Url  = "";
        public volatile byte[]? AvatarPng;   // written by the download Task, read on the main thread
        public object? AvatarTex;            // boxed Texture2D decoded from AvatarPng on the main thread (Poll); read by the UI
        public bool   AvatarDecodeTried;     // guard: a bad/failed PNG isn't re-decoded on every poll
        public bool   Resolved;
    }

    private const string ResolveFn   = "__stellar_inv_resolve_profile";
    private const string FlushGlobal = "__stellar_inv_profile_flush";
    private const string DiagGlobal  = "__stellar_inv_profile_diag";   // diag: coroutine step markers + any error, read in Poll

    private const char RecordSep = '\x1e';   // \30 between records
    private const char FieldSep  = '\x1f';   // \31 between fields

    private const float PollInterval    = 0.25f;  // same snappy drain/issue cadence as InviteNameResolver
    private const float InFlightTimeout = 10f;    // allow one retry if a resolve never produced a flush record
    private const float ReassertInterval = 3f;    // re-inject the resolver fn on this cadence — see InviteNameResolver
    private const int   MaxAttempts = 3;          // cap RPC retries for a uid that never flushes → terminal not-found

    private const int   PngMaxBytes = 5_000_000;  // reject an absurd payload; avatars are tiny

    // Generic default-avatar face, shown on the card when a player resolves with no custom photo OR as a terminal
    // not-found — so both states show a face instead of a blank box. Downloaded + decoded ONCE, shared by every card.
    private const string DefaultAvatarUrl = "https://bpm-sea-photo.17996cdn.net/avatar/default-avatar.png";

    // One client for all avatar downloads (socket reuse); short timeout so a dead url can't pile up.
    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    private readonly IPluginServices _services;

    private readonly Dictionary<string, Profile> _cache    = new();   // uid → resolved profile (terminal once set)
    private readonly Dictionary<string, float>   _inFlight = new();   // uid → monotonic issue time (dedup + retry)
    private readonly Dictionary<string, int>     _attempts = new();   // uid → retries issued past the timeout (cap guard)

    private readonly List<object> _textures = new();   // decoded avatar Texture2Ds (boxed), owned here → destroyed on Dispose

    // Shared default-avatar state (download once, decode once — see DefaultAvatarUrl). _defaultPng is written by the
    // one-shot download Task and read on the main thread; _defaultTex is the decoded Texture2D the cards fall back to.
    private volatile byte[]? _defaultPng;
    private object? _defaultTex;
    private bool    _defaultDownloadStarted;   // guard: start the shared download exactly once
    private bool    _defaultDecodeTried;        // guard: a bad/failed default PNG isn't re-decoded on every poll

    // The decoded default face (null until downloaded + decoded). The card reads this when a player has no custom photo.
    public object? DefaultAvatarTex => _defaultTex;

    private bool  _subscribed;
    private float _clock;         // accumulated dt — session-monotonic clock for the in-flight timeout compare
    private float _pollTimer;
    private float _reassertTimer; // accumulated dt toward the next InjectResolver re-assert (in-world only)

    public InviteProfileResolver(IPluginServices services) => _services = services;

    // Inject the resolver fn (idempotent) + ensure the flush global exists, and start polling. Safe before login — the
    // Lua no-ops until the social VM exists, and a resolve is only issued on demand while in-world.
    public void Install()
    {
        if (!_subscribed) { _services.Framework.Update += OnUpdate; _subscribed = true; }
        InjectResolver();
    }

    public void Dispose()
    {
        if (_subscribed) { try { _services.Framework.Update -= OnUpdate; } catch { } _subscribed = false; }
        // Destroy the avatar textures we created (main thread; Dispose runs on the game thread via the plugin teardown).
        foreach (var t in _textures) { try { UnityEngine.Object.Destroy((UnityEngine.Object)t); } catch { } }
        _textures.Clear();
    }

    // null = not resolved yet (card shows "Looking up…"); else the profile (AvatarPng may still be null).
    public Profile? TryGet(string uid)
        => (uid != null && _cache.TryGetValue(uid, out var p)) ? p : null;

    // On-demand resolve: same in-world gate as InviteNameResolver.Request. Skips a cached or in-flight uid, so pressing
    // Look up repeatedly is cheap. The result lands in the permanent session cache (TryGet).
    public void Request(string uid)
    {
        EnsureDefaultAvatar();   // kick the shared default-face download the first time anyone looks a player up (plain HTTP, no world gate)
        if (string.IsNullOrEmpty(uid) || !AllDigits(uid)) return;
        if (_cache.ContainsKey(uid)) return;
        if (_inFlight.TryGetValue(uid, out var started) && (_clock - started) < InFlightTimeout) return;
        if (_services.ClientState.Phase != GamePhase.World) return;   // RPC needs a logged-in session
        InjectResolver();          // define the fn NOW so this issue isn't a nil-call (Install ran pre-Ready)
        IssueResolve(uid);
        _inFlight[uid] = _clock;
    }

    private void OnUpdate(float dt)
    {
        _clock += dt;

        // Re-assert the resolver fn on a slow timer whenever in-world — the ACTUAL fix for "rpc issue but no resolve":
        // Install() runs pre-Lua-Ready so its inject no-ops → the fn was never defined. InjectResolver is idempotent
        // (rawget guard) and re-defines after a scene reload wipes globals. Runs before the poll gate so the poll
        // cadence never starves it. Mirrors InviteNameResolver.OnUpdate.
        if (_services.ClientState.Phase == GamePhase.World)
        {
            _reassertTimer += dt;
            if (_reassertTimer >= ReassertInterval) { _reassertTimer = 0f; InjectResolver(); }
        }

        _pollTimer += dt;
        if (_pollTimer < PollInterval) return;
        _pollTimer = 0f;
        try { Poll(); }
        catch (Exception ex) { _services.Log.Warning($"[InviteProfile] poll error: {ex.Message}"); }
    }

    private void Poll()
    {
        var flush = _services.Lua.ReadGlobalString(FlushGlobal);
        if (!string.IsNullOrEmpty(flush))
        {
            foreach (var rec in flush.Split(RecordSep))
            {
                if (rec.Length == 0) continue;   // trailing empty after the final \30
                var f = rec.Split(FieldSep);
                if (f.Length < 4) continue;      // guard against a short/partial record
                string uid = f[0];
                if (uid.Length == 0) continue;
                var p = new Profile
                {
                    Name     = f[1],
                    Level    = int.TryParse(f[2], out var lv) ? lv : 0,
                    Url      = f[3],
                    Resolved = true,
                };
                _cache[uid]  = p;
                _inFlight.Remove(uid);
                _attempts.Remove(uid);   // success clears the retry count so a later re-request can retry fresh
                _services.Log.Info($"[InviteProfile] {uid} name='{p.Name}' lv={p.Level} url={(p.Url.Length > 0 ? "yes" : "none")}");

                if (p.Url.Length > 0) StartAvatarDownload(uid, p.Url, p);
            }

            // Clear for the next batch (guarded write — same as InviteNameResolver.Poll).
            _services.Lua.DoString("pcall(function() rawset(_G,'" + FlushGlobal + "','') end)");
        }

        // Diagnostic: drain the coroutine step/error markers the injected resolver records (the xpcall error handler
        // swallows everything, so this is the only window into WHERE the two social RPC calls fail). Log + clear. Runs
        // even when flush is empty — a coroutine that never flushes still leaves its diag trail here.
        var diag = _services.Lua.ReadGlobalString(DiagGlobal);
        if (!string.IsNullOrEmpty(diag))
        {
            _services.Log.Warning("[InviteProfile][diag] " + diag);
            _services.Lua.DoString("pcall(function() rawset(_G,'" + DiagGlobal + "','') end)");
        }

        // Decode sweep (MAIN THREAD — Texture2D creation is main-thread-only; the download Task only stores the bytes).
        // ImageElement decodes its PNG once at build time, so an avatar that arrives AFTER the card is built never shows;
        // we decode to a Texture2D here and the card renders it with GameTextureElement (re-pulls its texture per frame).
        // Cache is tiny (on-demand dialog lookups) so a full scan each poll is cheap. AvatarDecodeTried guards a bad PNG
        // from re-decoding every poll.
        foreach (var kv in _cache)
        {
            var p = kv.Value;
            if (p.AvatarPng is not { Length: > 0 } bytes || p.AvatarTex != null || p.AvatarDecodeTried) continue;
            p.AvatarDecodeTried = true;
            var uid = kv.Key;
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true) {
                    filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                if (ImageConversion.LoadImage(tex, bytes)) { tex.Apply(true, false); p.AvatarTex = tex; _textures.Add(tex);
                    _services.Log.Info($"[InviteProfile] avatar tex ready {uid}"); }
                else { UnityEngine.Object.Destroy(tex); _services.Log.Warning($"[InviteProfile] avatar decode failed {uid}"); }
            }
            catch (Exception ex) { _services.Log.Warning($"[InviteProfile] avatar decode error {uid}: {ex.Message}"); }
        }

        // Default-avatar decode (OUTSIDE the cache loop so it runs even when the cache is empty — the first lookup kicks
        // the download in Request before anything lands in _cache). Same download-once / decode-once idiom as above,
        // shared by every card via DefaultAvatarTex. _defaultDecodeTried guards a bad PNG from re-decoding each poll.
        if (_defaultPng is { Length: > 0 } dbytes && _defaultTex == null && !_defaultDecodeTried)
        {
            _defaultDecodeTried = true;
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true) {
                    filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                if (ImageConversion.LoadImage(tex, dbytes)) { tex.Apply(true, false); _defaultTex = tex; _textures.Add(tex);
                    _services.Log.Info("[InviteProfile] default avatar tex ready"); }
                else { UnityEngine.Object.Destroy(tex); _services.Log.Warning("[InviteProfile] default avatar decode failed"); }
            }
            catch (Exception ex) { _services.Log.Warning($"[InviteProfile] default avatar decode error: {ex.Message}"); }
        }

        // Retry/cap sweep. Profile resolves are on-demand (Request), so unlike InviteNameResolver there's no wanted-set
        // to re-scan — the only uids still in flight are the ones a Look up issued that haven't flushed yet. This is the
        // profile analog of InviteNameResolver.IssuePendingResolves' timeout-retry, driven from Poll. Only in-world.
        if (_services.ClientState.Phase != GamePhase.World) return;
        ReissueTimedOut();
    }

    // Reissue each in-flight uid past the in-flight timeout, capped at MaxAttempts. Past the cap a uid that still won't
    // flush is cached as a TERMINAL not-found Profile (the same shape a genuine empty lookup produces) and dropped from
    // in-flight/attempts, so the card leaves the "Looking up…" state instead of spinning forever. Snapshots the keys
    // first because it mutates _inFlight while iterating.
    private void ReissueTimedOut()
    {
        List<string>? due = null;
        foreach (var kv in _inFlight)
            if ((_clock - kv.Value) >= InFlightTimeout) (due ??= new List<string>()).Add(kv.Key);
        if (due == null) return;

        foreach (var uid in due)
        {
            int n = _attempts.TryGetValue(uid, out var a) ? a : 0;
            if (n >= MaxAttempts)
            {
                _cache[uid] = new Profile { Resolved = true, Name = "", Level = 0, Url = "" };
                _inFlight.Remove(uid);
                _attempts.Remove(uid);
                continue;
            }
            _attempts[uid] = n + 1;
            IssueResolve(uid);
            _inFlight[uid] = _clock;
        }
    }

    // Fetch the avatar png off the game thread and store the bytes back into the profile. Accepts only a real PNG
    // (magic bytes 89 50 4E 47) under the size cap. Never throws out of the Task.
    private void StartAvatarDownload(string uid, string url, Profile p)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                if (bytes == null || bytes.Length < 4 || bytes.Length >= PngMaxBytes
                    || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47)
                {
                    _services.Log.Warning($"[InviteProfile] avatar {uid}: not a PNG or too large ({bytes?.Length ?? 0} bytes)");
                    return;
                }
                p.AvatarPng = bytes;   // simple reference assign; field is volatile, read on the main thread
                _services.Log.Info($"[InviteProfile] avatar {uid}: {bytes.Length} bytes");
            }
            catch (Exception ex)
            {
                _services.Log.Warning($"[InviteProfile] avatar {uid}: download failed: {ex.Message}");
            }
        });
    }

    // Fetch the shared default-avatar png off the game thread ONCE (guarded by _defaultDownloadStarted). Same shared
    // Http client, same PNG magic-byte + size validation as StartAvatarDownload; stores bytes into the volatile
    // _defaultPng for Poll to decode on the main thread. Plain HTTP, so no world gate. Never throws out of the Task.
    private void EnsureDefaultAvatar()
    {
        if (_defaultDownloadStarted) return;
        _defaultDownloadStarted = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(DefaultAvatarUrl).ConfigureAwait(false);
                if (bytes == null || bytes.Length < 4 || bytes.Length >= PngMaxBytes
                    || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47)
                {
                    _services.Log.Warning($"[InviteProfile] default avatar: not a PNG or too large ({bytes?.Length ?? 0} bytes)");
                    return;
                }
                _defaultPng = bytes;   // simple reference assign; field is volatile, read on the main thread
                _services.Log.Info($"[InviteProfile] default avatar: {bytes.Length} bytes");
            }
            catch (Exception ex)
            {
                _services.Log.Warning($"[InviteProfile] default avatar: download failed: {ex.Message}");
            }
        });
    }

    // Call the injected resolver with the uid as a bare integer literal (digits only — validated) so int64 precision
    // is preserved. Guarded so a pre-login / missing-fn call is a silent no-op.
    private void IssueResolve(string uid)
    {
        _services.Log.Info($"[InviteProfile] rpc issue {uid}");
        _services.Lua.DoString(
            "pcall(function() if " + ResolveFn + " then " + ResolveFn + "(" + uid + ") end end)");
    }

    // Define the resolver fn once + ensure the flush + diag globals exist. The fn runs BOTH social calls inside one
    // coroutine (Base mask for name+level, Avatar mask for the url) with a single Rent()'d CancelSource and a fresh
    // token per call, then appends "<uid>\31name\31level\31url\30" to the flush global. It writes NOTHING to flush when
    // the social VM isn't ready (pre-login) so the uid stays in-flight and is retried. DIAGNOSTIC: each of the TWO
    // social calls is pcall'd separately and every step/error recorded into the diag global (the xpcall error handler
    // otherwise swallows all failures), drained in Poll. All global writes use rawset and reads use rawget(_G,…)
    // (strict _G __newindex guard). Mirrors InviteNameResolver.InjectResolver.
    private void InjectResolver() => _services.Lua.DoString(@"
pcall(function()
  if rawget(_G,'" + ResolveFn + @"')==nil then
    rawset(_G,'" + ResolveFn + @"', function(uid)
      local function d(t) rawset(_G,'" + DiagGlobal + @"',(rawget(_G,'" + DiagGlobal + @"') or '')..tostring(t)..' | ') end
      Z.CoroUtil.create_coro_xpcall(function()
        d('start '..tostring(uid))
        local social = Z.VMMgr.GetVM('social')
        if not social then d('NO social VM') return end
        d('basicFn='..type(social.AsyncGetSocialDataTypeBasic)..' avatarFn='..type(social.AsyncGetAvatarInfo))
        local cs = Z.CancelSource.Rent()
        local ok1, basic = pcall(function() return social.AsyncGetSocialDataTypeBasic(uid, cs:CreateToken()) end)
        d('basic ok='..tostring(ok1)..' nil='..tostring(basic==nil)..(ok1 and '' or (' err='..tostring(basic))))
        local name  = (basic and basic.basicData and basic.basicData.name) or ''
        local level = (basic and basic.basicData and basic.basicData.level) or 0
        local ok2, av = pcall(function() return social.AsyncGetAvatarInfo(uid, cs:CreateToken()) end)
        d('avatar ok='..tostring(ok2)..' nil='..tostring(av==nil)..(ok2 and '' or (' err='..tostring(av))))
        cs:Recycle()
        local url = (av and av.avatarInfo and av.avatarInfo.profile and av.avatarInfo.profile.url) or ''
        d('name='..tostring(name)..' lv='..tostring(level)..' urlLen='..tostring(#url))
        rawset(_G,'" + FlushGlobal + @"',(rawget(_G,'" + FlushGlobal + @"') or '')..tostring(uid)..'\31'..name..'\31'..tostring(level)..'\31'..url..'\30')
        d('flushed')
      end, function(e) d('XPCALL ERR '..tostring(e)) end)()
    end)
  end
  if rawget(_G,'" + FlushGlobal + @"')==nil then rawset(_G,'" + FlushGlobal + @"','') end
  if rawget(_G,'" + DiagGlobal + @"')==nil then rawset(_G,'" + DiagGlobal + @"','') end
end)");

    private static bool AllDigits(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (char c in s) if (c < '0' || c > '9') return false;
        return true;
    }
}
