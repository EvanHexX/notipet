namespace Notipet.Sound;

// Turns a platform sound name ("Notification.Default" on Windows) into a file
// the engines can play. The Windows daemon reads HKCU\AppEvents; a macOS front
// end would map to /System/Library/Sounds. SoundResolver uses whichever one
// the host installs at startup.
internal interface ISoundCatalog
{
    // The file behind an alias, or null when it does not resolve - the
    // fallback engine may still be able to play the alias by name.
    string? ResolvePath(string? alias);

    // Something that is always there, for when nothing else resolved.
    string? FallbackPath();
}

// No platform sounds: every alias is unresolved. The default until a host
// installs its own catalog, and what a headless test without one sees.
internal sealed class NoSoundCatalog : ISoundCatalog
{
    public static readonly NoSoundCatalog Instance = new();
    public string? ResolvePath(string? alias) => null;
    public string? FallbackPath() => null;
}
