namespace Stellar.MultiTool;

// The Lua half of "Reduce party list updates" (Plugin.PartyThrottle.cs). Ported from the in-game-validated Experiment
// copy (StellarExperimentPlugin Plugin.PartyThrottle.Lua.cs @ 632d8a8) with OWN global names (__stellarMtTvThr* /
// __mtTvThr*) and OWN class-table park keys (__stellarMtThrOMd / __stellarMtThrOBi), so both plugins can run at once.
// Sent through DoString ONCE per Lua state; C# then talks to it only via one-line calls. Game refs (release_3.7):
// ui/view/team_view.lua updateBuffInfo :746-775, addMemberAttrWatcher :796-837 (watcher closures :807 / :832),
// updateMemberData :989-1013; utility/timer_manager.lua StartTimer/StopTimer; System/Timer.lua (loop -1 = forever).
//
// Design notes (the why behind each line):
// - require('ui/view/team_view') with SLASHES = the same cached table mainui_view.lua:74 uses (a dotted path would
//   load a phantom second copy — require-separator rule). class() instances look methods up on the class table, and
//   the watcher closures call self:updateMemberData / self:updateBuffInfo by name → swapping the class field applies to
//   LIVE views at once (Performance-Levers.md "Lua attr-change watchers in raids").
// - updateMemberData passes nil / isForce / no-charId data straight through: the isForce path adds watchers (:999-1001)
//   and must not be delayed. Wrappers also pass through while S.on is false, because something may still hold (or sit
//   on top of) a wrapper reference after Off — the Team.UpdateMemberData event (:604), or another mod's wrapper.
//   S.oMd / S.oBi are therefore never cleared once set.
// - Own Z.TimerMgr instance, NOT the view's self.timerMgr (UIBase clears that on hide, ui_base.lua:75).
// - Two mark sets swapped on flush (marks arriving during a flush land in the other set — no insert-while-iterating),
//   weak-keyed by view so a destroyed view is collected; one reused {charId} arg table → no per-flush allocation once
//   warm. Every original call is pcall'd; only the FIRST error is kept (S.err) and C# logs it once.
// - ⚠️ EVERY global write is rawset(_G, ...): the game's _G has a __newindex that silently DROPS plain new global
//   writes (Lua-Injection-from-CSharp.md §3). Reads may stay plain/rawget.
//
// SHARING THE METHOD WITH ANOTHER WRAPPER (e.g. the Experiment plugin's copy of this throttle, or any other mod):
// - Install parks WHATEVER is in the class field (the game original, or a foreign wrapper) and calls exactly that from
//   the flush; Off puts back exactly what was parked — but only while OUR wrapper is still the one in the field.
// - If the field holds something else, `wraps(cur, ours)` decides: a function that holds our wrapper (as an upvalue, or
//   as a field of an upvalue table — how both throttle copies keep their original) is a layer ON TOP of us → we are
//   still in the call chain, so we neither re-park (parking it would make its flush call ours and ours call it: an
//   endless re-mark, HP bars would never update) nor restore over it (that would cut it out). Otherwise we were
//   replaced (e.g. the other copy turned Off and put back the original it parked before us) → re-park + re-install.
// - Without the debug library `wraps` answers "yes" (the conservative side: worst case our throttle goes inert).
// - Ordering caveat: the Experiment copy re-installs its wrapper every 10 s from ITS park key, which can drop ours from
//   the chain until our own 10 s ensure puts it back on top (harmless ping-pong). While stacked, an update may be held
//   by both throttles → up to two flush periods late. Only one-level detection: a wrapper that keeps the original in a
//   nested helper closure is not recognised (it is then treated as "replaced" → re-park; avoid stacking such mods).
public sealed partial class Plugin
{
    private const string PartyThrLuaDefine =
@"local S = rawget(_G, '__stellarMtTvThr')
if not S then
  local function weak() return setmetatable({}, { __mode = 'k' }) end
  S = { mIn = 0, mOut = 0, pass = 0, on = false, arg = {}, weak = weak,
        hp = weak(), hpB = weak(), bu = weak(), buB = weak() }
  rawset(_G, '__stellarMtTvThr', S)
end

S.wMd = S.wMd or function(self, d)
  if not S.on or type(d) ~= 'table' or d.isForce or d.charId == nil then
    if S.on then S.pass = S.pass + 1 end
    return S.oMd(self, d)
  end
  local t = S.hp[self]
  if not t then t = {} S.hp[self] = t end
  t[d.charId] = true
  S.mIn = S.mIn + 1
end

S.wBi = S.wBi or function(self, charId)
  if not S.on or charId == nil then return S.oBi(self, charId) end
  local t = S.bu[self]
  if not t then t = {} S.bu[self] = t end
  t[charId] = true
  S.mIn = S.mIn + 1
end

S.flush = S.flush or function()
  local cur = S.hp S.hp = S.hpB S.hpB = cur
  local md, arg = S.oMd, S.arg
  for view, ids in pairs(cur) do
    for id in pairs(ids) do
      ids[id] = nil
      arg.charId = id
      local ok, e = pcall(md, view, arg)
      S.mOut = S.mOut + 1
      if not ok and not S.err then S.err = 'updateMemberData: ' .. tostring(e) end
    end
  end
  arg.charId = nil
  cur = S.bu S.bu = S.buB S.buB = cur
  local bi = S.oBi
  for view, ids in pairs(cur) do
    for id in pairs(ids) do
      ids[id] = nil
      local ok, e = pcall(bi, view, id)
      S.mOut = S.mOut + 1
      if not ok and not S.err then S.err = 'updateBuffInfo: ' .. tostring(e) end
    end
  end
end

S.tick = S.tick or function()
  local ok, e = pcall(S.flush)
  if not ok and not S.err then S.err = 'flush: ' .. tostring(e) end
end

-- Does f hold w (directly as an upvalue, or as a field of an upvalue table)? = f is a wrapper layered on top of w.
S.wraps = S.wraps or function(f, w)
  local gu = debug and debug.getupvalue
  if not gu then return true end
  for i = 1, 32 do
    local n, v = gu(f, i)
    if n == nil then break end
    if v == w then return true end
    if type(v) == 'table' then
      local c = 0
      for _, x in next, v do
        if x == w then return true end
        c = c + 1
        if c > 64 then break end
      end
    end
  end
  return false
end

-- Puts our wrapper w into TV[name] (parking what was there in S[key] + TV[mark]); see the class note for the rules.
S.hook = S.hook or function(TV, name, mark, w, key)
  local cur = rawget(TV, name)
  if cur == w then return end
  if type(cur) ~= 'function' then error('no original ' .. name) end
  if S[key] ~= nil and S[key] ~= cur and S.wraps(cur, w) then return end
  S[key] = cur
  rawset(TV, mark, cur)
  rawset(TV, name, w)
end

S.unhook = S.unhook or function(TV, name, mark, w, key)
  if rawget(TV, name) == w and S[key] ~= nil then
    rawset(TV, name, S[key])
    rawset(TV, mark, nil)
  end
end

rawset(_G, '__stellarMtTvThrEnsure', function(hz)
  rawset(_G, '__mtTvThrOk', nil)
  local ok, e = pcall(function()
    local TV = require('ui/view/team_view')
    if type(TV) ~= 'table' then error('team_view is not a table') end
    S.hook(TV, 'updateMemberData', '__stellarMtThrOMd', S.wMd, 'oMd')
    S.hook(TV, 'updateBuffInfo',   '__stellarMtThrOBi', S.wBi, 'oBi')
    if type(S.oMd) ~= 'function' or type(S.oBi) ~= 'function' then error('originals not parked') end
    S.TV = TV
    S.on = true
    if not S.tm then S.tm = (Z.TimerMgr).new() end
    if S.timer == nil or not S.timer.running or S.hz ~= hz then
      if S.timer then S.tm:StopTimer(S.timer) end
      S.hz = hz
      S.timer = S.tm:StartTimer(S.tick, 1 / hz, -1)
      if S.timer == nil then error('StartTimer returned nil') end
    end
  end)
  if ok then rawset(_G, '__mtTvThrOk', true)
  else
    local msg = 'ensure: ' .. tostring(e)
    rawset(_G, '__mtTvThrErr', msg)
    if not S.err then S.err = msg end
  end
end)

rawset(_G, '__stellarMtTvThrOff', function(doFlush)
  local ok, e = pcall(function()
    if S.timer then S.tm:StopTimer(S.timer) S.timer = nil end
    S.hz = nil
    if doFlush then pcall(S.flush)
    else S.hp = S.weak() S.hpB = S.weak() S.bu = S.weak() S.buB = S.weak() end
    local TV = S.TV
    if TV then
      S.unhook(TV, 'updateMemberData', '__stellarMtThrOMd', S.wMd, 'oMd')
      S.unhook(TV, 'updateBuffInfo',   '__stellarMtThrOBi', S.wBi, 'oBi')
    end
    S.on = false
  end)
  if not ok and not S.err then S.err = 'off: ' .. tostring(e) end
end)

rawset(_G, '__stellarMtTvThrStats', function()
  rawset(_G, '__mtTvThrIn', S.mIn) rawset(_G, '__mtTvThrOut', S.mOut)
  rawset(_G, '__mtTvThrPass', S.pass) rawset(_G, '__mtTvThrErr', S.err)
  S.mIn = 0 S.mOut = 0 S.pass = 0
end)

rawset(_G, '__stellarMtTvThrDefined', true)";
}
