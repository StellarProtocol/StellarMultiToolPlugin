using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace Stellar.MultiTool;

// One-time settings import for the Language Unlock, which moved here from MahiruUtility (2026-10-29). If MultiTool's own
// config has NONE of the six language keys yet, copy whichever of them MahiruUtility's config holds, so a user's
// toggles, exclude list and remembered language/voice carry over. Once any key exists in our config (imported, toggled,
// or recorded by the reconcile) this never runs again.
//
// Where the file is: the framework stores each plugin's config at <game root>\stellar\plugins\<guid>.config.json, with
// guid = the plugin's assembly name lowercased (PluginHost: asm.GetName().Name.ToLowerInvariant(); FileConfigStore +
// FrameworkPaths.PluginScanSubdir). The plugin API exposes no config path, so it is rebuilt from the same parts.
// Format: { "<section>": { key: value, … } } — Mahiru used GetSection("settings"), same as MultiTool.
//
// READ-ONLY on Mahiru's file (never written, never moved). Values are only STAGED (Set) + _cfgLoadDirty, so the
// constructor's single save persists them (MultiTool-Plugin.md §4.1). Missing / locked / malformed → one log line.
public sealed partial class Plugin
{
    private const string MahiruConfigFile    = "stellar.mahiruutility.config.json";   // "Stellar.MahiruUtility" lowercased
    private const string MahiruConfigSection = "settings";

    private enum LangKeyKind { Bool, Int, Text }

    private static readonly (string Key, LangKeyKind Kind)[] LanguageConfigKeys =
    {
        ("lang_unlock",          LangKeyKind.Bool),
        ("lang_exclude",         LangKeyKind.Text),
        ("lang_index",           LangKeyKind.Int),
        ("voice_unlock_enabled", LangKeyKind.Bool),
        ("voice_unlock",         LangKeyKind.Text),
        ("voice_index",          LangKeyKind.Int),
    };

    private void ImportLanguageSettingsFromMahiru()
    {
        // Presence probe: Get<object> deserializes any stored JSON value to a non-null JsonElement, and returns the
        // default (null) only when the key is absent — IConfigSection has no Contains.
        foreach (var (key, _) in LanguageConfigKeys)
            if (_cfg.Get<object>(key, null) != null) return;

        string? path = FindMahiruConfig();
        if (path == null) { _services.Log.Info("[Lang] import: no MahiruUtility config found — using defaults"); return; }

        try
        {
            string text;
            // Shared read: the framework (or the old Mahiru build, if still installed) may have it open.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs))
                text = sr.ReadToEnd();

            if (JsonNode.Parse(text)?[MahiruConfigSection] is not JsonObject section)
            {
                _services.Log.Info($"[Lang] import: \"{MahiruConfigSection}\" section not found in {path} — using defaults");
                return;
            }

            var imported = new List<string>();
            foreach (var (key, kind) in LanguageConfigKeys)
            {
                if (section[key] is not JsonValue v) continue;
                try
                {
                    switch (kind)
                    {
                        case LangKeyKind.Bool: { bool b = v.GetValue<bool>();     _cfg.Set<bool>(key, b);   imported.Add($"{key}={b}"); break; }
                        case LangKeyKind.Int:  { int i = v.GetValue<int>();       _cfg.Set<int>(key, i);    imported.Add($"{key}={i}"); break; }
                        default:               { string s = v.GetValue<string>(); _cfg.Set<string>(key, s); imported.Add($"{key}=\"{s}\""); break; }
                    }
                }
                catch { _services.Log.Warning($"[Lang] import: {key} has an unexpected value ({v.ToJsonString()}) — skipped"); }
            }

            if (imported.Count == 0) { _services.Log.Info($"[Lang] import: no language settings in {path} — using defaults"); return; }
            _cfgLoadDirty = true;   // staged only — the constructor's single save persists it
            _services.Log.Info($"[Lang] imported from MahiruUtility: {string.Join(", ", imported)}");
        }
        catch (Exception ex)
        {
            _services.Log.Info($"[Lang] import: couldn't read {path} ({ex.GetType().Name}: {ex.Message}) — using defaults");
        }
    }

    // Candidates, first existing wins: the framework's own root (the game exe dir = AppContext.BaseDirectory under
    // BepInEx, as AccountSwitcher/Maestro rely on) + stellar\plugins; then the ancestors of our own DLL, which the
    // framework loads from somewhere under stellar\plugins (normally stellar\plugins\multitool\).
    private static string? FindMahiruConfig()
    {
        var candidates = new List<string>();
        try
        {
            if (!string.IsNullOrEmpty(AppContext.BaseDirectory))
                candidates.Add(Path.Combine(AppContext.BaseDirectory, "stellar", "plugins", MahiruConfigFile));
        }
        catch { }
        try
        {
            var dir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            for (int up = 0; up < 3 && !string.IsNullOrEmpty(dir); up++)
            {
                dir = Path.GetDirectoryName(dir);
                if (!string.IsNullOrEmpty(dir)) candidates.Add(Path.Combine(dir, MahiruConfigFile));
            }
        }
        catch { }

        foreach (var c in candidates)
            try { if (File.Exists(c)) return c; } catch { }
        return null;
    }
}
