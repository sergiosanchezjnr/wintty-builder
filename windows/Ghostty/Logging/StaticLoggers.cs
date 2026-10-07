using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ghostty.Logging;

/// <summary>
/// Static accessor for <see cref="ILogger{T}"/> instances used by
/// <c>Ghostty</c>-project types whose call sites genuinely cannot
/// receive a logger through a constructor argument. Every remaining
/// entry here has a documented reason below; new components should
/// take <c>ILogger&lt;T&gt;</c> in their ctor via the <see cref="ILoggerFactory"/>
/// threaded through <see cref="App.OnLaunched"/>, not grow this class.
///
/// Populated once from <c>App.OnLaunched</c> via
/// <see cref="Initialize(ILoggerFactory)"/>. Before that call every
/// accessor returns <see cref="NullLogger{T}"/> (or
/// <see cref="NullLogger.Instance"/> for the string-category
/// <see cref="WindowStateMigration"/> entry), so early call sites
/// are safe no-ops.
///
/// Why each remaining site stays here:
///   - <see cref="App"/>: XAML constructs it, so there is no ctor the
///     factory can be handed to, and its earliest records (AUMID,
///     jump list) are written before the factory exists at all.
///     Every App-scoped record goes through here, including the ones
///     written long after startup.
///   - <see cref="ConfigService"/>: constructed before the factory
///     exists because the factory reads <c>log-level</c> /
///     <c>log-filter</c> off <c>ConfigService</c>. Chicken-and-egg,
///     so ctor injection is impossible.
///   - <see cref="WindowStateMigration"/>: <c>static class</c>, so
///     there is no ctor to inject into.
///   - <see cref="WindowState"/>: data class deserialized by
///     <c>System.Text.Json</c> via a static <c>Load()</c> method.
///     Ctor injection would require threading a logger through
///     every deserialization site.
///   - <see cref="GeneralPage"/>: XAML page constructed by the
///     WinUI 3 Frame via a parameterless ctor; ctor injection
///     requires a DI-enabled <c>Frame</c> which is a larger
///     refactor than this slot is worth.
///   - <see cref="KeybindingsPage"/>: same XAML-page constraint as
///     <see cref="GeneralPage"/>; logs keybind config write failures.
///   - <see cref="CheatSheet"/>: read-only keybind cheat-sheet
///     <c>ContentDialog</c>, constructed directly (no DI-enabled
///     <c>Frame</c>); logs show / export failures.
///   - <see cref="SettingsConfigWriter"/>: shared Core helper
///     constructed by the XAML Settings pages (same no-DI-Frame
///     constraint as <see cref="GeneralPage"/>); logs config-file
///     write failures from immediate-write handlers.
///   - <see cref="ShaderPreviewFeed"/>: Core helper owned by
///     <see cref="Ghostty.Settings.ShaderPickerWindow"/>, a
///     <c>Window</c> the settings pages construct directly (same
///     no-DI-Frame constraint as <see cref="GeneralPage"/>); logs
///     the autoplay feed dying.
///
/// Tests should use <see cref="Install"/> which returns an
/// <see cref="IDisposable"/> scope that restores the pre-install
/// state on disposal.
///
/// Note: <c>Ghostty.Settings.WindowStateMigration</c> is a
/// <c>static class</c> so it cannot appear as a type argument to
/// <see cref="ILogger{T}"/>. Its accessor uses the non-generic
/// <see cref="ILogger"/> bound to the same category name the
/// generic path would have produced, so filter rules keyed on
/// "Ghostty.Settings.WindowStateMigration" behave identically.
/// </summary>
internal static partial class StaticLoggers
{
    private static ILogger<Ghostty.Services.ConfigService>? _configService;
    // The config watcher's timer is built inside ConfigService, so it shares
    // ConfigService's reason for living here.
    private static ILogger<Ghostty.Core.Config.SystemSchedulerTimer>? _configWatcherTimer;

    // WindowStateMigration is a static class, so it uses the non-generic
    // ILogger with an explicit category name; see class docstring.
    private const string WindowStateMigrationCategory = "Ghostty.Settings.WindowStateMigration";
    private static ILogger? _windowStateMigration;
    private static ILogger<Ghostty.Settings.WindowState>? _windowState;
    private static ILogger<Ghostty.Settings.Pages.GeneralPage>? _generalPage;
    private static ILogger<Ghostty.Settings.Pages.KeybindingsPage>? _keybindingsPage;
    private static ILogger<Ghostty.Settings.CheatSheetDialog>? _cheatSheet;
    private static ILogger<Ghostty.Core.Config.SettingsConfigWriter>? _settingsConfigWriter;
    private static ILogger<Ghostty.Core.Settings.ShaderPreviewFeed>? _shaderPreviewFeed;
    private static ILogger<App>? _app;
    private static ILogger<Ghostty.Hosting.BellAudioPlayer>? _bellAudio;

    internal static ILogger<Ghostty.Services.ConfigService> ConfigService
        => _configService ?? NullLogger<Ghostty.Services.ConfigService>.Instance;
    internal static ILogger<Ghostty.Core.Config.SystemSchedulerTimer> ConfigWatcherTimer
        => _configWatcherTimer ?? NullLogger<Ghostty.Core.Config.SystemSchedulerTimer>.Instance;
    internal static ILogger WindowStateMigration
        => _windowStateMigration ?? NullLogger.Instance;
    internal static ILogger<Ghostty.Settings.WindowState> WindowState
        => _windowState ?? NullLogger<Ghostty.Settings.WindowState>.Instance;
    internal static ILogger<Ghostty.Settings.Pages.GeneralPage> GeneralPage
        => _generalPage ?? NullLogger<Ghostty.Settings.Pages.GeneralPage>.Instance;
    internal static ILogger<Ghostty.Settings.Pages.KeybindingsPage> KeybindingsPage
        => _keybindingsPage ?? NullLogger<Ghostty.Settings.Pages.KeybindingsPage>.Instance;
    internal static ILogger<Ghostty.Settings.CheatSheetDialog> CheatSheet
        => _cheatSheet ?? NullLogger<Ghostty.Settings.CheatSheetDialog>.Instance;
    internal static ILogger<Ghostty.Core.Config.SettingsConfigWriter> SettingsConfigWriter
        => _settingsConfigWriter ?? NullLogger<Ghostty.Core.Config.SettingsConfigWriter>.Instance;
    internal static ILogger<Ghostty.Core.Settings.ShaderPreviewFeed> ShaderPreviewFeed
        => _shaderPreviewFeed ?? NullLogger<Ghostty.Core.Settings.ShaderPreviewFeed>.Instance;
    internal static ILogger<App> App
        => _app ?? NullLogger<App>.Instance;
    internal static ILogger<Ghostty.Hosting.BellAudioPlayer> BellAudio
        => _bellAudio ?? NullLogger<Ghostty.Hosting.BellAudioPlayer>.Instance;

    internal static void Initialize(ILoggerFactory factory)
    {
        _configService = factory.CreateLogger<Ghostty.Services.ConfigService>();
        _configWatcherTimer = factory.CreateLogger<Ghostty.Core.Config.SystemSchedulerTimer>();
        _windowStateMigration = factory.CreateLogger(WindowStateMigrationCategory);
        _windowState = factory.CreateLogger<Ghostty.Settings.WindowState>();
        _generalPage = factory.CreateLogger<Ghostty.Settings.Pages.GeneralPage>();
        _keybindingsPage = factory.CreateLogger<Ghostty.Settings.Pages.KeybindingsPage>();
        _cheatSheet = factory.CreateLogger<Ghostty.Settings.CheatSheetDialog>();
        _settingsConfigWriter = factory.CreateLogger<Ghostty.Core.Config.SettingsConfigWriter>();
        _shaderPreviewFeed = factory.CreateLogger<Ghostty.Core.Settings.ShaderPreviewFeed>();
        _app = factory.CreateLogger<App>();
        _bellAudio = factory.CreateLogger<Ghostty.Hosting.BellAudioPlayer>();
    }

    internal static IDisposable Install(ILoggerFactory factory)
    {
        var prior = CaptureSnapshot();
        Initialize(factory);
        return new Scope(prior);
    }

    private static Snapshot CaptureSnapshot() => new(
        _configService, _configWatcherTimer, _windowStateMigration, _windowState, _generalPage, _keybindingsPage,
        _cheatSheet, _settingsConfigWriter, _shaderPreviewFeed, _app, _bellAudio);

    private readonly record struct Snapshot(
        ILogger<Ghostty.Services.ConfigService>? ConfigService,
        ILogger<Ghostty.Core.Config.SystemSchedulerTimer>? ConfigWatcherTimer,
        ILogger? WindowStateMigration,
        ILogger<Ghostty.Settings.WindowState>? WindowState,
        ILogger<Ghostty.Settings.Pages.GeneralPage>? GeneralPage,
        ILogger<Ghostty.Settings.Pages.KeybindingsPage>? KeybindingsPage,
        ILogger<Ghostty.Settings.CheatSheetDialog>? CheatSheet,
        ILogger<Ghostty.Core.Config.SettingsConfigWriter>? SettingsConfigWriter,
        ILogger<Ghostty.Core.Settings.ShaderPreviewFeed>? ShaderPreviewFeed,
        ILogger<App>? App,
        ILogger<Ghostty.Hosting.BellAudioPlayer>? BellAudio);

    private sealed partial class Scope : IDisposable
    {
        private readonly Snapshot _prior;
        public Scope(Snapshot prior) => _prior = prior;

        public void Dispose()
        {
            _configService = _prior.ConfigService;
            _configWatcherTimer = _prior.ConfigWatcherTimer;
            _windowStateMigration = _prior.WindowStateMigration;
            _windowState = _prior.WindowState;
            _generalPage = _prior.GeneralPage;
            _keybindingsPage = _prior.KeybindingsPage;
            _cheatSheet = _prior.CheatSheet;
            _settingsConfigWriter = _prior.SettingsConfigWriter;
            _shaderPreviewFeed = _prior.ShaderPreviewFeed;
            _app = _prior.App;
            _bellAudio = _prior.BellAudio;
        }
    }
}

/// <summary>
/// Portable self-update records (Ghostty.Services.PortableSelfUpdater),
/// routed through the App category because the updater is a static
/// background pipeline with no ctor to inject into and its failures are
/// launch-adjacent events that belong beside the other startup records.
/// </summary>
internal static partial class UpdaterLogExtensions
{
    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Updater.Failed,
                   Level = LogLevel.Warning,
                   Message = "Portable self-update failed")]
    internal static partial void LogUpdaterFailed(
        this ILogger<App> logger, System.Exception ex);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Updater.Timeout,
                   Level = LogLevel.Warning,
                   Message = "Portable self-update timed out")]
    internal static partial void LogUpdaterTimeout(
        this ILogger<App> logger);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Updater.NoAsset,
                   Level = LogLevel.Warning,
                   Message = "Latest release {Tag} has no portable zip asset")]
    internal static partial void LogUpdaterNoAsset(
        this ILogger<App> logger, string Tag);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Updater.UpToDate,
                   Level = LogLevel.Debug,
                   Message = "Already up to date: commit {Commit}, latest release {Tag} (prerelease: {Prerelease})")]
    internal static partial void LogUpdaterUpToDate(
        this ILogger<App> logger, string Commit, string Tag, bool Prerelease);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Updater.Found,
                   Level = LogLevel.Information,
                   Message = "Newer build available: {Tag} (running {Commit})")]
    internal static partial void LogUpdaterFound(
        this ILogger<App> logger, string Commit, string Tag);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Updater.ChecksumMismatch,
                   Level = LogLevel.Warning,
                   Message = "Update download rejected: sha256 mismatch (expected {Expected}, got {Actual})")]
    internal static partial void LogUpdaterChecksumMismatch(
        this ILogger<App> logger, string Expected, string Actual);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Updater.BadPayload,
                   Level = LogLevel.Warning,
                   Message = "Update payload for {Tag} contains no Wintty.exe; discarded")]
    internal static partial void LogUpdaterBadPayload(
        this ILogger<App> logger, string Tag);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Updater.Staged,
                   Level = LogLevel.Information,
                   Message = "Update {Tag} staged; applies on next launch")]
    internal static partial void LogUpdaterStaged(
        this ILogger<App> logger, string Tag);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Updater.SwapFailed,
                   Level = LogLevel.Warning,
                   Message = "Pending update swap failed; running current files, will retry next launch")]
    internal static partial void LogUpdaterSwapFailed(
        this ILogger<App> logger, System.Exception ex);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Updater.Swapped,
                   Level = LogLevel.Information,
                   Message = "Applied update {Tag}; relaunching")]
    internal static partial void LogUpdaterSwapped(
        this ILogger<App> logger, string Tag);
}

internal static partial class CheatSheetLogExtensions
{
    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.SettingsUi.CheatSheetShowFailed,
                   Level = LogLevel.Warning, Message = "Failed to show keybind cheat sheet")]
    internal static partial void LogCheatSheetShowFailed(
        this ILogger<Ghostty.Settings.CheatSheetDialog> logger, System.Exception ex);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.SettingsUi.CheatSheetExportFailed,
                   Level = LogLevel.Warning, Message = "Failed to export keybind cheat sheet")]
    internal static partial void LogCheatSheetExportFailed(
        this ILogger<Ghostty.Settings.CheatSheetDialog> logger, System.Exception ex);
}
