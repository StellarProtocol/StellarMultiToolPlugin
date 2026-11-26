using System;
using System.Collections.Generic;
using System.Text;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

/// <summary>
/// Auto-accepts (or lets fall through to the normal manual prompt) incoming player-to-player invites and requests,
/// filtered by the requester's character uid. Three user-facing systems, each with a 3-way MODE (Off / Listed /
/// Everyone — <see cref="ModeOff"/>/<see cref="ModeListed"/>/<see cref="ModeEveryone"/>) and its own uid-set; the one
/// decision every wrap makes is <c>accept = (mode == Everyone) or (mode == Listed and uids[uid])</c>
/// (<c>__stellar_inv_ok</c>). Backed by these Lua notify → tip wraps (InviteAutoAccept.Lua.cs):
///
///   PARTY system (mode/uids <c>__stellar_inv_party_*</c>):
///   (1) PARTY / TEAM invite — gRPC notify → <c>Z.VMMgr.GetVM("team_tips").ReceiveInvited</c>. Accept via
///       <c>Z.VMMgr.GetVM("team").AsyncReplyBeInvitation(teamId, true, token)</c>. uid = <c>inviteMemData.basicData.charID</c>.
///   (4) PARTY JOIN REQUEST / application (the INVERSE of (1): someone applies to join MY party and I approve). Choke
///       point is the TEAM gRPC notify STUB <c>GrpcTeamNtfStubImpl.NotifyTeamJoinApplyChanged(self, call, vRequest)</c>
///       on the MODULE-LOCAL table reached by <c>require("zservice/grpc_team_ntf_impl")</c> (EXACT slash separator) — a
///       COLON method, so the wrap keeps self and calls the original FIRST (its SetApplyList + red-point bookkeeping
///       must run). Decided by the APPLICANT (<c>vRequest.item.applicantId</c>, int64, proto
///       <c>TeamJoinApplyListItem.applicant_id</c>; for a party-MERGE application that is the other party's leader).
///       Approve via <c>Z.VMMgr.GetVM("team").AsyncDealApplyJoin(applyId, true, token)</c> — <c>applyId</c> is a STRING
///       reply key (pass as-is). A backlog sweep (applies already pending, keyed under team_data.applyList — PEEKED
///       from DataMgr's cache, never <c>Z.DataMgr.Get</c>) runs once per re-assert tick. Every evaluated applyId goes
///       in a transient seen-set — approved OR left for the user — so nothing is re-evaluated every tick; the set is
///       reset whenever the pushed state changes (<see cref="PushState"/>), so a mode/list change re-evaluates.
///   SOCIAL system (mode/uids <c>__stellar_inv_int_*</c>):
///   (2) INTERACTION invite (paired-emote / "play together") — world notify → <c>GetVM("multaction").NotifyInvite</c>.
///       Accept via the SAME VM's <c>asyncCheckandReplyInvite(vOrigId, vActionId, true, token)</c>.
///   (3) SHARE MOUNT / ride-together invite — the gRPC notify STUB methods on <c>require("zservice/world_ntf_impl")</c>:
///       <c>NotifyInviteApplyRide</c> (uid = <c>param.driverId</c>) and <c>NotifyDriverApplyRide</c> (uid =
///       <c>param.applyId</c>), COLON methods. Accept via <c>GetVM("vehicle").AsyncApplyToRideResult(uid, 1, token)</c>.
///   LEADER system (mode/uids <c>__stellar_inv_leader_*</c>):
///   (5) PARTY LEADER REQUEST (a member asks to take over as leader) — the TEAM stub
///       <c>GrpcTeamNtfStubImpl.NotifyApplyBeLeader(self, call, vRequest)</c> (COLON). The original ignores a request
///       from the current leader or from self, else shows the tip via <c>team_tips_vm.ApplyCaptain</c>. Accept = what the
///       tip's ✓ does (team_tips_vm.lua:21): <c>GetVM("team").AsyncTransferLeader(vRequest.memData.charId, token)</c>
///       with the ORIGINAL int64. uid = <c>tostring(vRequest.memData.charId)</c>; tip key <c>3_&lt;charID&gt;</c>.
///
/// Hooked by Lua method override (NOT Harmony — these are Lua methods and a C# patch risks AOT-inlining). Each wrap
/// is idempotent: it stashes the original on the table (<c>__stellar_orig_*</c>), calls it FIRST so the game sets up
/// its tip/VM state, then applies the decision. The wraps are re-asserted on a ~3s timer because the VMs can be rebuilt
/// on scene loads. The wrap/sweep code is DEFINED ONCE per Lua state as _G functions (<c>__stellar_inv_def</c>) and each
/// tick only calls them, so the big chunk is not re-compiled every 3 s (Plugin-Performance-Rules.md §4).
///
/// ⚠️ The two VM notify methods are declared SELF-LESS — <c>ReceiveInvited(inviteMemData, teamId, …)</c> and
/// <c>NotifyInvite(vOrigId, vActionId)</c> — and called with a DOT, as are the accept methods. So every call there is
/// a dot call; a colon would shift the args (the colon-vs-dot gotcha in Lua-Injection-from-CSharp.md).
///
/// ⚠️ uid is int64 and can exceed 2^53 → a Lua number would lose precision. The uid SETS are keyed by STRING and the
/// wraps compare <c>tostring(id)</c> against them; the accept RPCs always get the ORIGINAL int64, never the string.
/// Each decision runs in its own pcall so a payload shape change can never throw inside the game's notify path — a
/// throw just falls through = normal prompt.
///
/// State lives in Lua globals, pushed from C# whenever config changes (and re-pushed each re-assert). Writes go through
/// <c>rawset(_G, …)</c> because the game protects <c>_G</c> with a strict <c>__newindex</c> that silently drops plain
/// <c>_G.x = v</c> writes (Lua-Injection-from-CSharp.md §strict-global guard); reads are unguarded.
/// </summary>
internal sealed partial class InviteAutoAccept
{
    // The 3-way mode of every system. Stored as these ints in config (invite_*_mode) and pushed to Lua as-is.
    public const int ModeOff = 0, ModeListed = 1, ModeEveryone = 2;

    private const float PollInterval = 3f;   // re-assert against VM recreation on scene loads

    private readonly IPluginServices _services;

    public InviteAutoAccept(IPluginServices services) => _services = services;

    private int _partyMode, _intMode, _leaderMode;
    private IReadOnlyCollection<string> _partyUids  = Array.Empty<string>();
    private IReadOnlyCollection<string> _intUids    = Array.Empty<string>();
    private IReadOnlyCollection<string> _leaderUids = Array.Empty<string>();

    // The state push, PREBUILT whenever a Set* changes it and re-sent verbatim on every re-assert tick — the 3 s timer
    // never builds a string (Plugin-Performance-Rules.md §2). _stateChanged = the next push also resets the
    // join-request seen-set, so a mode/list change re-evaluates applies that were left for the user.
    private string _pushChunk = "";
    private bool   _stateChanged;

    private bool   _subscribed;
    private float  _timer;
    private double _tdHealed;   // last seen _mt_td_healed (team_data self-heal count) — log once per increase

    // Called from the plugin on any config change: store the new mode/uids and push them into Lua immediately so the
    // change bites without waiting for the next re-assert tick. Party = invites (1) + join requests (4).
    public void SetParty(int mode, IReadOnlyCollection<string> uids)
    {
        _partyMode = mode; _partyUids = uids ?? Array.Empty<string>();
        StateChanged();
    }

    // Social: drives BOTH the co-op-emote (multaction) invite AND both ride-together (vehicle/mount) directions — one
    // mode, one uid-set, pushed as the __stellar_inv_int_* globals that all three wraps read.
    public void SetInteraction(int mode, IReadOnlyCollection<string> uids)
    {
        _intMode = mode; _intUids = uids ?? Array.Empty<string>();
        StateChanged();
    }

    // (5) Party leader requests — its own mode + uid-set (separate trust from Party), __stellar_inv_leader_*.
    public void SetLeader(int mode, IReadOnlyCollection<string> uids)
    {
        _leaderMode = mode; _leaderUids = uids ?? Array.Empty<string>();
        StateChanged();
    }

    private void StateChanged()
    {
        _pushChunk = BuildPushChunk();
        _stateChanged = true;
        Apply();
    }

    // Inject the wrap (idempotent) and start the re-assert timer. Safe before login — the Lua guards no-op until the
    // VMs exist, and the ~3s re-assert installs them the moment they come up.
    public void Install()
    {
        if (!_subscribed) { _services.Framework.Update += OnUpdate; _subscribed = true; }
        _timer = PollInterval;
        Apply();
    }

    public void Dispose()
    {
        if (_subscribed) { try { _services.Framework.Update -= OnUpdate; } catch { } _subscribed = false; }
        RestoreWrap();   // best-effort; tolerates the game being gone
    }

    private void OnUpdate(float dt)
    {
        _timer += dt;
        if (_timer < PollInterval) return;
        _timer = 0f;
        try { Apply(); }
        catch (Exception ex) { _services.Log.Warning($"[InviteAccept] apply error: {ex.Message}"); }
    }

    // Re-assert: (re)install the idempotent wraps and push the current modes/uid-sets. Each call is a guarded pcall,
    // so this is safe to run repeatedly and before login.
    public void Apply()
    {
        bool tdLua  = EnsureTeamDataLua();   // defines the cache peek the sweep + heal use (once per Lua state)
        bool invLua = EnsureInviteLua();     // defines the wraps / sweep / tip-close helpers (once per Lua state)
        if (invLua) _services.Lua.DoString(InstallCall);
        PushState();
        if (invLua) _services.Lua.DoString(SweepCall);   // (4) backlog of applies pending before the wrap / a change
        if (tdLua) HealTeamData();   // every tick, regardless of modes: repair a team_data poisoned by ANY early caller
    }

    public void Reassert() => Apply();

    // Push the prebuilt state chunk (and, once after a change, the seen-set reset). The wraps read these globals live
    // on every invite, so re-pushing is how a config change takes effect.
    private void PushState()
    {
        if (_pushChunk.Length == 0) _pushChunk = BuildPushChunk();
        _services.Lua.DoString(_pushChunk);
        if (_stateChanged)
        {
            _services.Lua.DoString(ResetSeenChunk);
            _stateChanged = false;
        }
    }

    private const string ResetSeenChunk = "pcall(function() rawset(_G,'__stellar_inv_apply_seen',{}) end)";

    // Modes as Lua integers; uid tables as Lua table literals keyed by the decimal uid STRING (`{ ["123"]=true }`) —
    // BuildUidTable only emits digit-only keys, so there is no way for a uid to inject Lua. Built on change only.
    private string BuildPushChunk() =>
        "pcall(function()" +
        " rawset(_G,'__stellar_inv_party_mode', " + ModeLit(_partyMode) + ")" +
        " rawset(_G,'__stellar_inv_party_uids', " + BuildUidTable(_partyUids) + ")" +
        " rawset(_G,'__stellar_inv_int_mode', " + ModeLit(_intMode) + ")" +
        " rawset(_G,'__stellar_inv_int_uids', " + BuildUidTable(_intUids) + ")" +
        " rawset(_G,'__stellar_inv_leader_mode', " + ModeLit(_leaderMode) + ")" +
        " rawset(_G,'__stellar_inv_leader_uids', " + BuildUidTable(_leaderUids) + ")" +
        " end)";

    // Clamped so a hand-edited config can only ever push 0/1/2.
    private static string ModeLit(int mode) => mode == ModeListed ? "1" : mode == ModeEveryone ? "2" : "0";

    // team_data cache PEEK + self-heal. ⚠️ We must NEVER call Z.DataMgr.Get('team_data') on a timer (1.2.0's sweep did):
    // DataMgr caches the object BEFORE Init(), so if we are the first caller while TeamTargetTableMgr isn't registered
    // yet (tables load async; world-active ≠ tables ready) Init throws inside our pcall and the half-built team_data
    // sticks for the session — My Party then shows "Loading" forever (KB Lua-Injection-from-CSharp.md
    // "Z.DataMgr.Get caches BEFORE Init()"; same fix as RaidManager 2.9.1). __stellar_mt_tdPeek reads DataMgr's
    // cached_data upvalue via debug.getupvalue and returns the instance ONLY if the game already made it (never
    // creates). __stellar_mt_tdHeal re-runs InitCfgData once on a cached instance that lacks TeamTargetTableDatas once
    // the table is registered, and bumps _mt_td_healed (C# logs once per increase). Defined once per Lua state (KB
    // "poll big chunks via a once-compiled _G function"); every global goes through rawset (_G.__newindex drops plain
    // new globals).
    private const string TdDefChunk = @"pcall(function()
rawset(_G, '__stellar_mt_tdPeek', function()
  local up = debug ~= nil and debug.getupvalue or nil
  if up == nil then return nil end
  local cache
  for i = 1, 8 do
    local n, v = up(Z.DataMgr.Get, i)
    if n == nil then break end
    if n == 'cached_data' then cache = v break end
  end
  if type(cache) ~= 'table' then return nil end
  return rawget(cache, 'team_data')
end)
rawset(_G, '__stellar_mt_tdHeal', function()
  if Z.TableMgr.GetTable('TeamTargetTableMgr') == nil then return end
  local td = __stellar_mt_tdPeek()
  if td ~= nil and rawget(td, 'TeamTargetTableDatas') == nil and rawget(td, '__mt_healed') ~= true then
    rawset(td, '__mt_healed', true)
    td:InitCfgData()
    if rawget(td, 'TeamTargetTableDatas') ~= nil then
      rawset(_G, '_mt_td_healed', (rawget(_G, '_mt_td_healed') or 0) + 1)
    end
  end
end)
rawset(_G, '__stellar_mt_td_def', true)
end)";

    // Constant one-liner into the defined function — nothing is built per tick. pcall(nil) after a Lua reset just
    // returns false (the next EnsureTeamDataLua re-defines).
    private const string TdHealCall = "pcall(__stellar_mt_tdHeal)";

    private bool EnsureTeamDataLua()
    {
        var lua = _services.Lua;
        if (!lua.Ready) return false;
        if (lua.TryReadGlobalBool("__stellar_mt_td_def", out var def) && def) return true;
        lua.DoString(TdDefChunk);
        return true;
    }

    private void HealTeamData()
    {
        _services.Lua.DoString(TdHealCall);
        // One typed read at the 3 s cadence; a log line only when the count goes up.
        if (_services.Lua.TryReadGlobalNumber("_mt_td_healed", out var healed) && healed > _tdHealed)
        {
            _tdHealed = healed;
            _services.Log.Info("[MultiTool] repaired party data (team target table)");
        }
    }

    private static string BuildUidTable(IReadOnlyCollection<string> uids)
    {
        var sb = new StringBuilder("{");
        if (uids != null)
        {
            foreach (var u in uids)
            {
                if (string.IsNullOrEmpty(u)) continue;
                bool digits = true;
                foreach (char c in u) if (c < '0' || c > '9') { digits = false; break; }
                if (!digits) continue;   // uid sets are decimal char ids only — never emit anything else into the literal
                sb.Append("[\"").Append(u).Append("\"]=true,");
            }
        }
        sb.Append('}');
        return sb.ToString();
    }
}
