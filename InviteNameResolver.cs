using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

/// <summary>
/// Resolves player display names for the character uids shown in the invite auto-accept lists, so a row can read
/// "18434296 (SixSeven)" instead of a bare id. Names come from the async <c>social</c> RPC
/// (<c>AsyncGetSocialDataTypeBasic</c>) — the ONLY general by-id lookup that also covers ABSENT players (a uid the
/// user simply typed in). See Player-Name-Resolution.md.
///
/// There is no synchronous name cache in the game and no "name updated" event: every lookup is a coroutine RPC whose
/// result is delivered as the coro's return value. So the pattern is: inject a Lua resolver function once, call it per
/// wanted uid, and have it append the result to a flat flush global that this C# side polls, parses and clears. We
/// cache <c>uid→name</c> in-memory for the session (NOT persisted — keeps names fresh, adds no config key). A uid
/// resolves either to a real name or to <c>""</c> (completed-but-not-found) and then stops retrying — the RPC always
/// completes, even for a bad id, so <c>""</c> is a terminal state, not an error.
///
/// ⚠️ The uid is int64 and can exceed 2^53. When issuing a resolve we inject the uid as a BARE INTEGER LITERAL (digits
/// only — the lists are already all-digit, and we re-check) so Lua 5.3 parses it as a 64-bit integer and keeps full
/// precision; never quote it or <c>tonumber</c> it. Reads of the flush global are plain string reads; every WRITE to a
/// global goes through <c>rawset(_G, …)</c> because the game guards <c>_G</c> with a strict <c>__newindex</c> that
/// silently drops plain writes (Lua-Injection-from-CSharp.md).
///
/// The same flush global + record format is ALSO fed opportunistically by the invite wraps (InviteAutoAccept) when a
/// payload already carries a name (party/vehicle) — a free name with no RPC. This poller picks those up identically.
/// </summary>
internal sealed class InviteNameResolver
{
    // Idempotent resolver fn + the shared flush global. Record format: "<uid>\31<name>\30" appended repeatedly; the
    // poller splits on \30 (records) then \31 (uid, name). These names are shared with InviteAutoAccept's free-name
    // appends — keep them byte-identical there.
    private const string ResolveFn   = "__stellar_inv_resolve_name";
    private const string FlushGlobal = "__stellar_inv_name_flush";
    private const string DiagGlobal  = "__stellar_inv_name_diag";   // diag: coroutine step markers + any error, read in Poll

    private const char RecordSep = '\x1e';   // \30 between records
    private const char FieldSep  = '\x1f';   // \31 between uid and name

    private const float PollInterval   = 0.25f; // snappy flush-drain + resolve-issue cadence (~4x faster than the old 1s)
    private const float InFlightTimeout = 10f;  // allow one retry if a resolve never produced a flush record
    private const int   MaxResolvesPerPass = 6; // cap new RPCs per pass so a big pasted list doesn't burst; the rest
                                                // catch up on later ticks (now only ~0.25s apart)
    private const float ReassertInterval = 3f;  // re-inject the resolver fn on this cadence — Install() ran before Lua
                                                // was Ready (so its one-shot inject no-opped) and scene loads can wipe
                                                // _G; same reason InviteAutoAccept re-asserts its wraps on ~3s
    private const int   MaxAttempts = 3;        // cap RPC retries for a uid that never flushes → then mark terminal ""

    private readonly IPluginServices _services;

    // uid → name ("" = resolved-but-no-name, terminal). Absent key = unknown-yet.
    private readonly Dictionary<string, string> _cache    = new();
    // uid → monotonic time the resolve was issued; cleared when a flush record arrives or on timeout retry.
    private readonly Dictionary<string, float>   _inFlight = new();
    // uid → count of RPC retries issued past the in-flight timeout; cleared on a flush or when a uid goes terminal.
    private readonly Dictionary<string, int>     _attempts = new();
    // The union of uids the UI currently wants names for (pushed via SetWanted on every list change).
    private readonly HashSet<string>             _wanted   = new();

    private bool  _subscribed;
    private float _clock;         // accumulated dt — a session-monotonic clock for the in-flight timeout compare
    private float _pollTimer;
    private float _reassertTimer; // accumulated dt toward the next InjectResolver re-assert (in-world only)

    public InviteNameResolver(IPluginServices services) => _services = services;

    // Inject the resolver fn (idempotent) + ensure the flush global exists, and start polling. Safe before login — the
    // Lua no-ops until Ready, and the resolve issue is gated on being in-world anyway.
    public void Install()
    {
        if (!_subscribed) { _services.Framework.Update += OnUpdate; _subscribed = true; }
        InjectResolver();
    }

    public void Dispose()
    {
        if (_subscribed) { try { _services.Framework.Update -= OnUpdate; } catch { } _subscribed = false; }
    }

    // The union of all uids any invite list currently shows. Only wanted uids are ever resolved via RPC; cached names
    // (incl. free ones captured from invite payloads) survive a wanted-set change.
    public void SetWanted(IEnumerable<string> uids)
    {
        _wanted.Clear();
        if (uids != null)
            foreach (var u in uids)
                if (!string.IsNullOrEmpty(u)) _wanted.Add(u);

        // Eager kick: issue resolves for newly-wanted uids right now instead of waiting for the next tick, so adding a
        // uid (via the save path) starts its lookup instantly. Same in-world gate the tick uses — not in-world is a
        // silent no-op (the tick picks them up after login, as before). Goes through the shared in-flight guard, so
        // there's no duplicate-RPC risk vs. the tick.
        if (_services.ClientState.Phase == GamePhase.World)
            IssuePendingResolves();
    }

    // null = unknown-yet (keep showing bare uid), "" = resolved-no-name, else the display name.
    public string? TryGet(string uid)
        => (uid != null && _cache.TryGetValue(uid, out var n)) ? n : null;

    // Request a name for ONE uid without disturbing the wanted-set union (unlike SetWanted, which clears). Used by the
    // Add-player dialog's "Look up" button. Adds the uid to the wanted set and eagerly issues the RPC when in-world; the
    // result lands in the permanent session cache (TryGet), so it survives a later SetWanted that drops it from _wanted.
    public void Request(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return;
        _wanted.Add(uid);
        if (_services.ClientState.Phase == GamePhase.World)
        {
            InjectResolver();          // define the fn NOW so this same tick's issue isn't a nil-call (Install ran pre-Ready)
            IssuePendingResolves();
        }
    }

    // Pre-seed a known name (e.g. a nearby player whose name we already read from the nameplate) so the uid rows show it
    // immediately with no RPC. Does not overwrite an already-cached entry.
    public void Seed(string uid, string name)
    {
        if (string.IsNullOrEmpty(uid) || name == null) return;
        if (!_cache.ContainsKey(uid)) { _cache[uid] = name; _inFlight.Remove(uid); }
    }

    private void OnUpdate(float dt)
    {
        _clock += dt;

        // Re-assert the resolver fn on a slow timer whenever in-world. This is the ACTUAL fix for "rpc issue but no
        // resolve": Install() runs at construction (pre-Lua-Ready) so its inject no-ops → the fn was never defined.
        // InjectResolver is idempotent (its rawget guard makes a re-call a cheap no-op once defined, and it re-defines
        // after a scene reload wipes globals). Gated on in-world, where the Lua VM is live. Runs before the poll gate
        // so the poll cadence never starves it.
        if (_services.ClientState.Phase == GamePhase.World)
        {
            _reassertTimer += dt;
            if (_reassertTimer >= ReassertInterval) { _reassertTimer = 0f; InjectResolver(); }
        }

        _pollTimer += dt;
        if (_pollTimer < PollInterval) return;
        _pollTimer = 0f;
        try { Poll(); }
        catch (Exception ex) { _services.Log.Warning($"[InviteNames] poll error: {ex.Message}"); }
    }

    private void Poll()
    {
        // 1. Drain the flush global: each record caches a name (possibly "") and clears its in-flight mark. This also
        //    absorbs the free names the invite wraps append, so a captured name needs no RPC.
        var flush = _services.Lua.ReadGlobalString(FlushGlobal);
        if (!string.IsNullOrEmpty(flush))
        {
            foreach (var rec in flush.Split(RecordSep))
            {
                if (rec.Length == 0) continue;   // trailing empty after the final \30
                int sep = rec.IndexOf(FieldSep);
                string uid  = sep >= 0 ? rec.Substring(0, sep) : rec;
                string name = sep >= 0 ? rec.Substring(sep + 1) : "";
                if (uid.Length == 0) continue;
                _cache[uid] = name;      // may be "" — terminal, stops retrying
                _inFlight.Remove(uid);
                _attempts.Remove(uid);   // success clears the retry count so a later re-request can retry fresh
                _services.Log.Info($"[InviteNames] resolved {uid} => '{name}'");   // diag: RPC/free-name landing
            }
            // Clear for the next batch (guarded write).
            _services.Lua.DoString("pcall(function() rawset(_G,'" + FlushGlobal + "','') end)");
        }

        // 1b. Diagnostic: drain the coroutine step/error markers the injected resolver records (the xpcall error handler
        //     swallows everything, so this is the only window into WHERE the social RPC coroutine fails). Log + clear.
        var diag = _services.Lua.ReadGlobalString(DiagGlobal);
        if (!string.IsNullOrEmpty(diag))
        {
            _services.Log.Warning("[InviteNames][diag] " + diag);
            _services.Lua.DoString("pcall(function() rawset(_G,'" + DiagGlobal + "','') end)");
        }

        // 2. Issue resolves for wanted-but-unknown uids — only in-world (the RPC needs a logged-in session).
        if (_services.ClientState.Phase != GamePhase.World) return;
        IssuePendingResolves();
    }

    // Issue resolves for wanted-but-unknown, not-in-flight uids, capped per pass. Caller must have already confirmed
    // we're in-world. An in-flight uid older than the timeout gets one retry (covers a resolve that silently dropped);
    // the in-flight mark is the dedup guard, so this is safe to call from both the tick and SetWanted. Only real RPC
    // issues count against the cap — marking a non-numeric uid terminal is free.
    private void IssuePendingResolves()
    {
        int issued = 0;
        foreach (var uid in _wanted)
        {
            if (_cache.ContainsKey(uid)) continue;
            bool retry = _inFlight.TryGetValue(uid, out var started);
            if (retry && (_clock - started) < InFlightTimeout) continue;
            if (!AllDigits(uid)) { _cache[uid] = ""; continue; }   // not a numeric id → mark terminal, never RPC

            // A uid already in-flight that reaches here is a timed-out RETRY. Cap it: past MaxAttempts a uid that still
            // won't flush becomes the terminal "" (resolved-no-name) instead of retrying forever, so its row/card leaves
            // the "Looking up…" state. A real flush clears _attempts, so a later re-request retries fresh.
            if (retry)
            {
                int n = _attempts.TryGetValue(uid, out var a) ? a : 0;
                if (n >= MaxAttempts) { _cache[uid] = ""; _inFlight.Remove(uid); _attempts.Remove(uid); continue; }
                _attempts[uid] = n + 1;
            }

            // Present/AOI players resolve SYNCHRONOUSLY via CombatLookup — no RPC needed (the async social RPC is
            // flaky, and most adds are present players). Build the present-player entity uuid from the typed charId
            // (uuid = charId<<16 | 640) and ask CombatLookup; a non-empty name is terminal, so a nearby player never
            // needs the RPC. Only an absent id (empty/null here) falls through to the async path below.
            long cid = long.Parse(uid);
            long uuid = (cid << 16) | 640;
            try
            {
                var nm = _services.CombatLookup.GetEntityName(new EntityId(uuid));
                if (!string.IsNullOrEmpty(nm)) { _cache[uid] = nm; _inFlight.Remove(uid); _attempts.Remove(uid); continue; }
            }
            catch { }

            IssueResolve(uid);
            _inFlight[uid] = _clock;
            if (++issued >= MaxResolvesPerPass) break;
        }
    }

    // Call the injected resolver with the uid as a bare integer literal (digits only — validated) so int64 precision
    // is preserved. Guarded so a pre-login / missing-fn call is a silent no-op.
    private void IssueResolve(string uid)
    {
        _services.Log.Info($"[InviteNames] rpc issue {uid}");   // diag: absent-player async path taken
        _services.Lua.DoString(
            "pcall(function() if " + ResolveFn + " then " + ResolveFn + "(" + uid + ") end end)");
    }

    // Define the resolver fn once + ensure the flush + diag globals exist. The fn runs the social RPC inside a coroutine
    // and appends "<uid>\31<name>\30" to the flush global; it writes NOTHING to flush when the social VM isn't ready
    // (pre-login) so the uid stays in-flight and is retried. DIAGNOSTIC: every step marker + any pcall/xpcall error is
    // recorded into the diag global (the xpcall error handler otherwise swallows all failures), drained in Poll. All
    // global writes use rawset, and reads use rawget(_G,…), because the game guards _G with a strict __newindex.
    private void InjectResolver() => _services.Lua.DoString(@"
pcall(function()
  if rawget(_G,'" + ResolveFn + @"')==nil then
    rawset(_G,'" + ResolveFn + @"', function(uid)
      local function d(t) rawset(_G,'" + DiagGlobal + @"',(rawget(_G,'" + DiagGlobal + @"') or '')..tostring(t)..' | ') end
      Z.CoroUtil.create_coro_xpcall(function()
        d('start '..tostring(uid))
        local social = Z.VMMgr.GetVM('social')
        if not social then d('NO social VM') return end
        d('social type='..type(social)..' basicFn='..type(social.AsyncGetSocialDataTypeBasic))
        local cs = Z.CancelSource.Rent()
        local ok, data = pcall(function() return social.AsyncGetSocialDataTypeBasic(uid, cs:CreateToken()) end)
        cs:Recycle()
        d('call ok='..tostring(ok)..' dataNil='..tostring(data==nil))
        if not ok then d('CALL ERR '..tostring(data)) end
        local name = (data and data.basicData and data.basicData.name) or ''
        d('name='..tostring(name))
        rawset(_G,'" + FlushGlobal + @"',(rawget(_G,'" + FlushGlobal + @"') or '')..tostring(uid)..'\31'..name..'\30')
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
