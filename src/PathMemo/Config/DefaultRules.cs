using PathMemo.Analysis;
using PathMemo.Audit;

namespace PathMemo.Config;

/// <summary>
/// The built-in cleanup rules, exactly the table in README section 7.2.
/// </summary>
/// <remarks>
/// <para>
/// A list of records, not a switch and not a plugin interface. Every one of these is
/// something a user could have written in <c>config.json</c>, which is the property that
/// keeps the rule language honest: there is no condition here that a custom rule cannot
/// express (README section 7.4).
/// </para>
/// <para>
/// <b>What is deliberately absent</b> (README section 7.2, "Deliberately not rules"):
/// <c>hiberfil.sys</c>, <c>pagefile.sys</c> and <c>swapfile.sys</c> are in use and
/// undeletable - they are audit findings with a <c>powercfg</c> command. <c>WinSxS</c>
/// breaks the system when touched by hand; <c>DISM</c> only. <c>C:\Windows\Installer</c>
/// breaks uninstall and updates. <c>System Volume Information</c> is the VSS API's.
/// <c>.git\objects</c> is the repository. A rule that matched any of them would be a rule
/// that eventually deletes them.
/// </para>
/// </remarks>
internal static class DefaultRules
{
    private const long Mb = 1L << 20;
    private const long Gb = 1L << 30;

    internal static IReadOnlyList<ReclaimRule> All { get; } =
    [
        new ReclaimRule
        {
            Id = "dev.node_modules",
            Patterns = [@"**\node_modules"],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Redownload,
            What = "installed npm packages",
            Command = "npm ci",
        },
        new ReclaimRule
        {
            Id = "dev.npm_cache",
            Patterns = [@"%LOCALAPPDATA%\npm-cache", @"~\.npm\_cacache"],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Redownload,
            What = "npm's download cache",
            Command = "npm cache clean --force",
        },
        new ReclaimRule
        {
            Id = "dev.pnpm_store",
            Patterns = [@"%LOCALAPPDATA%\pnpm\store"],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Redownload,
            What = "pnpm's content-addressed store",
            Command = "pnpm store prune",
        },
        new ReclaimRule
        {
            Id = "dev.yarn_cache",
            Patterns = [@"%LOCALAPPDATA%\Yarn\Cache"],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Redownload,
            What = "yarn's package cache",
            Command = "yarn cache clean",
        },
        new ReclaimRule
        {
            Id = "dev.nuget",
            Patterns = [@"~\.nuget\packages", @"%LOCALAPPDATA%\NuGet\v3-cache"],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Redownload,
            What = "NuGet packages and the HTTP cache",
            Command = "dotnet nuget locals all --clear",
        },
        new ReclaimRule
        {
            Id = "dev.dotnet_artifacts",
            Patterns = [@"**\bin\Debug", @"**\bin\Release", @"**\obj"],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Rebuild,
            What = ".NET build output",
        },
        new ReclaimRule
        {
            Id = "dev.gradle",
            Patterns = [@"~\.gradle\caches"],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Redownload,
            What = "Gradle's dependency and build cache",
            Command = "gradle --stop, then delete",
        },
        new ReclaimRule
        {
            Id = "dev.maven",
            Patterns = [@"~\.m2\repository"],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Redownload,
            What = "the local Maven repository",
        },
        new ReclaimRule
        {
            Id = "dev.pip_cache",
            Patterns = [@"%LOCALAPPDATA%\pip\Cache"],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Redownload,
            What = "pip's wheel and HTTP cache",
            Command = "pip cache purge",
        },
        new ReclaimRule
        {
            Id = "dev.pycache",
            Patterns = [@"**\__pycache__", @"**\*.pyc"],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Rebuild,
            What = "compiled Python bytecode",
            Kind = MatchKind.Any,
        },
        new ReclaimRule
        {
            Id = "dev.venv",
            Patterns = [@"**\.venv", @"**\venv"],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Rebuild,
            What = "Python virtual environments",
            RequiresChild = "pyvenv.cfg",
            Command = "pip install -r requirements.txt",
        },
        new ReclaimRule
        {
            Id = "dev.cargo",
            Patterns = [@"~\.cargo\registry", @"**\target\debug", @"**\target\release"],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Rebuild,
            What = "Rust registry and build output",
            Command = "cargo clean",
        },
        new ReclaimRule
        {
            Id = "dev.go_modcache",
            Patterns = [@"~\go\pkg\mod"],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Redownload,
            What = "the Go module cache",
            Command = "go clean -modcache",
        },
        new ReclaimRule
        {
            Id = "dev.conda_pkgs",
            Patterns = [@"**\anaconda3\pkgs", @"**\miniconda3\pkgs"],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Redownload,
            What = "unpacked conda packages",
            Command = "conda clean --all",
        },
        new ReclaimRule
        {
            // The sibling and the child together are what distinguish a Unity project's
            // Library from any other directory called Library - of which a Windows disk
            // has plenty, and most of them are somebody's documents.
            Id = "dev.unity_library",
            Patterns = [@"**\Library"],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Rebuild,
            What = "a Unity project's import cache (reimport is slow)",
            RequiresChild = "ArtifactDB",
            RequiresSibling = "Assets",
        },
        new ReclaimRule
        {
            Id = "dev.unreal_ddc",
            Patterns = [@"**\DerivedDataCache", @"**\Intermediate", @"**\Saved\Autosaves"],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Rebuild,
            What = "Unreal derived data and intermediates",
        },
        new ReclaimRule
        {
            // Reported, never deleted. Removing objects destroys the repository; the whole
            // point of the rule is to find the repositories worth repacking.
            Id = "dev.git_gc",
            Patterns = [@"**\.git"],
            Risk = Risk.Caution,
            Recoverability = Recoverability.Irreversible,
            What = "a git repository whose object store has grown",
            Action = ReclaimAction.Command,
            Command = "git gc --prune=now",
            RequiresChildOver = "objects",
            ChildOverBytes = 500 * Mb,
        },
        new ReclaimRule
        {
            Id = "dev.docker",
            Patterns = [@"%LOCALAPPDATA%\Docker\wsl\**\*.vhdx", @"**\DockerDesktopWSL\**\*.vhdx"],
            Risk = Risk.Caution,
            Recoverability = Recoverability.Redownload,
            What = "Docker's virtual disk (images, volumes and layers together)",
            Action = ReclaimAction.Command,
            Command = "docker system prune -a --volumes",
            Kind = MatchKind.File,
        },
        new ReclaimRule
        {
            Id = "dev.vs_artifacts",
            Patterns = [@"**\.vs", @"**\CachedExtensionVSIXs"],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Instant,
            What = "Visual Studio's per-solution state",
        },
        new ReclaimRule
        {
            Id = "app.browser_cache",
            Patterns = [@"**\User Data\*\Cache*", @"**\GPUCache", @"**\cache2", @"**\Code Cache"],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Instant,
            What = "browser page and GPU caches",
        },
        new ReclaimRule
        {
            Id = "app.electron_cache",
            Patterns =
            [
                @"%APPDATA%\Slack\Cache", @"%APPDATA%\discord\Cache", @"%APPDATA%\Teams\Cache",
                @"%APPDATA%\Signal\Cache", @"%APPDATA%\Code\Cache", @"%APPDATA%\Telegram Desktop\Cache",
            ],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Instant,
            What = "Electron application caches",
        },
        new ReclaimRule
        {
            Id = "app.shader_cache",
            Patterns =
            [
                @"%LOCALAPPDATA%\NVIDIA\DXCache", @"%LOCALAPPDATA%\NVIDIA\GLCache",
                @"%LOCALAPPDATA%\AMD\DxCache", @"%LOCALAPPDATA%\D3DSCache", @"**\ShaderCache",
            ],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Instant,
            What = "compiled GPU shaders",
        },
        new ReclaimRule
        {
            Id = "app.steam_downloading",
            Patterns = [@"**\steamapps\downloading", @"**\steamapps\temp"],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Redownload,
            What = "partially downloaded Steam content",
        },
        new ReclaimRule
        {
            Id = "app.adobe_media_cache",
            Patterns = [@"%APPDATA%\Adobe\Common\Media Cache*"],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Rebuild,
            What = "Adobe's conformed audio and peak files",
        },
        new ReclaimRule
        {
            Id = "app.apple_backups",
            Patterns = [@"%APPDATA%\Apple Computer\MobileSync\Backup"],
            Risk = Risk.Caution,
            Recoverability = Recoverability.Irreversible,
            What = "iPhone and iPad backups - possibly the only copy",
            Action = ReclaimAction.Manual,
        },
        new ReclaimRule
        {
            // Contents only: Windows, installers and half the installed software assume
            // %TEMP% exists, and the guard refuses the directory itself anyway
            // (README section 9.3).
            Id = "sys.temp",
            Patterns = [@"%TEMP%", @"%WINDIR%\Temp", @"%LOCALAPPDATA%\Temp"],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Instant,
            What = "temporary files",
            ContentsOnly = true,
        },
        new ReclaimRule
        {
            Id = "sys.thumbnails",
            Patterns = [@"**\Explorer\thumbcache_*.db", @"**\Explorer\iconcache_*.db"],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Instant,
            What = "Explorer's thumbnail and icon caches",
            Kind = MatchKind.File,
        },
        new ReclaimRule
        {
            // A floor of 1 MB, which README section 7.2 does not state: without one this
            // rule matches tens of thousands of two-kilobyte files, and a report nobody
            // can read is the same as no report (README section 7.3).
            Id = "sys.old_logs",
            Patterns = [@"**\*.log", @"**\*.etl"],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Irreversible,
            What = "log files older than 30 days",
            Kind = MatchKind.File,
            OlderThan = TimeSpan.FromDays(30),
            MinSizeBytes = Mb,
            NotUnder = [@"%PROGRAMDATA%\**"],
        },
        new ReclaimRule
        {
            Id = "sys.dumps",
            Patterns = [@"**\CrashDumps", @"**\Minidump", @"**\MEMORY.DMP"],
            Risk = Risk.Safe,
            Recoverability = Recoverability.Irreversible,
            What = "crash dumps",
            Kind = MatchKind.Any,
        },
        new ReclaimRule
        {
            Id = "user.old_installers",
            Patterns =
            [
                @"%USERPROFILE%\Downloads\**\*.msi", @"%USERPROFILE%\Downloads\**\*.exe",
                @"%USERPROFILE%\Downloads\**\*.iso",
            ],
            Risk = Risk.Caution,
            Recoverability = Recoverability.Redownload,
            What = "installers downloaded over 90 days ago (unless licence-bound)",
            Kind = MatchKind.File,
            OlderThan = TimeSpan.FromDays(90),
            MinSizeBytes = Mb,
        },
        new ReclaimRule
        {
            Id = "user.large_media",
            Patterns =
            [
                @"**\*.iso", @"**\*.vhd", @"**\*.vhdx", @"**\*.img", @"**\*.bak", @"**\*.vmdk",
            ],
            Risk = Risk.Caution,
            Recoverability = Recoverability.Irreversible,
            What = "disk images and backups over 1 GB",
            Action = ReclaimAction.Manual,
            Kind = MatchKind.File,
            MinSizeBytes = Gb,
        },
    ];

    internal static ReclaimRule? ById(string id) =>
        All.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
