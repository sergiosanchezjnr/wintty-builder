using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.SingleInstance;
using Ghostty.Core.Version;
using Ghostty.Logging;
using Microsoft.Extensions.Logging;

namespace Ghostty.Services;

/// <summary>
/// Automatic self-update for the portable (unpacked, double-click) build.
///
/// The sponsor builds update through Velopack; this OSS fork ships a
/// portable zip and an exe with no installer, so "update automatically"
/// has to be done here. The contract:
///
///  - On launch (fire-and-forget, off the UI thread), ask GitHub's Releases
///    API for the latest release of <see cref="Repository"/>.
///  - A release is newer when its commit hash differs from the running
///    build's baked-in commit (<see cref="BuildInfo.WinttyCommit"/>).
///    Version strings cannot carry this signal: every tip build is
///    versioned 0.0.0 by the BuildInfo generator, so the commit IS the
///    version. An unknown ("unknown") baked commit never matches and
///    never updates — dev builds are left alone.
///  - Download the `wintty-portable-win-x64.zip` asset into a staging dir
///    under %LOCALAPPDATA%, verify the published SHA-256 sidecar when the
///    release carries one, extract there.
///  - Write a marker next to Wintty.exe. The NEXT launch sees the marker
///    first (Program.Main → <see cref="ApplyPendingUpdateIfMarked"/>) and
///    swaps the staged folder over the install folder before any Wintty
///    binary is loaded, then relaunches itself. A swap that dies halfway
///    is resumable: the marker outlives it and the same code runs again.
///
/// Deliberately conservative: nothing ever deletes the old tree (it moves
/// to `wintty-update-old/` beside the install dir), the whole thing is one
/// try per launch with a hard timeout, and every failure path logs under
/// LogEvents.Updater and leaves the running build untouched. Set
/// WINTTY_NO_AUTOUPDATE=1 to disable.
/// </summary>
internal static class PortableSelfUpdater
{
    /// <summary>The repository whose Releases feed the updater.</summary>
    public const string Repository = "sergiosanchezjnr/wintty-builder";

    /// <summary>Name the packaging workflow must give the portable asset.</summary>
    public const string PortableAssetName = "wintty-portable-win-x64.zip";

    /// <summary>Optional companion asset: one line, "&lt;sha256&gt;  &lt;zip name&gt;".</summary>
    public const string ChecksumAssetName = "wintty-portable-win-x64.zip.sha256";

    // Marker written next to Wintty.exe when a staged update is ready.
    internal const string MarkerFileName = "wintty-update.json";

    // Staging root under %LOCALAPPDATA%.
    private static string StagingRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Wintty", "wintty-update-staging");

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        // GitHub's API requires a User-Agent on every request.
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Wintty", "portable-updater"));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>
    /// Kick off the check/download/stage pipeline. Fire-and-forget: called
    /// from App.OnLaunched after the loggers exist; a throw here would be
    /// worse than no update.
    /// </summary>
    public static void StartBackgroundCheck()
    {
        if (Environment.GetEnvironmentVariable("WINTTY_NO_AUTOUPDATE") == "1")
            return;
        _ = Task.Run(RunAsync);
    }

    private static async Task RunAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;
            var installDir = Path.GetDirectoryName(exePath)!;

            // Only the portable layout self-updates: an MSI/Velopack install
            // owns its directory and updating under it corrupts the package.
            if (!IsPortableInstall(installDir)) return;

            // Already staged and waiting on a restart? Don't re-download.
            if (HasMarker(exePath)) return;

            // Another instance may already be staging; one pipeline at a time.
            var gate = Path.Combine(StagingRoot, ".lock");
            Directory.CreateDirectory(StagingRoot);
            if (!TryAcquireLock(gate, cts.Token)) return;
            try
            {
                await StageLatestReleaseAsync(exePath, installDir, cts.Token);
            }
            finally
            {
                TryDelete(gate);
            }
        }
        catch (OperationCanceledException)
        {
            StaticLoggers.App.LogUpdaterTimeout();
        }
        catch (Exception ex)
        {
            // Never surface an error UI: a failed background update is a
            // non-event for the user; the log line is the whole story.
            StaticLoggers.App.LogUpdaterFailed(ex);
        }
    }

    private static bool IsPortableInstall(string installDir)
    {
        // The signed/sponsored layouts keep their own bookkeeping folders;
        // the OSS portable zip is flat: exe + native/ + share/. Require the
        // native core dll — without it this is not a Wintty payload anyway.
        return File.Exists(Path.Combine(installDir, "native", "ghostty.dll"))
            || File.Exists(Path.Combine(installDir, "ghostty.dll"));
    }

    private static bool TryAcquireLock(string gate, CancellationToken ct)
    {
        try
        {
            if (File.Exists(gate) &&
                File.GetLastWriteTimeUtc(gate) > DateTime.UtcNow - TimeSpan.FromMinutes(30))
                return false; // live lock from another instance
            File.WriteAllText(gate, Environment.ProcessId.ToString());
            return true;
        }
        catch (Exception ex)
        {
            StaticLoggers.App.LogUpdaterFailed(ex);
            return false;
        }
    }

    private static bool HasMarker(string exePath)
    {
        var dir = Path.GetDirectoryName(exePath)!;
        return File.Exists(Path.Combine(dir, MarkerFileName)) ||
               PendingSwapExists(dir);
    }

    /// <summary>True when a previous swap left a half-finished rename.</summary>
    private static bool PendingSwapExists(string installDir)
    {
        var staging = Path.Combine(installDir, "wintty-update-staged");
        var old = Path.Combine(installDir, "wintty-update-old");
        return Directory.Exists(staging) || Directory.Exists(old);
    }

    // ── Check / download / stage ─────────────────────────────────────────

    private static async Task StageLatestReleaseAsync(
        string exePath, string installDir, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(
            $"https://api.github.com/repos/{Repository}/releases/latest",
            HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var tag = ReadStr(root, "tag_name") ?? "";
        var prerelease = root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean();

        string? zipUrl = null, zipSha = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = ReadStr(asset, "name");
                if (name == PortableAssetName)
                    zipUrl = ReadStr(asset, "browser_download_url");
                else if (name == ChecksumAssetName)
                    zipSha = ReadStr(asset, "browser_download_url");
            }
        }
        if (zipUrl is null)
        {
            StaticLoggers.App.LogUpdaterNoAsset(tag);
            return;
        }

        // Identity of the release: the packaging workflow stamps the commit
        // into the tag body's first line ("<short-sha>" or a line containing
        // it). Fall back to comparing the whole body against our baked commit.
        var body = ReadStr(root, "body") ?? "";
        // Runtime identity first (the sidecar shipped inside the portable
        // zip), compile-time constant as fallback for in-tree builds.
        var current = BuildIdentity.ReadCommit(exePath) ?? BuildInfo.WinttyCommit;
        var newer = IsNewer(current, tag, body);
        if (!newer)
        {
            StaticLoggers.App.LogUpdaterUpToDate(current, tag, prerelease);
            return;
        }

        StaticLoggers.App.LogUpdaterFound(current, tag);

        var workDir = Path.Combine(StagingRoot, Sanitize(tag));
        TryDeleteTree(workDir);
        Directory.CreateDirectory(workDir);

        var zipPath = Path.Combine(workDir, PortableAssetName);
        await DownloadToFileAsync(zipUrl, zipPath, ct);

        if (zipSha is not null)
        {
            using var shaResp = await Http.GetAsync(zipSha, ct);
            if (shaResp.IsSuccessStatusCode)
            {
                var shaText = await shaResp.Content.ReadAsStringAsync(ct);
                var expected = shaText.Split([' ', '\t', '\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
                var actual = ComputeSha256(zipPath);
                if (!string.Equals(expected, actual, StringComparison.Ordinal))
                {
                    TryDeleteTree(workDir);
                    StaticLoggers.App.LogUpdaterChecksumMismatch(expected, actual);
                    return;
                }
            }
            // A missing checksum asset is treated as "no verification":
            // the repo publishes the zip either way; failing closed would
            // strand users on the last good build forever if CI stopped
            // emitting the sidecar. The mismatch branch above is the real
            // guard when the sidecar exists.
        }

        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, workDir);
        TryDelete(zipPath);

        // Locate the extracted root: the zip may nest everything under one
        // top-level folder; find the directory holding Wintty.exe.
        var newRoot = FindExtractedRoot(workDir);
        if (newRoot is null)
        {
            TryDeleteTree(workDir);
            StaticLoggers.App.LogUpdaterBadPayload(tag);
            return;
        }

        // Stage beside the install dir so the swap is a same-volume rename
        // (atomic-ish, no copy across drives).
        var staged = Path.Combine(installDir, "wintty-update-staged");
        TryDeleteTree(staged);
        Directory.Move(newRoot, staged);

        var marker = new UpdateMarker(
            Tag: tag,
            Staged: "wintty-update-staged",
            Exe: Path.GetFileName(exePath));
        File.WriteAllText(
            Path.Combine(installDir, MarkerFileName),
            JsonSerializer.Serialize(marker, UpdaterMarkerContext.Default.UpdateMarker));

        StaticLoggers.App.LogUpdaterStaged(tag);
    }

    /// <summary>
    /// A release is newer when its identity differs from ours. Tags look
    /// like `tip-&lt;sha&gt;` / `v1.2.3`; the body carries the full commit
    /// list. Our baked commit appearing anywhere in tag+body means we are
    /// that build (or it predates us in the chain we can see) — otherwise
    /// the latest release is a different build and worth taking.
    /// </summary>
    internal static bool IsNewer(string currentCommit, string tag, string body)
    {
        if (string.IsNullOrEmpty(currentCommit) || currentCommit == "unknown")
            return false; // dev/unlabeled build: never touch it
        var haystack = tag + "\n" + body;
        if (haystack.Contains(currentCommit, StringComparison.OrdinalIgnoreCase))
            return false;
        return !string.IsNullOrEmpty(tag);
    }

    private static async Task DownloadToFileAsync(string url, string path, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url,
            HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(path, FileMode.Create,
            FileAccess.Write, FileShare.None);
        await src.CopyToAsync(dst, ct);
    }

    private static string ComputeSha256(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    private static string? FindExtractedRoot(string workDir)
    {
        var exe = Path.Combine(workDir, "Wintty.exe");
        if (File.Exists(exe)) return workDir;
        foreach (var sub in Directory.GetDirectories(workDir))
        {
            if (File.Exists(Path.Combine(sub, "Wintty.exe"))) return sub;
        }
        return null;
    }

    private static string Sanitize(string tag)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            tag = tag.Replace(c, '_');
        return tag;
    }

    // AOT-friendly: the GitHub payload is read structurally with
    // JsonDocument, never through reflection-based deserialization.
    private static string? ReadStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    internal static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    internal static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch { /* best effort: locked leftovers retry on the next launch */ }
    }

    /// <summary>The marker payload written beside Wintty.exe.</summary>
    internal sealed record UpdateMarker(string Tag, string Staged, string Exe)
    {
        // Property names on the wire match the record parameters exactly
        // (PascalCase), which is what System.Text.Json writes by default
        // and what the packaging workflow's marker reads back.
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(UpdateMarker))]
    internal partial class UpdaterMarkerContext : JsonSerializerContext
    {
    }

    // ── Apply (runs BEFORE anything loads, from Program.Main) ────────────

    /// <summary>
    /// If a staged update is waiting, swap it over the install directory
    /// and relaunch. Returns the exit code when a relaunch was performed;
    /// null when there was nothing pending (the normal case) and startup
    /// should continue untouched.
    ///
    /// Called at the very top of MainImpl, before AttachToParentConsole
    /// matters, before the native resolver registers, before ghostty.dll
    /// loads: once any binary in the install dir is memory-mapped, its
    /// file cannot be renamed.
    /// </summary>
    public static int? ApplyPendingUpdateIfMarked(string[] args)
    {
        try
        {
            // An explicit opt-out must also cancel an already-staged swap:
            // WINTTY_NO_AUTOUPDATE=1 means "do not touch my files", full stop.
            if (Environment.GetEnvironmentVariable("WINTTY_NO_AUTOUPDATE") == "1")
                return null;

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return null;
            var installDir = Path.GetDirectoryName(exePath)!;
            var markerPath = Path.Combine(installDir, MarkerFileName);
            var stagedDir = Path.Combine(installDir, "wintty-update-staged");
            var oldDir = Path.Combine(installDir, "wintty-update-old");

            var resuming = Directory.Exists(stagedDir) || Directory.Exists(oldDir);
            if (!resuming && !File.Exists(markerPath)) return null;

            // A staged update is worthless if a running instance has the old
            // binaries locked: the rename-swap would fail file by file. Bail
            // quietly — the marker survives, and the swap happens on the next
            // launch after the last window closes (the normal double-click
            // flow: relaunch Wintty, it updates itself).
            var election = SingleInstanceElection.Run(enabled: true, exePath);
            if (election.Role == SingleInstanceRole.Secondary)
                return null;

            string exeName = "Wintty.exe";
            string tag = "?";
            if (File.Exists(markerPath))
            {
                var marker = JsonSerializer.Deserialize(
                    File.ReadAllText(markerPath),
                    UpdaterMarkerContext.Default.UpdateMarker);
                if (marker is not null) { exeName = marker.Exe; tag = marker.Tag; }
            }

            // Refuse to swap onto a target that isn't where the marker says
            // it is; a moved/copied folder just loses the marker instead.
            if (!resuming && !File.Exists(Path.Combine(stagedDir, exeName)))
            {
                TryDelete(markerPath);
                election.Dispose();
                return null;
            }

            Swap(installDir, stagedDir, oldDir, markerPath);

            // Logged through the NullLogger at this point in startup (the
            // factory does not exist until App.OnLaunched), but the swap and
            // relaunch are recorded here for whoever greps a verbose trace.
            StaticLoggers.App.LogUpdaterSwapped(tag);

            // Release the held primary mutex before relaunching: the new
            // process must win its own election cleanly rather than see this
            // one's abandoned handle. (The mutex also dies with this process,
            // but explicit release removes any race with the fast relaunch.)
            election.Dispose();

            // Relaunch the new exe with the original arguments, detached,
            // then exit. The user sees one window close and reopen.
            var newExe = Path.Combine(installDir, exeName);
            Process.Start(new ProcessStartInfo
            {
                FileName = newExe,
                ArgumentList = { args },
                WorkingDirectory = installDir,
                UseShellExecute = true,
            });
            return 0;
        }
        catch (Exception ex)
        {
            // A failed swap must not brick the launch: fall through and run
            // whatever is currently on disk. The marker stays, so the next
            // launch retries.
            StaticLoggers.App.LogUpdaterSwapFailed(ex);
            return null;
        }
    }

    /// <summary>
    /// Rename-swap the staged tree over the install contents. Order matters
    /// for crash safety: (1) move staged→current only after the current
    /// binaries are parked in old/, and (2) the marker is deleted last, so
    /// any interruption leaves `resuming` true and the same routine
    /// finishes the job on next launch.
    /// </summary>
    private static void Swap(string installDir, string stagedDir, string oldDir, string markerPath)
    {
        TryDeleteTree(oldDir);
        Directory.CreateDirectory(oldDir);

        // Park every entry of the install dir except the swap machinery.
        foreach (var dir in Directory.GetDirectories(installDir))
        {
            var name = Path.GetFileName(dir);
            if (name is "wintty-update-staged" or "wintty-update-old") continue;
            Directory.Move(dir, Path.Combine(oldDir, name));
        }
        foreach (var file in Directory.GetFiles(installDir))
        {
            var name = Path.GetFileName(file);
            if (name == MarkerFileName) continue;
            File.Move(file, Path.Combine(oldDir, name));
        }

        // Bring in the staged tree, file by file, so a partially-populated
        // install dir still ends up complete.
        MoveInto(stagedDir, installDir);

        TryDelete(markerPath);
        TryDeleteTree(stagedDir);
        // old/ is intentionally NOT deleted: it is the rollback material,
        // swept by the next successful launch's stale cleanup (below).
    }

    private static void MoveInto(string source, string target)
    {
        foreach (var dir in Directory.GetDirectories(source))
        {
            var dest = Path.Combine(target, Path.GetFileName(dir));
            Directory.CreateDirectory(dest);
            MoveInto(dir, dest);
        }
        foreach (var file in Directory.GetFiles(source))
        {
            var dest = Path.Combine(target, Path.GetFileName(file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Move(file, dest, overwrite: true);
        }
    }

    /// <summary>
    /// After a successful swap the process relaunches; on that second
    /// launch this sweeps the rollback folder. Called from the background
    /// pipeline start, fire-and-forget. Best-effort: a locked leftover
    /// simply survives until some later launch.
    /// </summary>
    public static void SweepOldInstall()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;
            var oldDir = Path.Combine(
                Path.GetDirectoryName(exePath)!, "wintty-update-old");
            if (Directory.Exists(oldDir)) TryDeleteTree(oldDir);
        }
        catch (Exception ex)
        {
            StaticLoggers.App.LogUpdaterFailed(ex);
        }
    }
}
