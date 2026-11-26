namespace Stellar.MultiTool;

// The Lua side of InviteAutoAccept (wraps, backlog sweep, tip close, restore), split out of InviteAutoAccept.cs to stay
// under the 500-line cap. The mechanism, the per-system choke points and every gotcha are documented on the class.
//
// Everything is DEFINED ONCE per Lua state as _G functions (InvDefChunk, flag __stellar_inv_def — same shape as the
// team_data TdDefChunk) and the 3 s re-assert only runs the constant one-liners InstallCall / SweepCall, so the big
// chunk is compiled once instead of every tick (KB "poll big chunks via a once-compiled _G function"). pcall(nil)
// after a Lua reset just returns false; the next EnsureInviteLua re-defines. Every global goes through rawset
// (_G.__newindex drops plain new globals); no statement starts with "(" (it would need a leading ";").
internal sealed partial class InviteAutoAccept
{
    private const string InstallCall = "pcall(__stellar_inv_install)";
    private const string SweepCall   = "pcall(__stellar_inv_sweep)";

    private bool EnsureInviteLua()
    {
        var lua = _services.Lua;
        if (!lua.Ready) return false;
        if (lua.TryReadGlobalBool("__stellar_inv_def", out var def) && def) return true;
        lua.DoString(InvDefChunk);
        return true;
    }

    private void RestoreWrap() => _services.Lua.DoString("pcall(__stellar_inv_restore)");

    // decision: accept = (mode == 2 Everyone) or (mode == 1 Listed and uids[uid]). Every accept runs inside
    // Z.CoroUtil.create_coro_xpcall because the accept RPCs yield; the token is a freshly-Rent()'d CancelSource
    // :CreateToken() :Recycle()'d after — exactly how the game's own MultActionVM.NotVaildMultAction replies
    // (multaction_vm.lua:260). The tip is closed ONLY on the accept branch — a fall-through keeps the tip so the user can
    // still answer manually.
    private const string InvDefChunk = @"pcall(function()
rawset(_G, '__stellar_inv_ok', function(mode, uids, uid)
  if mode == 2 then return true end
  return mode == 1 and type(uids) == 'table' and uids[uid] == true
end)

-- Close an invitation tip by its unitName (tostring(tipsType)..'_'..unitId): the full-teardown event the game's own
-- close uses (InvitationClearTipsUnit), NOT Remove (which leaves dedup state and would block future legit tips).
rawset(_G, '__stellar_mt_closeTip', function(key)
  Z.EventMgr:Dispatch(Z.ConstValue.InvitationClearTipsUnit, key)
end)

-- (4) approve one party-join application. applyId is a STRING reply key (pass as-is). Only a Team-MERGE application
-- shows a tip (tipsType 2 = TeamRequest, unitId = applyId); for a Personal apply the close is a no-op.
-- AsyncDealApplyJoin itself does the RemoveApplyList + red-point clear (after its RPC returns, team_vm.lua:665).
rawset(_G, '__stellar_inv_approveJoin', function(id)
  Z.CoroUtil.create_coro_xpcall(function()
    local cs = Z.CancelSource.Rent()
    Z.VMMgr.GetVM('team').AsyncDealApplyJoin(id, true, cs:CreateToken())
    cs:Recycle()
  end, function() end)()
  __stellar_mt_closeTip('2_' .. tostring(id))
end)

-- (4) one application, from the push wrap or the sweep. Every evaluated applyId is marked seen — approved OR left for
-- the user — so the sweep doesn't re-evaluate it every tick; an approve in flight must not re-fire either, because
-- the id stays in applyList until the RPC returns. C# resets the set when the pushed state changes.
rawset(_G, '__stellar_inv_decideJoin', function(mode, item, aid)
  local id = (item and item.applyId) or aid
  if id == nil then return end
  local seen = rawget(_G, '__stellar_inv_apply_seen')
  if type(seen) ~= 'table' then seen = {}; rawset(_G, '__stellar_inv_apply_seen', seen) end
  if seen[id] then return end
  seen[id] = true
  local ok, yes = pcall(__stellar_inv_ok, mode, __stellar_inv_party_uids, tostring(item.applicantId))
  if ok and yes then __stellar_inv_approveJoin(id) end
end)

-- (4) Backlog sweep — applies already pending (the push wrap only catches FUTURE ones, and a mode/list change must
-- re-evaluate the ones left for the user). The live list is team_data.applyList keyed by applyId → applyInfo
-- (team_data.lua:294), PEEKED from DataMgr's cache — never Z.DataMgr.Get on a timer (see TdDefChunk). The seen set is
-- pruned against the live list each pass so a later distinct apply can fire. Immediate bail while Party is Off.
rawset(_G, '__stellar_inv_sweep', function()
  local mode = __stellar_inv_party_mode
  if mode ~= 1 and mode ~= 2 then return end
  local peek = rawget(_G, '__stellar_mt_tdPeek')
  if peek == nil then return end
  local td = peek()
  if not td or not td.applyList then return end
  local seen = rawget(_G, '__stellar_inv_apply_seen')
  if type(seen) == 'table' then
    for aid in pairs(seen) do
      if td.applyList[aid] == nil then seen[aid] = nil end
    end
  end
  for aid, item in pairs(td.applyList) do
    __stellar_inv_decideJoin(mode, item, aid)
  end
end)

rawset(_G, '__stellar_inv_install', function()
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
        local uid = tostring(inviteMemData.basicData.charID)
        if not __stellar_inv_ok(__stellar_inv_party_mode, __stellar_inv_party_uids, uid) then return end
        -- Team tipsType=1; the team tip's unitName keys on charID (team_tips_vm.lua:140 sets unitId = charID).
        __stellar_mt_closeTip('1_' .. uid)
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
        local uid = tostring(vOrigId)
        if not __stellar_inv_ok(__stellar_inv_int_mode, __stellar_inv_int_uids, uid) then return end
        __stellar_mt_closeTip('5_' .. uid)   -- MultActionInvite tipsType=5, keyed on vOrigId
        Z.CoroUtil.create_coro_xpcall(function()
          local cs = Z.CancelSource.Rent()
          Z.VMMgr.GetVM('multaction').asyncCheckandReplyInvite(vOrigId, vActionId, true, cs:CreateToken())
          cs:Recycle()
        end, function() end)()
      end)
    end
  end
  -- (3) SHARE MOUNT (ride-together) — the gRPC notify STUB methods on the module-local table (NOT a VM). require() with
  -- the game's EXACT slash key returns the same cached table the dispatcher uses; nil pre-login, the re-assert retries.
  -- Both directions share the Social mode/uid-set (__stellar_inv_int_*). COLON methods: keep self, call the original
  -- FIRST (its CheckApplyType gate + tip setup), then decide.
  local ntf = require('zservice/world_ntf_impl')
  if ntf then
    if not ntf.__stellar_orig_notify_invite_ride then
      ntf.__stellar_orig_notify_invite_ride = ntf.NotifyInviteApplyRide
      ntf.NotifyInviteApplyRide = function(self, call, param)
        ntf.__stellar_orig_notify_invite_ride(self, call, param)
        pcall(function()   -- free name: driver invite payload carries param.driverName
          local nu = tostring(param.driverId)
          rawset(_G,'__stellar_inv_name_flush',(__stellar_inv_name_flush or '')..nu..'\31'..(param.driverName or '')..'\30')
        end)
        pcall(function()
          local uid = tostring(param.driverId)
          if not __stellar_inv_ok(__stellar_inv_int_mode, __stellar_inv_int_uids, uid) then return end
          __stellar_mt_closeTip('12_' .. uid)   -- VehicleInvite tipsType=12, keyed on the driver uid
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
          local uid = tostring(param.applyId)
          if not __stellar_inv_ok(__stellar_inv_int_mode, __stellar_inv_int_uids, uid) then return end
          __stellar_mt_closeTip('11_' .. uid)   -- VehicleApply tipsType=11, keyed on the applicant uid
          Z.CoroUtil.create_coro_xpcall(function()
            local cs = Z.CancelSource.Rent()
            Z.VMMgr.GetVM('vehicle').AsyncApplyToRideResult(param.applyId, 1, cs:CreateToken())
            cs:Recycle()
          end, function() end)()
        end)
      end
    end
  end
  -- (4) PARTY JOIN REQUEST + (5) PARTY LEADER REQUEST — the TEAM gRPC notify stubs on their module-local table (same
  -- require technique + exact slash). COLON methods function(self, call, vRequest): call the original FIRST.
  local teamNtf = require('zservice/grpc_team_ntf_impl')
  if teamNtf and not teamNtf.__stellar_orig_notify_team_apply then
    teamNtf.__stellar_orig_notify_team_apply = teamNtf.NotifyTeamJoinApplyChanged
    teamNtf.NotifyTeamJoinApplyChanged = function(self, call, vRequest)
      teamNtf.__stellar_orig_notify_team_apply(self, call, vRequest)
      pcall(function()
        local mode = __stellar_inv_party_mode
        if mode ~= 1 and mode ~= 2 then return end
        __stellar_inv_decideJoin(mode, vRequest.item, nil)
      end)
    end
  end
  if teamNtf and not teamNtf.__stellar_orig_notify_be_leader then
    teamNtf.__stellar_orig_notify_be_leader = teamNtf.NotifyApplyBeLeader
    teamNtf.NotifyApplyBeLeader = function(self, call, vRequest)
      teamNtf.__stellar_orig_notify_be_leader(self, call, vRequest)
      pcall(function()
        local mode = __stellar_inv_leader_mode
        if mode ~= 1 and mode ~= 2 then return end
        local md = vRequest.memData
        local cid = md.charId
        local uid = tostring(cid)
        -- The original drops a request from self or from the current leader (no tip) — never transfer for those.
        -- Leader id via the team_data cache PEEK (never Z.DataMgr.Get from our code).
        local me = Z.ContainerMgr.CharSerialize.charBase.charId
        if cid == me or uid == tostring(me) then return end
        local peek = rawget(_G, '__stellar_mt_tdPeek')
        local td = peek and peek()
        if td and td.TeamInfo and td.TeamInfo.baseInfo and tostring(td.TeamInfo.baseInfo.leaderId) == uid then return end
        if not __stellar_inv_ok(mode, __stellar_inv_leader_uids, uid) then return end
        -- The tip (team_tips_vm.ApplyCaptain) keys on unitId = memData.basicData.charID; TeamLeader tipsType = 3.
        local tt = (E and E.InvitationTipsType and E.InvitationTipsType.TeamLeader) or 3
        local tipId = (md.basicData and md.basicData.charID) or cid
        __stellar_mt_closeTip(tostring(tt) .. '_' .. tostring(tipId))
        Z.CoroUtil.create_coro_xpcall(function()
          local cs = Z.CancelSource.Rent()
          -- What the tip's accept does (team_tips_vm.lua:21); the ORIGINAL int64, not the string.
          Z.VMMgr.GetVM('team').AsyncTransferLeader(cid, cs:CreateToken())
          cs:Recycle()
        end, function() end)()
      end)
    end
  end
end)

rawset(_G, '__stellar_inv_restore', function()
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
  local ntf = require('zservice/world_ntf_impl')   -- same exact-slash handle as install
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
  local teamNtf = require('zservice/grpc_team_ntf_impl')   -- same exact-slash handle as install
  if teamNtf then
    if teamNtf.__stellar_orig_notify_team_apply then
      teamNtf.NotifyTeamJoinApplyChanged = teamNtf.__stellar_orig_notify_team_apply
      teamNtf.__stellar_orig_notify_team_apply = nil
    end
    if teamNtf.__stellar_orig_notify_be_leader then
      teamNtf.NotifyApplyBeLeader = teamNtf.__stellar_orig_notify_be_leader
      teamNtf.__stellar_orig_notify_be_leader = nil
    end
  end
end)

rawset(_G, '__stellar_inv_def', true)
end)";
}
