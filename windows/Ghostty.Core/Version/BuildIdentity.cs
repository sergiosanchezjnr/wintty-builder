using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ghostty.Core.Version;

/// <summary>
/// The build identity of the running binary, read at runtime.
///
/// <see cref="BuildInfo"/> bakes the commit into a compile-time constant
/// (<c>WinttyCommit</c>, generated from <c>git rev-parse --short HEAD</c>).
/// That is what <c>wintty +version</c> prints, but it cannot answer the
/// updater's question — "is this folder newer than me?" — because two
/// portable builds of different commits carry identical version strings
/// until one of them is recompiled. So the release packaging writes this
/// sidecar next to Wintty.exe:
///
///   wintty-build.json   {"commit":"abc1234","version":"0.0.0-tip+abc1234"}
///
/// Every reader here is fail-soft: no file, unparseable JSON, or an
/// unknown shape all read as "unknown" (null), which callers must treat
/// as "cannot compare", never as "up to date". A dev build (plain
/// <c>dotnet build</c>) has no sidecar and simply never participates in
/// update comparisons.
/// </summary>
public static class BuildIdentity
{
    /// <summary>Sidecar file name, written by the release packaging.</summary>
    public const string FileName = "wintty-build.json";

    /// <summary>
    /// The short commit recorded for the install directory containing
    /// <paramref name="exePath"/>, or null when no sidecar exists there
    /// or it cannot be read. Never throws.
    /// </summary>
    public static string? ReadCommit(string exePath)
        => Read(exePath).Commit;

    /// <summary>
    /// Reads the sidecar beside <paramref name="exePath"/>. Returns the
    /// all-null <see cref="Entry"/> when absent or unreadable.
    /// </summary>
    public static Entry Read(string exePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(dir)) return default;
            var file = Path.Combine(dir, FileName);
            if (!File.Exists(file)) return default;
            // FileShare.ReadWrite: the self-update staging step may still be
            // flushing the sidecar while a second instance reads it.
            using var stream = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            // Source-generated contract: this assembly ships NativeAOT, so
            // no reflection-based deserialization (same shape as Core's
            // FrecencyStore / DiscoveryCache).
            var entry = JsonSerializer.Deserialize(stream,
                BuildIdentityJsonContext.Default.Entry);
            return entry ?? default;
        }
        catch
        {
            // An identity probe must never take down a launch.
            return default;
        }
    }

    /// <summary>
    /// The sidecar payload. Property names match the JSON keys written by
    /// the packaging workflow (camelCase via the context below).
    /// </summary>
    public sealed record Entry
    {
        public string? Commit { get; init; }
        public string? Version { get; init; }
    }

    [JsonSourceGenerationOptions(PropertyNaming = JsonPropertyNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(Entry))]
    internal sealed partial class BuildIdentityJsonContext
        : JsonSerializerContext
    {
    }
}
