namespace Stellar.MultiTool;

// Instant Dialog — "No text animation" in the Language & Text window (Plugin.LanguageWindow.cs): NPC conversation text
// appears at once instead of letter by letter. Mechanism: InstantDialogPatch (Knowledge Base\NPC-Dialog-Typewriter.md).
// Unlike the language unlocks this applies live: the patch is installed once at start whatever the setting, and the
// toggle only flips InstantDialogPatch.Enabled, so it takes effect on the next dialog line.
// Config: "instant_dialog_enabled" (bool, default off).
public sealed partial class Plugin
{
    private bool _instantDialog;

    // Called from InitLanguageUnlock (same tool window), independent of the language unlocks being on.
    private void InitInstantDialog()
    {
        _instantDialog = _cfg.Get<bool>("instant_dialog_enabled", false);
        InstantDialogPatch.Enabled = _instantDialog;
        // The created Harmony handle auto-unpatches on plugin dispose (owned by IHarmonyHost), so it is not stored.
        try { InstantDialogPatch.Install(_services.Harmony.Create("instantdialog"), _services.Log.Info); }
        catch (System.Exception ex) { _services.Log.Warning($"[InstantDialog] install failed: {ex.Message}"); }
        _services.Log.Info($"[InstantDialog] enabled={_instantDialog} available={InstantDialogPatch.Available}");
    }

    // One save per click (MultiTool-Plugin.md §4.1).
    private void OnInstantDialogToggle(bool on)
    {
        _instantDialog = on;
        InstantDialogPatch.Enabled = on;
        _cfg.Set<bool>("instant_dialog_enabled", on);
        _cfg.Save();
    }
}
