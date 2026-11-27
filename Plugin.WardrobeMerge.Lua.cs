using System.Globalization;

namespace Stellar.MultiTool;

// The Lua half of "Merge clothes and accessory tabs" (Plugin.WardrobeMerge.cs). Sent through DoString ONCE per Lua
// state; C# then only sends one-line calls. Game refs (release_4.7, ui\view\fashion_system_view.lua): regionDataDict
// :9-45, OnActive :59 (initLoopTab :77 before initFirstTab :79), OnDeActive :97, initLoopTab/clearLoopTab :589-599,
// tab listeners :631-671, refreshSecondListSelect :719, refreshSecondList :734, OnSwitchRegion :818,
// changeCameraFocus :938, onFashionSaveConfirmItemClick :1102. Research: Fashion-Wardrobe-Screen.md.
//
// Design notes (the why behind each line):
// - require('ui/view/fashion_system_view') with SLASHES = the table ui_manager.lua:121 instantiates the view from
//   (require-separator rule, Lua-Injection-from-CSharp.md §8). class() instances look methods up on the class table
//   (zutil\class.lua setmetatableindex), so swapping a class field reaches the cached view instance too.
// - regionDataDict is a file-local upvalue → the two lists are rebuilt here, copied 1:1 from :9-40 (IsFocus included,
//   it drives the head/body camera zoom).
// - Wrappers are inert unless THIS open created a clone (self.__mtWm.ready): an open that started before the patch, a
//   failed clone, fashion_face_window (subclass of the dotted-path copy; guarded by viewConfigKey anyway) and the
//   weapon tab all run the game's own code. Every merged path is pcall'd and falls back to the original on error;
//   only the FIRST error is kept (W.err → __mtWmErr) and C# logs it once.
// - The clone is made BEFORE the original OnActive, i.e. before this open's initLoopTab → the copied loop_tab holds no
//   live rows of this session. It is a second ZLoopListView with its own loopListView (same constructor args as
//   initLoopTab, so the PC prefab "fashion_item_tab_pc" is picked the same way); rows are the game's own
//   fashion_second_item (eye toggle, red dots, icons) and call view:OnSwitchRegion like the original column's rows.
// - The ornament tab is folded in, not removed: whatever turns it on (a cached firstTab = EOrnament, or
//   onFashionSaveConfirmItemClick setting tog_ornament.isOn) ends in refreshSecondList, which flips back to the
//   clothes tab and then selects the region in the right column. So onFashionSaveConfirmItemClick needs no wrapper.
// - Unpatch only puts an original back while OUR wrapper is still the one in the field (another mod may sit on top).
//   Wrapper closures are kept across re-defines (`W.x = W.x or …`) so the "is it ours" identity check never parks our
//   own wrapper as an "original" (that would recurse forever).
// - ⚠️ EVERY global write is rawset(_G, ...): the game's _G __newindex silently DROPS plain new global writes.
public sealed partial class Plugin
{
    // Horizontal gap (canvas units) between the game's slot column and the cloned accessory column. Tune in game from
    // the "[GameUI] Wardrobe merge: loop_tab …" log line.
    private const int WmCloneGap = 8;

    // Model screen X (0..1 viewport) while the merged columns are shown. The game's initFashionCamera (:218-234) puts
    // the model at 0.4, which reads off-centre to the left once the second column takes up space; 0.50 was picked by the
    // user (0.46 tested live first: centred, head-zoom framing fine). All tabs incl. weapon — no per-tab switching.
    // Unpatch on an open Wardrobe → back to 0.4.
    private const double WmModelScreenX = 0.50;

    private static readonly string WmLuaDefine =
        "local GAP = " + WmCloneGap.ToString(CultureInfo.InvariantCulture) + "\n" +
        "local MODEL_X = " + WmModelScreenX.ToString(CultureInfo.InvariantCulture) + "\n" +
@"local W = rawget(_G, '__stellarMtWm')
if not W then W = {} rawset(_G, '__stellarMtWm', W) end
W.gap = GAP
W.modelX = MODEL_X

local function note(msg)
  if W.err then return end
  W.err = msg
  rawset(_G, '__mtWmErr', msg)
  rawset(_G, '__mtWmNews', true)
end
W.note = note

local function inList(list, region)
  if region == nil or list == nil then return nil end
  for i = 1, #list do if list[i].region == region then return i end end
  return nil
end
W.inList = inList

local function f1(x) return string.format('%.1f', tonumber(x) or -1) end

W.buildLists = function()
  local R = E.FashionRegion
  W.CL = {
    {region = R.Suit, IsFocus = false}, {region = R.UpperClothes, IsFocus = false},
    {region = R.Pants, IsFocus = false}, {region = R.Gloves, IsFocus = false},
    {region = R.Shoes, IsFocus = false},
  }
  W.OR = {
    {region = R.Headwear, IsFocus = true}, {region = R.HeadWearSecond, IsFocus = true},
    {region = R.FaceMask, IsFocus = true}, {region = R.MouthMask, IsFocus = true},
    {region = R.Back, IsFocus = false}, {region = R.Tail, IsFocus = false},
    {region = R.Earrings, IsFocus = true}, {region = R.Necklace, IsFocus = true},
    {region = R.Ring, IsFocus = true},
  }
end

-- Clone loop_tab next to itself + give it its own loopListView. State lands in self.__mtWm as it is built, so a
-- failure part-way is undone by W.drop.
W.mkClone = function(self)
  local ub = self.uiBinder
  local lt = ub.loop_tab
  -- Normalise the original's visibility first (the cached prefab may still be hidden from a weapon-tab close), so the
  -- copy starts visible whatever mechanism Ref:SetVisible uses. initFirstTab sets the original's real state next.
  pcall(function() ub.Ref:SetVisible(lt, true) end)
  local tr = lt.transform
  local parent = tr.parent
  -- Object.Instantiate(Component) clones the whole GameObject and returns the copy's component (here: the clone's
  -- ZLoopListView). If tolua hands back the GameObject instead, look the component up by name.
  local c = UnityEngine.GameObject.Instantiate(lt)
  if c == nil then error('Instantiate returned nil') end
  local st = {}
  self.__mtWm = st
  st.go = c.gameObject
  local okLv, fnLv = pcall(function() return c.InitListView end)
  if not okLv or fnLv == nil then c = st.go:GetComponent('ZLoopListView') end
  if c == nil then error('clone has no ZLoopListView') end
  st.go.name = 'loop_tab_mt_ornament'
  local ct = st.go.transform
  ct:SetParent(parent, false)
  ct:SetSiblingIndex(tr:GetSiblingIndex() + 1)
  ct.localScale = tr.localScale
  local ap, r = tr.anchoredPosition, tr.rect
  ct.anchoredPosition = Vector2.New(ap.x + r.width + W.gap, ap.y)
  st.go:SetActive(true)
  pcall(function() ub.tog_ornament.Ref.UIComp:SetVisible(false) end)
  st.list = W.LLV.new(self, c, W.ITEM, 'fashion_item_tab', true)
  st.list:Init({})
  st.ready = true
  pcall(function()
    local sd, cap, cr = tr.sizeDelta, ct.anchoredPosition, ct.rect
    rawset(_G, '__mtWmDiag', 'loop_tab parent=' .. tostring(parent.name) .. ' children=' .. tostring(parent.childCount) ..
      ' ap=(' .. f1(ap.x) .. ',' .. f1(ap.y) .. ') size=(' .. f1(sd.x) .. ',' .. f1(sd.y) ..
      ') rect=(' .. f1(r.width) .. ',' .. f1(r.height) .. ') anchors=(' .. f1(tr.anchorMin.x) .. ',' .. f1(tr.anchorMin.y) ..
      ')-(' .. f1(tr.anchorMax.x) .. ',' .. f1(tr.anchorMax.y) .. ') pivot=(' .. f1(tr.pivot.x) .. ',' .. f1(tr.pivot.y) ..
      ') | clone ap=(' .. f1(cap.x) .. ',' .. f1(cap.y) .. ') rect=(' .. f1(cr.width) .. ',' .. f1(cr.height) ..
      ') gap=' .. tostring(W.gap) .. ' pc=' .. tostring(Z.IsPCUI))
    rawset(_G, '__mtWmNews', true)
  end)
end

-- Undo one open's clone: UnInit its list (rows UnInit, C# UnInitListView), destroy the copy, show the ornament tab.
W.drop = function(self)
  local st = self.__mtWm
  self.__mtWm = nil
  if not st then return end
  if st.list then pcall(st.list.UnInit, st.list) end
  if st.go then
    pcall(function() st.go:SetActive(false) end)   -- hidden even if Destroy is unavailable (cached prefab reuse)
    pcall(UnityEngine.GameObject.Destroy, st.go)
  end
  pcall(function() self.uiBinder.tog_ornament.Ref.UIComp:SetVisible(true) end)
end

local function merged(self)
  local st = self.__mtWm
  if st and st.ready then return st end
  return nil
end

W.wOnActive = W.wOnActive or function(self, ...)
  if W.on and self.viewConfigKey == 'fashion_system' and self.__mtWm == nil then
    local inLogin = false
    pcall(function() inLogin = Z.StageMgr.GetIsInLogin() end)
    if not inLogin then
      local ok, e = pcall(W.mkClone, self)
      if not ok then note('clone: ' .. tostring(e)) pcall(W.drop, self) end
    end
  end
  return W.O.OnActive(self, ...)
end

W.wOnDeActive = W.wOnDeActive or function(self, ...)
  if self.__mtWm then pcall(W.drop, self) end
  return W.O.OnDeActive(self, ...)
end

W.wRefreshSecondList = W.wRefreshSecondList or function(self, ...)
  local st = merged(self)
  if not st then return W.O.refreshSecondList(self, ...) end
  local T = E.EFashionFirstTab
  if self.curFirstTab_ == T.EOrnament then
    -- Fold the (hidden) ornament tab back into the clothes tab.
    self.curFirstTab_ = T.EClothes
    pcall(function()
      self.uiBinder.tog_clothes.tog_tab_select:SetIsOnWithoutCallBack(true)
      self.uiBinder.tog_ornament.tog_tab_select:SetIsOnWithoutCallBack(false)
    end)
  end
  if self.curFirstTab_ ~= T.EClothes then return W.O.refreshSecondList(self, ...) end
  local ok, e = pcall(function()
    self.fashionVM_.RefreshFashionHideRegion()
    self.secondLoopListView_:RefreshListView(W.CL, false)
    st.list:RefreshListView(W.OR, false)
    st.go:SetActive(true)
  end)
  if not ok then note('refreshSecondList: ' .. tostring(e)) return W.O.refreshSecondList(self, ...) end
end

W.wRefreshSelect = W.wRefreshSelect or function(self, region, ...)
  local st = merged(self)
  if not st or self.curFirstTab_ ~= E.EFashionFirstTab.EClothes then
    return W.O.refreshSecondListSelect(self, region, ...)
  end
  local ok, e = pcall(function()
    local j = inList(W.OR, region)
    self.secondLoopListView_:ClearAllSelect()
    st.list:ClearAllSelect()
    if j then st.list:SetSelected(j)
    else self.secondLoopListView_:SetSelected(inList(W.CL, region) or 1) end
  end)
  if not ok then note('refreshSecondListSelect: ' .. tostring(e)) return W.O.refreshSecondListSelect(self, region, ...) end
end

-- Row click in either column (fashion_second_item OnSelected → view:OnSwitchRegion): clear the OTHER column so only
-- one slot is selected. ClearAllSelect fires OnSelected(false) only, which never calls back in here. The weapon tab
-- (its listener sets curFirstTab_ then calls this) hides the clone, as :668 hides loop_tab.
W.wSwitch = W.wSwitch or function(self, region, ...)
  local st = merged(self)
  if st then
    local ok, e = pcall(function()
      local T = E.EFashionFirstTab
      if self.curFirstTab_ == T.EWeaponSkin then st.go:SetActive(false)
      elseif self.curFirstTab_ == T.EClothes and region ~= nil then
        if inList(W.OR, region) then self.secondLoopListView_:ClearAllSelect()
        else st.list:ClearAllSelect() end
      end
    end)
    if not ok then note('OnSwitchRegion: ' .. tostring(e)) end
  end
  return W.O.OnSwitchRegion(self, region, ...)
end

-- Same as :938-955, but the region is looked up in BOTH columns (the original only searches curFirstTab_'s list).
W.wCamera = W.wCamera or function(self, ...)
  local st = merged(self)
  if not st or self.curFirstTab_ == E.EFashionFirstTab.EWeaponSkin then return W.O.changeCameraFocus(self, ...) end
  local ok, e = pcall(function()
    local isOnHead = false
    local i = inList(W.CL, self.curRegion_)
    if i then isOnHead = W.CL[i].IsFocus
    else
      local j = inList(W.OR, self.curRegion_)
      if j then isOnHead = W.OR[j].IsFocus end
    end
    if isOnHead then Z.UnrealSceneMgr:DoCameraAnimLookAtOffset('faceFocusHead', Vector3.New(0, self.offset_.x, 0))
    else Z.UnrealSceneMgr:DoCameraAnimLookAtOffset('faceFocusBody', Vector3.New(0, self.offset_.y, 0)) end
  end)
  if not ok then note('changeCameraFocus: ' .. tostring(e)) return W.O.changeCameraFocus(self, ...) end
end

-- initFashionCamera runs inside OnActive (:73), i.e. after our OnActive wrapper built the clone → merged() is already
-- true for this open. Only the screen X is overridden; zoom ranges / look-at offsets stay the game's.
W.wInitCamera = W.wInitCamera or function(self, ...)
  local r = W.O.initFashionCamera(self, ...)
  if merged(self) then
    local ok, e = pcall(function() Z.UnrealSceneMgr:SetUnrealCameraScreenXY(Vector2.New(W.modelX, 0.5)) end)
    if not ok then note('initFashionCamera: ' .. tostring(e)) end
  end
  return r
end

W.wrappers = {
  OnActive = W.wOnActive, OnDeActive = W.wOnDeActive,
  refreshSecondList = W.wRefreshSecondList, refreshSecondListSelect = W.wRefreshSelect,
  OnSwitchRegion = W.wSwitch, changeCameraFocus = W.wCamera, initFashionCamera = W.wInitCamera,
}

-- Idempotent: a field that already holds our wrapper keeps its parked original. Sets __mtWmPatched on success.
rawset(_G, '__stellarMtWmPatch', function()
  rawset(_G, '__mtWmPatched', nil)
  local ok, e = pcall(function()
    local V = require('ui/view/fashion_system_view')
    if type(V) ~= 'table' then error('fashion_system_view is not a table') end
    W.LLV = W.LLV or require('ui.component.loop_list_view')
    W.ITEM = W.ITEM or require('ui.component.fashion.fashion_second_item')
    if not W.CL then W.buildLists() end
    local O = (W.V == V and W.O) or {}
    for name, w in pairs(W.wrappers) do
      local cur = rawget(V, name)
      if cur ~= w then
        if type(cur) ~= 'function' then error('no original ' .. name) end
        O[name] = cur
      end
      if type(O[name]) ~= 'function' then error('original not parked: ' .. name) end
    end
    W.O = O
    W.V = V
    for name, w in pairs(W.wrappers) do rawset(V, name, w) end
    W.on = true
  end)
  if ok then rawset(_G, '__mtWmPatched', true) else note('patch: ' .. tostring(e)) end
end)

-- Restore the originals. A Wardrobe open right now is put back to the game's two tabs at once: clone dropped, and the
-- ornament tab re-selected if an accessory slot was the current one.
rawset(_G, '__stellarMtWmUnpatch', function()
  rawset(_G, '__mtWmPatched', nil)
  W.on = false
  local ok, e = pcall(function()
    local V = W.V
    if not V then return end
    local v = Z.UIMgr:GetView('fashion_system')
    local live = v ~= nil and v.__mtWm ~= nil
    if live then W.drop(v) end
    for name, w in pairs(W.wrappers) do
      if rawget(V, name) == w and W.O[name] then rawset(V, name, W.O[name]) end
    end
    -- Model back to the game's own screen X (initFashionCamera :226), any tab.
    if live then pcall(function() Z.UnrealSceneMgr:SetUnrealCameraScreenXY(Vector2.New(0.4, 0.5)) end) end
    if live and v.IsActive and v.uiBinder and v.curFirstTab_ == E.EFashionFirstTab.EClothes then
      if inList(W.OR, v.curRegion_) then
        v.uiBinder.tog_ornament.tog_tab_select.isOn = true
      else
        v:refreshSecondList()
        v:refreshSecondListSelect(v.curRegion_)
      end
    end
  end)
  if not ok then note('unpatch: ' .. tostring(e)) end
end)

rawset(_G, '__stellarMtWmDefined', true)";
}
