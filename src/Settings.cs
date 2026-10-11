using ModSettings;

namespace ObjectSinkFix;

internal static class ModSettingsIntegration
{
    internal static void Register()
    {
        var settings = Settings.Instance;
        settings.AddToModSettings("Object Sink Fix");
        settings.Apply();
    }
}

internal sealed class Settings : JsonModSettings
{
    internal static Settings Instance { get; } = new Settings();

    [Section("Recovery")]
    [Name("Also restore objects you may have destroyed")]
    [Description("Also brings back searched and emptied containers, containers with no saved contents, and furniture lost from a difficulty variant pair, all empty. This will likely restore intentionally destroyed objects from a scene's authored layout.")]
    public bool RestoreDestroyable = false;

    protected override void OnConfirm()
    {
        base.OnConfirm();
        Apply();
    }

    internal void Apply()
    {
        ObjectSinkFixMod.RecoverDestroyable = RestoreDestroyable;
    }
}
