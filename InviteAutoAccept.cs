using System;
using System.Collections.Generic;
using System.Text;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

/// <summary>
/// Auto-accepts (or lets fall through to the normal manual prompt) incoming player-to-player invites, filtered by the
/// requester's character uid. Two user-facing controls (Party, and Social which fans out to co-op-emote + ride-together),
/// backed by three Lua notify → tip wraps:
///
///   (1) PARTY / TEAM invite — gRPC notify → <c>Z.VMMgr.GetVM("team_tips").ReceiveInvited</c>. Accept via
///       <c>Z.VMMgr.GetVM("team").AsyncReplyBeInvitation(teamId, true, token)</c>. Invite key = the incoming teamId.
///   (2) INTERACTION invite (paired-emote / "play together") — world notify → <c>GetVM("multaction").NotifyInvite</c>.
///       Accept via the SAME VM's <c>asyncCheckandReplyInvite(vOrigId, vActionId, true, token)</c> (preferred over the
///       raw proxy so the required StatusHoldHand pose transition happens). Invite key = (vOrigId, vActionId).
///   (3) SHARE MOUNT / ride-together invite ("carpool" ECarpoolApply) — UNLIKE (1)/(2) the stub does NOT forward to a
///       dedicated VM; it builds the tip inline. So the choke points are the gRPC notify STUB methods themselves, on a
///       MODULE-LOCAL table reached by <c>require("zservice/world_ntf_impl")</c> (EXACT slash separator — the game
///       requires it that way; a different separator = a different cached table = the wrap never fires), NOT a VM.
///       Both directions are wrapped: <c>NotifyInviteApplyRide</c> (a driver invites YOU, uid = <c>param.driverId</c>)
///       and <c>NotifyDriverApplyRide</c> (someone asks to ride YOUR mount, uid = <c>param.applyId</c>). These stub
///       methods are COLON methods — <c>function(self, call, param)</c> — so the wrap keeps the explicit self. Accept
///       via <c>Z.VMMgr.GetVM("vehicle").AsyncApplyToRideResult(uid, 1, token)</c> where 1 =
///       <c>E.VehicleApplyRideResult.ApplyRideResultAgree</c> (numeric literal, robust if E isn't resolvable).
///       ⭐ The mount wraps have NO toggle/mode/uid-set of their own: they SHARE the interaction control, reading the
///       same <c>__stellar_inv_int_on/_wl/_uids</c> globals (2) pushes via <see cref="SetInteraction"/>.
///   (4) PARTY JOIN REQUEST / application (the INVERSE of (1): someone applies to join MY party and I approve) — a
///       single on/off toggle that approves EVERY incoming application, no whitelist/blacklist. Choke point is the
///       TEAM gRPC notify STUB <c>GrpcTeamNtfStubImpl.NotifyTeamJoinApplyChanged(self, call, vRequest)</c> on the
///       MODULE-LOCAL table reached by <c>require("zservice/grpc_team_ntf_impl")</c> (EXACT slash separator, same as
///       the vehicle stubs) — a COLON method, so the wrap keeps self and calls the original FIRST (its SetApplyList +
///       red-point bookkeeping must run). Approve via <c>Z.VMMgr.GetVM("team").AsyncDealApplyJoin(applyId, true, token)</c>
///       — <c>applyId</c> is a STRING reply key (<c>vRequest.item.applyId</c>; pass as-is, no tostring/convert). A
///       backlog sweep (applies already pending when the toggle flips on, keyed under <c>Z.DataMgr.Get("team_data").applyList</c>)
///       runs once per re-assert tick, de-duped by a transient seen-set so an in-flight approve is not re-fired. NO
///       uid-set → does NOT participate in name resolution. Pushed as <c>__stellar_inv_partyapply_on</c>.
///
/// Hooked by Lua VM-method override (NOT Harmony — these are Lua methods and a C# patch risks AOT-inlining). Each wrap
/// is idempotent: it stashes the original on the VM table (<c>__stellar_orig_*</c>), calls it FIRST so the game sets up
/// its tip/VM state, then applies the auto-accept decision. The wraps are re-asserted on a ~3s timer because the VMs can
/// be rebuilt on scene loads (same reason as AutoPlayPermitControl). See Player-Invite-AutoAccept.md.
///
/// ⚠️ The two notify methods are declared SELF-LESS in the game Lua — <c>ReceiveInvited(inviteMemData, teamId, …)</c>
/// and <c>NotifyInvite(vOrigId, vActionId)</c> — and the game calls them with a DOT at the stub sites
/// (world_ntf_impl.lua:97, grpc_team_ntf_impl.lua:153). The accept methods are self-less too. So every call here is a
/// dot call; a colon would shift the args (the colon-vs-dot gotcha in Lua-Injection-from-CSharp.md).
///
/// ⚠️ uid is int64 and can exceed 2^53 → a Lua number would lose precision. The uid SETS are keyed by STRING and the
/// wrap compares <c>tostring(charID)</c>/<c>tostring(vOrigId)</c> against them. (VERIFY in-game that tostring on this
/// VM's int64 yields the plain decimal the user typed — see the KB caveat.) The uid read is wrapped in its own pcall so
/// a payload shape change can never throw inside the game's notify path — a throw just falls through = normal prompt.
///
/// State lives in Lua globals, pushed from C# whenever config changes (and re-pushed each re-assert). Writes go through
/// <c>rawset(_G, …)</c> because the game protects <c>_G</c> with a strict <c>__newindex</c> that silently drops plain
/// <c>_G.x = v</c> writes (Lua-Injection-from-CSharp.md §strict-global guard); reads are unguarded.
/// </summary>
internal sealed class InviteAutoAccept
{
    private const float PollInterval = 3f;   // re-assert against VM recreation on scene loads

    private readonly IPluginServices _services;

    public InviteAutoAccept(IPluginServices services) => _services = services;

    private bool _partyOn, _partyWl;
    private bool _intOn,   _intWl;
    private bool _partyApplyOn;   // (4) approve ALL incoming party-join applications — single toggle, no uid list/mode
    private IReadOnlyCollection<string> _partyUids = Array.Empty<string>();
    private IReadOnlyCollection<string> _intUids   = Array.Empty<string>();

    private bool  _subscribed;
    private float _timer;

    // Called from the plugin on any config change: store the new flags/uids and push them into Lua immediately so the
    // change bites without waiting for the next re-assert tick.
    public void SetParty(bool enabled, bool whitelist, IReadOnlyCollection<string> uids)
    {
        _partyOn = enabled; _partyWl = whitelist; _partyUids = uids ?? Array.Empty<string>();
        Apply();
    }

    // Drives BOTH the co-op-emote (multaction) invite AND both ride-together (vehicle/mount) directions — one toggle,
    // one mode, one uid-set, pushed as the __stellar_inv_int_* globals that all three wraps read.
    public void SetInteraction(bool enabled, bool whitelist, IReadOnlyCollection<string> uids)
    {
        _intOn = enabled; _intWl = whitelist; _intUids = uids ?? Array.Empty<string>();
        Apply();
    }

    // (4) Party JOIN REQUESTS — approve every incoming application to join the player's party. A single on/off flag;
    // no whitelist/blacklist, no uid-set (and so no name resolution). Pushed as __stellar_inv_partyapply_on.
    public void SetPartyApply(bool enabled)
    {
        _partyApplyOn = enabled;
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

    // Re-assert: (re)install the idempotent wrap and push the current flags/uid-sets. Each chunk is a guarded pcall, so
    // this is safe to run repeatedly and before login.
    public void Apply()
    {
        InstallWrap();
        PushState();
        SweepPartyApplyBacklog();   // (4) best-effort: clear applies that were already pending when the toggle flipped on
    }

    public void Reassert() => Apply();

    // Push the enable flags, modes and uid sets as Lua globals. The wrap reads these live on every invite, so re-pushing
    // is how a config change (or a toggle-off) takes effect. uid tables are built as Lua table literals keyed by the
    // decimal uid STRING (`{ ["123"]=true }`) — BuildUidTable only emits digit-only keys, so there is no way for a uid
    // to inject Lua.
    private void PushState() => _services.Lua.DoString(
        "pcall(function()" +
        " rawset(_G,'__stellar_inv_party_on', " + Lit(_partyOn) + ")" +
        " rawset(_G,'__stellar_inv_party_wl', " + Lit(_partyWl) + ")" +
        " rawset(_G,'__stellar_inv_party_uids', " + BuildUidTable(_partyUids) + ")" +
        " rawset(_G,'__stellar_inv_int_on', " + Lit(_intOn) + ")" +
        " rawset(_G,'__stellar_inv_int_wl', " + Lit(_intWl) + ")" +
        " rawset(_G,'__stellar_inv_int_uids', " + BuildUidTable(_intUids) + ")" +
        " rawset(_G,'__stellar_inv_partyapply_on', " + Lit(_partyApplyOn) + ")" +
        " end)");

    // (4) Backlog sweep — approve applies that were already pending when the toggle flipped on (the push wrap only
    // catches FUTURE applies). The live list is Z.DataMgr.Get('team_data').applyList, keyed by applyId → applyInfo
    // (team_data.lua:294). Runs every re-assert tick; a transient __stellar_inv_apply_seen set (pruned against the
    // live list each pass) stops an in-flight approve from re-firing, because AsyncDealApplyJoin only RemoveApplyList's
    // the id AFTER its RPC returns (team_vm.lua:665) — until then the id is still in applyList. No-op (immediate bail)
    // when the toggle is off or the team data isn't up yet. Same coro/token idiom as the accept wraps.
    private void SweepPartyApplyBacklog() => _services.Lua.DoString(@"
pcall(function()
  if not __stellar_inv_partyapply_on then return end
  local td = Z.DataMgr.Get('team_data')
  if not td or not td.applyList then return end
  local seen = __stellar_inv_apply_seen
  if type(seen) ~= 'table' then seen = {}; rawset(_G,'__stellar_inv_apply_seen', seen) end
  for aid in pairs(seen) do
    if td.applyList[aid] == nil then seen[aid] = nil end   -- handled/removed → allow a later distinct apply to fire
  end
  for aid, item in pairs(td.applyList) do
    local id = (item and item.applyId) or aid            -- the key IS the applyId (team_data.lua:294); pass as-is
    if not seen[id] then
      seen[id] = true
      Z.CoroUtil.create_coro_xpcall(function()
        local cs = Z.CancelSource.Rent()
        Z.VMMgr.GetVM('team').AsyncDealApplyJoin(id, true, cs:CreateToken())
        cs:Recycle()
      end, function() end)()
    end
  end
end)");

    private static string Lit(bool b) => b ? "true" : "false";

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

    // The idempotent wraps. A verbatim Lua chunk (newlines, not C# concatenation) so there is no fragment-boundary
    // space trap. decision: listed = uids[uid]==true; accept = (wl and listed) or ((not wl) and not listed).
    // The accept runs inside Z.CoroUtil.create_coro_xpcall because AsyncReplyBeInvitation / asyncCheckandReplyInvite
    // yield on their RPC; the token is a freshly-Rent()'d CancelSource :CreateToken() :Recycle()'d after — exactly how
    // the game's own MultActionVM.NotVaildMultAction replies (multaction_vm.lua:260).
    private void InstallWrap() => _services.Lua.DoString(@"
pcall(function()
  local teamTips = Z.VMMgr.GetVM('team_tips')
  if teamTips and not teamTips.__stellar_orig_recv then
    teamTips.__stellar_orig_recv = teamTips.ReceiveInvited
    teamTips.ReceiveInvited = function(inviteMemData, teamId, targetId, teamNum, memberType)
      teamTips.__stellar_orig_recv(inviteMemData, teamId, targetId, teamNum, memberType)
      -- Free name: the party payload carries the requester name — append it to the resolver's flush global (same
      -- format) regardless of the accept decision, so the list can show it with no RPC. (See InviteNameResolver.)
      pcall(function()
        local nu = tostring(inviteMemData.basicData.charID)
        local nm = inviteMemData.basicData.name
        rawset(_G,'__stellar_inv_name_flush',(__stellar_inv_name_flush or '')..nu..'\31'..(nm or '')..'\30')
      end)
      pcall(function()
        if not __stellar_inv_party_on then return end
        local uid = tostring(inviteMemData.basicData.charID)
        local uids = __stellar_inv_party_uids or {}
        local listed = uids[uid] == true
        local wl = __stellar_inv_party_wl
        local accept = (wl and listed) or ((not wl) and (not listed))
        if not accept then return end
        -- Suppress the invite tip we're about to answer: mirror the game's own close (clearTipsUnit keyed by
        -- tostring(tipsType)..'_'..uid). Team tipsType=1; the team tip's unitName keys on charID (team_tips_vm.lua:140
        -- sets both unitId and charId to charID). Full teardown event (InvitationClearTipsUnit), NOT Remove (which
        -- leaves dedup state and would block future legit tips). Only on the accept branch — a fall-through keeps the
        -- tip so the user can still answer manually.
        Z.EventMgr:Dispatch(Z.ConstValue.InvitationClearTipsUnit, '1_' .. uid)
        Z.CoroUtil.create_coro_xpcall(function()
          local cs = Z.CancelSource.Rent()
          Z.VMMgr.GetVM('team').AsyncReplyBeInvitation(teamId, true, cs:CreateToken())
          cs:Recycle()
        end, function() end)()
      end)
    end
  end
  local multVM = Z.VMMgr.GetVM('multaction')
  if multVM and not multVM.__stellar_orig_notify then
    multVM.__stellar_orig_notify = multVM.NotifyInvite
    multVM.NotifyInvite = function(vOrigId, vActionId)
      multVM.__stellar_orig_notify(vOrigId, vActionId)
      pcall(function()
        if not __stellar_inv_int_on then return end
        local uid = tostring(vOrigId)
        local uids = __stellar_inv_int_uids or {}
        local listed = uids[uid] == true
        local wl = __stellar_inv_int_wl
        local accept = (wl and listed) or ((not wl) and (not listed))
        if not accept then return end
        -- Suppress the tip (MultActionInvite tipsType=5, keyed on vOrigId — confirmed). Full-teardown event only on
        -- the accept branch; see the team wrap for the rationale.
        Z.EventMgr:Dispatch(Z.ConstValue.InvitationClearTipsUnit, '5_' .. uid)
        Z.CoroUtil.create_coro_xpcall(function()
          local cs = Z.CancelSource.Rent()
          Z.VMMgr.GetVM('multaction').asyncCheckandReplyInvite(vOrigId, vActionId, true, cs:CreateToken())
          cs:Recycle()
        end, function() end)()
      end)
    end
  end
  -- (3) SHARE MOUNT (ride-together) — wrap the gRPC notify STUB methods on the module-local table (NOT a VM). require()
  -- with the game's EXACT slash key returns the same cached table the dispatcher uses (one module per require-key); nil
  -- pre-login, the 3s re-assert retries. (require chosen over package.loaded[..] — can't verify the game keys
  -- package.loaded by that raw string, but require returning the cached copy is guaranteed and is the proven
  -- injected-Lua pattern here.) Both directions share the SAME toggle/mode/uid-set as the co-op-emote invite above —
  -- they read the __stellar_inv_int_* globals, NOT a separate mount set.
  local ntf = require('zservice/world_ntf_impl')
  if ntf then
    -- ⚠️ These stubs are COLON methods: function(self, call, param). Keep self and pass it to the original. Call the
    -- original FIRST so the game's CheckApplyType(ECarpoolApply, …) gate runs + the tip is set up, then decide. The
    -- requester uid differs per direction: param.driverId (driver invites you) vs param.applyId (someone asks to ride).
    if not ntf.__stellar_orig_notify_invite_ride then
      ntf.__stellar_orig_notify_invite_ride = ntf.NotifyInviteApplyRide
      ntf.NotifyInviteApplyRide = function(self, call, param)
        ntf.__stellar_orig_notify_invite_ride(self, call, param)
        pcall(function()   -- free name: driver invite payload carries param.driverName
          local nu = tostring(param.driverId)
          rawset(_G,'__stellar_inv_name_flush',(__stellar_inv_name_flush or '')..nu..'\31'..(param.driverName or '')..'\30')
        end)
        pcall(function()
          if not __stellar_inv_int_on then return end
          local uid = tostring(param.driverId)
          local uids = __stellar_inv_int_uids or {}
          local listed = uids[uid] == true
          local wl = __stellar_inv_int_wl
          local accept = (wl and listed) or ((not wl) and (not listed))
          if not accept then return end
          -- Suppress the tip (VehicleInvite tipsType=12, keyed on the driver uid). Accept branch only.
          Z.EventMgr:Dispatch(Z.ConstValue.InvitationClearTipsUnit, '12_' .. uid)
          Z.CoroUtil.create_coro_xpcall(function()
            local cs = Z.CancelSource.Rent()
            -- 2nd arg 1 = E.VehicleApplyRideResult.ApplyRideResultAgree; pass the ORIGINAL int64 uid, not the string.
            Z.VMMgr.GetVM('vehicle').AsyncApplyToRideResult(param.driverId, 1, cs:CreateToken())
            cs:Recycle()
          end, function() end)()
        end)
      end
    end
    if not ntf.__stellar_orig_notify_driver_ride then
      ntf.__stellar_orig_notify_driver_ride = ntf.NotifyDriverApplyRide
      ntf.NotifyDriverApplyRide = function(self, call, param)
        ntf.__stellar_orig_notify_driver_ride(self, call, param)
        pcall(function()   -- free name: apply-to-ride payload carries param.applyName
          local nu = tostring(param.applyId)
          rawset(_G,'__stellar_inv_name_flush',(__stellar_inv_name_flush or '')..nu..'\31'..(param.applyName or '')..'\30')
        end)
        pcall(function()
          if not __stellar_inv_int_on then return end
          local uid = tostring(param.applyId)
          local uids = __stellar_inv_int_uids or {}
          local listed = uids[uid] == true
          local wl = __stellar_inv_int_wl
          local accept = (wl and listed) or ((not wl) and (not listed))
          if not accept then return end
          -- Suppress the tip (VehicleApply tipsType=11, keyed on the applicant uid). Accept branch only.
          Z.EventMgr:Dispatch(Z.ConstValue.InvitationClearTipsUnit, '11_' .. uid)
          Z.CoroUtil.create_coro_xpcall(function()
            local cs = Z.CancelSource.Rent()
            -- 2nd arg 1 = E.VehicleApplyRideResult.ApplyRideResultAgree; pass the ORIGINAL int64 uid, not the string.
            Z.VMMgr.GetVM('vehicle').AsyncApplyToRideResult(param.applyId, 1, cs:CreateToken())
            cs:Recycle()
          end, function() end)()
        end)
      end
    end
  end
  -- (4) PARTY JOIN REQUEST (someone applies to join MY party) — wrap the TEAM gRPC notify stub on its module-local
  -- table (same require technique + exact slash as the vehicle stubs above). COLON method function(self, call, vRequest):
  -- call the original FIRST so the game's SetApplyList + red-point bookkeeping runs, THEN approve. No uid list — the
  -- toggle approves EVERY apply. applyId is a STRING reply key (pass as-is, no tostring/convert). nil pre-login → the
  -- 3s re-assert retries. The backlog sweep (SweepPartyApplyBacklog) handles applies pending before this wrap installed.
  local teamNtf = require('zservice/grpc_team_ntf_impl')
  if teamNtf and not teamNtf.__stellar_orig_notify_team_apply then
    teamNtf.__stellar_orig_notify_team_apply = teamNtf.NotifyTeamJoinApplyChanged
    teamNtf.NotifyTeamJoinApplyChanged = function(self, call, vRequest)
      teamNtf.__stellar_orig_notify_team_apply(self, call, vRequest)
      pcall(function()
        if not __stellar_inv_partyapply_on then return end
        local id = vRequest.item.applyId   -- STRING reply key; AsyncDealApplyJoin wants it verbatim (no int64 risk)
        Z.CoroUtil.create_coro_xpcall(function()
          local cs = Z.CancelSource.Rent()
          Z.VMMgr.GetVM('team').AsyncDealApplyJoin(id, true, cs:CreateToken())
          cs:Recycle()
        end, function() end)()
        -- Suppress only the Team-MERGE tip (tipsType 2 = TeamRequest, keyed on unitId = applyId). A Personal apply shows
        -- no tip, so this is a harmless no-op there. AsyncDealApplyJoin itself does the RemoveApplyList + red-point clear.
        Z.EventMgr:Dispatch(Z.ConstValue.InvitationClearTipsUnit, '2_' .. tostring(id))
      end)
    end
  end
end)");

    private void RestoreWrap() => _services.Lua.DoString(@"
pcall(function()
  local teamTips = Z.VMMgr.GetVM('team_tips')
  if teamTips and teamTips.__stellar_orig_recv then
    teamTips.ReceiveInvited = teamTips.__stellar_orig_recv
    teamTips.__stellar_orig_recv = nil
  end
  local multVM = Z.VMMgr.GetVM('multaction')
  if multVM and multVM.__stellar_orig_notify then
    multVM.NotifyInvite = multVM.__stellar_orig_notify
    multVM.__stellar_orig_notify = nil
  end
  local ntf = require('zservice/world_ntf_impl')   -- same exact-slash handle as InstallWrap
  if ntf then
    if ntf.__stellar_orig_notify_invite_ride then
      ntf.NotifyInviteApplyRide = ntf.__stellar_orig_notify_invite_ride
      ntf.__stellar_orig_notify_invite_ride = nil
    end
    if ntf.__stellar_orig_notify_driver_ride then
      ntf.NotifyDriverApplyRide = ntf.__stellar_orig_notify_driver_ride
      ntf.__stellar_orig_notify_driver_ride = nil
    end
  end
  local teamNtf = require('zservice/grpc_team_ntf_impl')   -- same exact-slash handle as InstallWrap
  if teamNtf and teamNtf.__stellar_orig_notify_team_apply then
    teamNtf.NotifyTeamJoinApplyChanged = teamNtf.__stellar_orig_notify_team_apply
    teamNtf.__stellar_orig_notify_team_apply = nil
  end
end)");
}
