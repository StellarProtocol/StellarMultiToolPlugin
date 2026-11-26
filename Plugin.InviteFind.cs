using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MultiTool;

// Add Player › "Find player" NAME search (approved mockup multitool-addplayer-name.html). Digits-only input never comes
// here — it takes the existing UID look-up (Plugin.InviteAddDialog.cs): the user found that a UID through SearchFriend
// did not work in Experiment's Motion Player. Ported from Experiment's Motion Player clone search
// (StellarExperimentPlugin\Plugin.MotionPlayer.GhostLua.cs, the "find" half only — no fetch / model):
//   Z.VMMgr.GetVM('friends_main').SearchFriend(text, token) (friends_main_vm.lua, the friend window's search) in a coro →
//   SearchFriendReply.data = PersonalData{charId, socialData}. Names can be DUPLICATE, so every match is kept (name,
//   charId, showId = the UID on name cards, class, level, avatar url). Class name =
//   Z.TableMgr.GetTable('ProfessionSystemTableMgr').GetRow(id, true).Name (`true` = no error log for a missing row);
//   C# falls back to "Class <id>". A read request only: nothing is sent to the found player.
//
// The game's search cooldown (Z.Global.Chat_RECFriendsListRefreshCD) is honoured AND set, exactly as Experiment does:
// friend_main_data's LastSearchTime plus our own copy (__stMtFindNext), so neither we nor the friend window can search
// inside it. The CD length comes back with the answer, so the Search pill can count it down ("Wait N s").
//
// Lua side: compiled ONCE into rawset globals (the game's _G.__newindex silently drops plain global writes —
// Lua-Injection-from-CSharp.md §3); each search is a one-line DoString. The answer is parked in __stMtFindRes
// ("OK …" / "CD …" / "ERR …") and read back by a 4 Hz IFramework.Every that exists ONLY while a search is pending
// (disposed on the answer / a 15 s timeout) — nothing runs while idle. charId stays the exact string Lua printed
// (int64 can exceed 2^53, so it must never round-trip through a C# double; Lua-Injection-from-CSharp.md).
public sealed partial class Plugin
{
    private const string InvFindResGlobal = "__stMtFindRes", InvFindLuaFlag = "__stMtFindLua1";
    private const float  InvFindWaitSec = 15f, InvFindPollSec = 0.25f;

    private const string InvFindMsgEmpty   = "Type a name or UID and press Search";
    private const string InvFindMsgBusy    = "Searching…";
    private const string InvFindMsgFailed  = "Search failed · try again";

    // One match (cold: built once per answer; the row Funcs read these cached strings).
    private sealed class InvMatch
    {
        public string Name = "", CharId = "", Sub = "", Url = "";
        public int Level;
        public bool AvatarSeeded;   // the avatar url was handed to _profile (first time the row is on screen)
    }

    private readonly List<InvMatch> _invMatches = new();
    private bool _invFindPending;
    private double _invFindUntil;           // pending timeout (TimeNow)
    private double _invFindCdUntil;         // next search the game allows (TimeNow)
    private IDisposable? _invFindPoll;      // the 4 Hz poll, alive only while pending
    private string _invFindMsg = InvFindMsgEmpty;   // the list area's message while there are no rows
    private bool _invFindScrollReset;
    private int _invFindWaitN = -1;
    private string _invFindWaitLabel = "Search";

    private double FindNow => _services.Framework.TimeNow;

    // Single-quoted Lua only (verbatim C# string; '\t' / '\n' are Lua escapes). Every global through rawset. Same body as
    // Experiment's __stMpFind minus the portrait picks: the avatar is the url download MultiTool already does
    // (InviteProfileResolver), keyed by charId.
    private const string InvFindLuaChunk = @"
rawset(_G, '__stMtFind', function(q)
  rawset(_G, '__stMtFindRes', nil)
  local ok, err = pcall(function()
    local now = Time.time
    local data = Z.DataMgr.Get('friend_main_data')
    local nextAt = rawget(_G, '__stMtFindNext') or 0
    if data and data.GetLastSearchTime then
      local g = data:GetLastSearchTime()
      if g and g > nextAt then nextAt = g end
    end
    if nextAt > now then
      rawset(_G, '__stMtFindRes', string.format('CD %.1f', nextAt - now))
      return
    end
    local cd = Z.Global.Chat_RECFriendsListRefreshCD or 10
    rawset(_G, '__stMtFindNext', now + cd)
    if data and data.SetLastSearchTime then data:SetLastSearchTime(now + cd) end
    Z.CoroUtil.create_coro_xpcall(function()
      local cs = Z.CancelSource.Rent()
      local ret = Z.VMMgr.GetVM('friends_main').SearchFriend(q, cs:CreateToken())
      cs:Recycle()
      if not ret then rawset(_G, '__stMtFindRes', 'ERR no reply') return end
      if ret.errCode ~= 0 then rawset(_G, '__stMtFindRes', 'ERR errCode=' .. tostring(ret.errCode)) return end
      local tbl = nil
      pcall(function() tbl = Z.TableMgr.GetTable('ProfessionSystemTableMgr') end)
      local out = {}
      for _, v in ipairs(ret.data or {}) do
        if v.charId ~= 0 then
          local sd = v.socialData or {}
          local url = ''
          pcall(function()
            local pr = sd.avatarInfo and sd.avatarInfo.profile
            if pr then url = tostring(pr.url or '') end
          end)
          local b = sd.basicData or {}
          local p = sd.professionData
          local pid = p and p.professionId
          local pname = ''
          if tbl and pid then
            local okr, row = pcall(tbl.GetRow, pid, true)
            if okr and row and row.Name then pname = tostring(row.Name) end
          end
          local cid = v.charId
          if math.type and math.type(cid) == 'integer' then cid = string.format('%d', cid) else cid = tostring(cid) end
          out[#out + 1] = table.concat({ (string.gsub(tostring(b.name), '[\t\n]', ' ')), cid, tostring(b.showId),
            tostring(pid), (string.gsub(pname, '[\t\n]', ' ')), tostring(b.level), (string.gsub(url, '[\t\n]', '')) }, '\t')
        end
      end
      rawset(_G, '__stMtFindRes', 'OK ' .. #out .. ' ' .. string.format('%.1f', cd) ..
        (#out > 0 and ('\n' .. table.concat(out, '\n')) or ''))
    end, function(e) rawset(_G, '__stMtFindRes', 'ERR ' .. tostring(e)) end)()
  end)
  if not ok then rawset(_G, '__stMtFindRes', 'ERR ' .. tostring(err)) end
end)
rawset(_G, '__stMtFindLua1', true)
";

    // Compile the function once (again after a Lua reset drops it).
    private bool InvFindLuaReady()
    {
        var lua = _services.Lua;
        if (!lua.Ready) return false;
        if (lua.TryReadGlobalBool(InvFindLuaFlag, out bool have) && have) return true;
        lua.DoString(InvFindLuaChunk);
        if (lua.TryReadGlobalBool(InvFindLuaFlag, out have) && have) return true;
        _services.Log.Warning("[InviteFind] Lua chunk didn't define its function (compile error?)");
        return false;
    }

    // Cooldown left in whole seconds (0 = may search). Cheap: no allocation.
    private int InvFindCdLeft()
    {
        double left = _invFindCdUntil - FindNow;
        return left > 0 ? (int)Math.Ceiling(left) : 0;
    }

    // The Search pill is recessed while a name search is out, or while the game's cooldown runs and the draft would
    // start a NAME search. A digits-only draft takes the UID look-up (a different request), so the cooldown never
    // blocks it.
    private bool InvSearchEnabled() => !_invFindPending && (_invAddDraftDigits || InvFindCdLeft() == 0);

    // "Search", or "Wait N s" while the cooldown blocks a name search. The string is rebuilt only when N changes.
    private string InvSearchLabel()
    {
        int left = _invFindPending || _invAddDraftDigits ? 0 : InvFindCdLeft();
        if (left != _invFindWaitN)
        {
            _invFindWaitN = left;
            _invFindWaitLabel = left > 0 ? "Wait " + left.ToString(CultureInfo.InvariantCulture) + " s" : "Search";
        }
        return _invFindWaitLabel;
    }

    // Start a name search (the caller already routed digits to the UID path).
    private void InvFindStart(string q)
    {
        if (_invFindPending || InvFindCdLeft() > 0) return;
        _invFindAsked = q;
        _invMatches.Clear();
        _invFindScrollReset = true;
        if (_services.ClientState.Phase != GamePhase.World || !InvFindLuaReady())
        {
            _invFindMsg = InvFindMsgFailed;
            return;
        }
        try { _services.Lua.DoString("__stMtFind('" + InvLuaEsc(q) + "')"); }
        catch (Exception ex)
        {
            _services.Log.Warning("[InviteFind] search call failed: " + ex.Message);
            _invFindMsg = InvFindMsgFailed;
            return;
        }
        _invFindMsg = InvFindMsgBusy;
        _invFindPending = true;
        _invFindUntil = FindNow + InvFindWaitSec;
        _invFindPoll?.Dispose();
        _invFindPoll = _services.Framework.Every(TimeSpan.FromSeconds(InvFindPollSec), InvFindPollTick);
    }

    private string _invFindAsked = "";

    private void StopInvFindPoll()
    {
        _invFindPoll?.Dispose();
        _invFindPoll = null;
    }

    // 4 Hz while pending only: take the parked answer (or time out), then stop polling.
    private void InvFindPollTick()
    {
        if (!_invFindPending) { StopInvFindPoll(); return; }
        string got;
        try { got = _services.Lua.ReadGlobalString(InvFindResGlobal) ?? ""; }
        catch (Exception ex) { _services.Log.Warning("[InviteFind] result read failed: " + ex.Message); got = "ERR read"; }
        if (got.Length == 0)
        {
            if (FindNow < _invFindUntil) return;
            got = "ERR no answer after " + InvFindWaitSec.ToString("0", CultureInfo.InvariantCulture) + " s";
        }
        else
        {
            try { _services.Lua.DoString("rawset(_G,'" + InvFindResGlobal + "',nil)"); } catch { }
        }
        _invFindPending = false;
        StopInvFindPoll();
        InvFindDone(got);
    }

    private void InvFindDone(string res)
    {
        _invFindScrollReset = true;
        if (res.StartsWith("CD ", StringComparison.Ordinal))
        {
            // The game's (or the friend window's) cooldown was still running: nothing was sent. The pill counts it down.
            if (float.TryParse(res.AsSpan(3), NumberStyles.Float, CultureInfo.InvariantCulture, out var left) && left > 0f)
                _invFindCdUntil = FindNow + left;
            _invFindMsg = InvFindMsgEmpty;
            return;
        }
        if (!res.StartsWith("OK", StringComparison.Ordinal))
        {
            _services.Log.Warning("[InviteFind] search: " + res);
            _invFindMsg = InvFindMsgFailed;
            return;
        }
        var lines = res.Split('\n');
        var head = lines[0].Split(' ');
        if (head.Length > 2 && float.TryParse(head[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var cd) && cd > 0f)
            _invFindCdUntil = FindNow + cd;
        _invMatches.Clear();
        for (int i = 1; i < lines.Length; i++)
        {
            var f = lines[i].Split('\t');
            if (f.Length < 7 || !AllDigits(f[1])) continue;   // charId must be the exact decimal id Lua printed
            int prof = InvInt(f[3]);
            string cls = f[4].Length > 0 ? f[4]
                : "Class " + (prof > 0 ? prof.ToString(CultureInfo.InvariantCulture) : "?");
            var m = new InvMatch
            {
                Name = f[0] == "nil" ? "Player" : f[0], CharId = f[1], Level = InvInt(f[5]), Url = f[6],
            };
            string show = f[2] == "nil" || f[2].Length == 0 ? f[1] : f[2];
            m.Sub = $"UID {show} · {cls} · Lv {m.Level.ToString(CultureInfo.InvariantCulture)}";
            _invMatches.Add(m);
        }
        _invFindMsg = _invMatches.Count > 0 ? "" : $"No player named \"{_invFindAsked}\"";
    }

    // Add on a result row: the same commit as the UID path — list (every switch off if new), then the per-player dialog
    // centred on this window. The found name is seeded into the name resolver so the roster row shows it at once (Seed
    // never overwrites an already-cached name).
    private void InvFindAdd(InvMatch m)
    {
        if (!AllDigits(m.CharId)) return;
        if (m.Name.Length > 0 && m.Name != "Player") _nameResolver.Seed(m.CharId, m.Name);
        EnsureInviteListed(m.CharId);
        OpenInviteCardDialog(m.CharId, WindowCenterer.CentreOf(_invAddWindow));
    }

    // Row avatar: the first time a row shows a match, hand its search-reply url to the profile resolver (the existing
    // MultiTool url download, no extra social RPC); then the decoded photo, else the shared default face.
    private object? InvMatchAvatar(InvMatch? m)
    {
        if (m == null) return null;
        if (!m.AvatarSeeded) { m.AvatarSeeded = true; _profile.Seed(m.CharId, m.Name, m.Level, m.Url); }
        var p = _profile.TryGet(m.CharId);
        return p?.AvatarTex ?? _profile.DefaultAvatarTex;
    }

    private static int InvInt(string s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v
        : double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)d : 0;

    // Lua single-quoted string body: backslash / quote escaped, control chars as \ddd.
    private static string InvLuaEsc(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            if (c == '\\' || c == '\'') sb.Append('\\').Append(c);
            else if (c < 0x20) sb.Append('\\').Append(((int)c).ToString("000", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
