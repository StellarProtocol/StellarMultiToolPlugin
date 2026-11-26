namespace Stellar.MultiTool;

// The Lua wrap chunks of InviteAutoAccept (install + restore), split out of InviteAutoAccept.cs to stay under the
// 500-line cap. The mechanism, the per-system choke points and every gotcha are documented on the class itself.
internal sealed partial class InviteAutoAccept
{
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
