using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Yesify.Services;

/// <summary>How the running copy was made, which decides how it is updated.</summary>
public enum UpdateKind
{
    /// <summary>The single-file Yesify.exe from GitHub Releases: download the new exe and swap it in.</summary>
    Release,
    /// <summary>Built from a git clone: pull, rebuild, swap the build output.</summary>
    SourceGit,
    /// <summary>Built from source without git (a downloaded zip): fetch the latest source, rebuild, swap.</summary>
    SourceZip,
}

public sealed record UpdateInfo(
    UpdateKind Kind,
    string Title,               // "Yesify 2026.11.27"
    string Tagline,             // "New look, Cinema & a smarter Library"
    DateTimeOffset PublishedAt, // when the exe was uploaded / the newest commit
    long SizeBytes,             // the exe (releases only)
    string Notes,               // Markdown: the release notes, or one "- " line per commit
    string? DownloadUrl,
    string? Sha256,             // lower-case hex, from GitHub's asset digest
    int CommitCount,
    string? Branch,
    DateTime LocalTime);        // when this copy was downloaded / built

public sealed record UpdateProgress(string Stage, double? Fraction, string Detail, bool CanCancel);

/// <summary>
/// Yesify's updater. Each launch it asks GitHub whether there's something newer than the running copy:
/// <list type="bullet">
/// <item>The released exe: the latest release's <c>Yesify.exe</c> counts as newer when it was uploaded after
/// this exe was downloaded (its file time), unless it is in fact the very same file (same SHA-256).</item>
/// <item>Built from a git clone: the commit it was built from (stamped in at build time) is compared with
/// the default branch on GitHub.</item>
/// <item>Built from source without git: commits on the default branch newer than the newest source file.</item>
/// </list>
/// Installing swaps the new files in next to the running ones: Windows lets a running exe or dll be renamed
/// (not deleted), so the old files step aside as <c>*.old</c>, the new copy starts, waits for this one to
/// exit, and clears them away.
/// </summary>
public static class Updater
{
    public const string Repo = "YesD3v/Yesify-Desktop-App";
    public const string RepoUrl = "https://github.com/" + Repo;
    private const string Api = "https://api.github.com/repos/" + Repo;
    private const string ExeName = "Yesify.exe";
    private const string OldSuffix = ".yesify-old";

    private static readonly HttpClient Http = CreateHttp();

    /// <summary>
    /// `--simulate-update`: offers the latest release even when this copy is newer, and goes through the whole
    /// install (real download and checksum) except the swap: it restarts the same exe. For seeing the updater.
    /// </summary>
    public static bool Simulate { get; set; }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = Timeout.InfiniteTimeSpan, // each call brings its own cancellation
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Yesify-Updater");
        return http;
    }

    // ---------------------------------------------------------------- the running copy

    /// <summary>The git commit this copy was built from, when it was built in a clone.</summary>
    public static string? BuildCommit => Metadata("YesifyCommit");

    /// <summary>The project folder a source build came from (not stamped into released exes).</summary>
    private static string? ProjectDir => Metadata("YesifyProjectDir");

    /// <summary>A published single-file exe (the release) rather than a build output folder.</summary>
    public static bool IsSingleFile => string.IsNullOrEmpty(typeof(Updater).Assembly.Location);

    private static string? Metadata(string key) =>
        typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value?.Trim() is { Length: > 0 } v ? v : null;

    private static string? SourceDir() =>
        ProjectDir is { } dir && File.Exists(Path.Combine(dir, "Yesify.csproj")) ? dir : null;

    // ---------------------------------------------------------------- checking

    /// <summary>Asks GitHub for something newer than the running copy. Null when it's up to date (or can't tell).</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        if (IsSingleFile || Simulate) return await CheckReleaseAsync(ct);
        if (SourceDir() is not { } dir) return null; // a build copied away from its source: nothing to rebuild
        return BuildCommit is { } commit ? await CheckGitAsync(commit, ct) : await CheckZipAsync(dir, ct);
    }

    private static async Task<UpdateInfo?> CheckReleaseAsync(CancellationToken ct)
    {
        if (Environment.ProcessPath is not { } exe || !File.Exists(exe)) return null;
        using var doc = await GetJsonAsync($"{Api}/releases/latest", ct);
        if (doc == null) return null;
        var release = doc.RootElement;
        if (Bool(release, "draft") || Bool(release, "prerelease")) return null;

        var asset = release.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => string.Equals(Str(a, "name"), ExeName, StringComparison.OrdinalIgnoreCase));
        if (asset.ValueKind != JsonValueKind.Object || Str(asset, "browser_download_url") is not { } url) return null;

        // Uploaded before this exe was downloaded (or published here): nothing newer.
        var uploaded = asset.GetProperty("updated_at").GetDateTimeOffset();
        var local = File.GetLastWriteTimeUtc(exe);
        if (uploaded.UtcDateTime <= local && !Simulate) return null;

        // Newer on paper, but maybe the very same file (downloaded again, or the build that was uploaded).
        var size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
        var sha = Str(asset, "digest") is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? digest[7..].ToLowerInvariant() : null;
        if (!Simulate && (sha != null ? await Task.Run(() => HashFile(exe), ct) == sha : new FileInfo(exe).Length == size)) return null;

        var (title, tagline) = SplitName(Str(release, "name") ?? Str(release, "tag_name") ?? "Yesify");
        return new UpdateInfo(UpdateKind.Release, title, tagline, uploaded, size, CleanReleaseNotes(Str(release, "body") ?? ""),
            url, sha, 0, null, local);
    }

    private static async Task<UpdateInfo?> CheckGitAsync(string commit, CancellationToken ct)
    {
        var branch = await DefaultBranchAsync(ct);
        // A commit GitHub has never seen (local work, a fork) can't be compared: leave that copy alone.
        using var doc = await GetJsonAsync($"{Api}/compare/{commit}...{branch}", ct);
        if (doc == null) return null;
        var cmp = doc.RootElement;
        if (Str(cmp, "status") != "ahead" || !cmp.TryGetProperty("ahead_by", out var a) || a.GetInt32() <= 0) return null;

        var commits = cmp.GetProperty("commits").EnumerateArray().Reverse().ToList(); // newest first
        if (commits.Count == 0) return null;
        var newest = commits[0];
        var count = a.GetInt32();
        return new UpdateInfo(UpdateKind.SourceGit, $"Yesify @ {Str(newest, "sha")![..7]}",
            $"{count} new commit{(count == 1 ? "" : "s")} on {branch}", CommitDate(newest), 0, CommitNotes(commits),
            null, null, count, branch, LocalBuildTime());
    }

    private static async Task<UpdateInfo?> CheckZipAsync(string dir, CancellationToken ct)
    {
        // The newest of the build and its sources: edits made since are newer than anything on GitHub.
        var local = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => IsSourceFile(dir, f))
            .Select(File.GetLastWriteTimeUtc)
            .Append(LocalBuildTime())
            .Max();
        var branch = await DefaultBranchAsync(ct);
        var since = local.AddSeconds(1).ToString("yyyy-MM-ddTHH:mm:ssZ");
        using var doc = await GetJsonAsync($"{Api}/commits?sha={Uri.EscapeDataString(branch)}&since={since}&per_page=50", ct);
        if (doc == null) return null;
        var commits = doc.RootElement.EnumerateArray().ToList(); // newest first
        if (commits.Count == 0) return null;
        // Newer commits, but maybe with the very same files (edits made here, then committed and pushed).
        if (await SameSourceAsync(dir, Str(commits[0], "sha")!, ct)) return null;
        var count = commits.Count;
        return new UpdateInfo(UpdateKind.SourceZip, $"Yesify @ {Str(commits[0], "sha")![..7]}",
            $"{count}{(count == 50 ? "+" : "")} new commit{(count == 1 ? "" : "s")} on {branch}", CommitDate(commits[0]), 0,
            CommitNotes(commits), null, null, count, branch, local);
    }

    /// <summary>
    /// Whether every file of a commit on GitHub is already here, byte for byte (git's blob hash, with Windows
    /// line endings counted as the same file).
    /// </summary>
    private static async Task<bool> SameSourceAsync(string dir, string commit, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{Api}/git/trees/{commit}?recursive=1", ct);
        if (doc == null || Bool(doc.RootElement, "truncated")) return false;
        var blobs = doc.RootElement.GetProperty("tree").EnumerateArray()
            .Where(e => Str(e, "type") == "blob")
            .Select(e => (Path: Str(e, "path")!, Sha: Str(e, "sha")!))
            .ToList();
        return await Task.Run(() => blobs.All(b =>
        {
            var file = Path.Combine(dir, b.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file)) return false;
            var bytes = File.ReadAllBytes(file);
            if (BlobSha(bytes) == b.Sha) return true;
            var lf = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(bytes).Replace("\r\n", "\n"));
            return lf.Length != bytes.Length && BlobSha(lf) == b.Sha;
        }), ct);
    }

    private static string BlobSha(byte[] content)
    {
        var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha.AppendData(header);
        sha.AppendData(content);
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    private static DateTime LocalBuildTime() => File.GetLastWriteTimeUtc(typeof(Updater).Assembly.Location);

    private static async Task<string> DefaultBranchAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync(Api, ct);
        return doc != null && Str(doc.RootElement, "default_branch") is { } b ? b : "main";
    }

    // ---------------------------------------------------------------- installing

    /// <summary>
    /// Gets the update in place next to the running copy and starts it (it waits for this one to exit).
    /// Afterwards the caller only has to quit. Throws with a readable message when anything fails; the
    /// running copy is then left as it was.
    /// </summary>
    public static async Task InstallAsync(UpdateInfo update, IProgress<UpdateProgress> progress, CancellationToken ct)
    {
        string launch;
        if (update.Kind == UpdateKind.Release) launch = await InstallReleaseAsync(update, progress, ct);
        else launch = await InstallSourceAsync(update, progress, ct);

        progress.Report(new("Restarting Yesify", null, "Back in a moment", false));
        var start = new ProcessStartInfo(launch)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(launch)!,
        };
        start.ArgumentList.Add("--updated");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(start)?.Dispose();
    }

    private static async Task<string> InstallReleaseAsync(UpdateInfo update, IProgress<UpdateProgress> progress, CancellationToken ct)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Couldn't find Yesify.exe.");
        var dir = Path.GetDirectoryName(exe)!;
        var download = Simulate ? Path.Combine(Path.GetTempPath(), "yesify-simulated-update.download") : Path.Combine(dir, ExeName + ".download");
        try
        {
            var sha = await DownloadAsync(update.DownloadUrl!, download, update.SizeBytes, progress, ct);
            progress.Report(new("Verifying", null, "Checking the download against GitHub", false));
            if (update.Sha256 != null && sha != update.Sha256)
                throw new IOException("The download didn't match GitHub's checksum. Nothing was changed.");
            ct.ThrowIfCancellationRequested();

            progress.Report(new("Installing", null, "Swapping in the new Yesify.exe", false));
            if (Simulate) { await Task.Delay(1200, ct); return exe; } // the download is thrown away; same exe restarts
            var old = exe + "." + DateTime.UtcNow.Ticks + OldSuffix;
            File.Move(exe, old);
            try { File.Move(download, exe); }
            catch { File.Move(old, exe); throw; }
            return exe;
        }
        catch (UnauthorizedAccessException)
        {
            throw new IOException($"Yesify can't write to {dir}. Move Yesify.exe to a folder you own (like Documents) and try again.");
        }
        finally
        {
            TryDelete(download);
        }
    }

    private static async Task<string> InstallSourceAsync(UpdateInfo update, IProgress<UpdateProgress> progress, CancellationToken ct)
    {
        var dir = SourceDir() ?? throw new IOException("Couldn't find the source folder this copy was built from.");
        if (update.Kind == UpdateKind.SourceGit)
        {
            progress.Report(new("Getting the latest code", null, $"git pull ({update.Branch})", true));
            try { await RunAsync("git", ["-C", dir, "pull", "--ff-only"], ct); }
            catch (System.ComponentModel.Win32Exception) { throw new IOException("Git isn't installed, so this copy can't pull the latest code."); }
        }
        else
        {
            var zip = Path.Combine(Path.GetTempPath(), $"yesify-source-{DateTime.UtcNow.Ticks}.zip");
            try
            {
                await DownloadAsync($"{Api}/zipball/{Uri.EscapeDataString(update.Branch ?? "main")}", zip, 0, progress, ct);
                progress.Report(new("Updating the source", null, "Your old source is kept in .yesify-backup", false));
                await Task.Run(() => { BackUpSource(dir); ExtractSource(zip, dir); }, ct);
            }
            finally { TryDelete(zip); }
        }

        // Built next to the source, then copied over the running build: its files are in use.
        progress.Report(new("Building", null, "Compiling the new version, this can take a minute", true));
        var staging = Path.Combine(dir, "obj", "yesify-update");
        TryDeleteDir(staging);
        try
        {
            await RunAsync("dotnet", ["build", Path.Combine(dir, "Yesify.csproj"), "-c", "Release", "-o", staging, "-nologo", "-v", "q"], ct);
        }
        catch (System.ComponentModel.Win32Exception) { throw new IOException("The .NET SDK isn't installed, so the new code can't be built."); }

        progress.Report(new("Installing", null, "Swapping in the new build", false));
        var target = AppContext.BaseDirectory;
        await Task.Run(() => CopyOver(staging, target));
        TryDeleteDir(staging);
        return Path.Combine(target, ExeName);
    }

    /// <summary>Copies a build over the running one: files in use are renamed out of the way first.</summary>
    private static void CopyOver(string from, string to)
    {
        var stamp = DateTime.UtcNow.Ticks;
        foreach (var src in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, src));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            try { File.Copy(src, dest, overwrite: true); }
            catch (IOException)
            {
                File.Move(dest, dest + "." + stamp + OldSuffix);
                File.Copy(src, dest);
            }
        }
    }

    /// <summary>Zips the current source (not its build output) into .yesify-backup before it is overwritten.</summary>
    private static void BackUpSource(string dir)
    {
        var backups = Path.Combine(dir, ".yesify-backup");
        Directory.CreateDirectory(backups);
        using var zip = ZipFile.Open(Path.Combine(backups, $"source-{DateTime.Now:yyyy-MM-dd-HHmmss}.zip"), ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(f => IsSourceFile(dir, f)))
            zip.CreateEntryFromFile(file, Path.GetRelativePath(dir, file).Replace('\\', '/'));
    }

    /// <summary>GitHub's zipball puts everything in one top folder (owner-repo-sha); its contents go over the project.</summary>
    private static void ExtractSource(string zipPath, string dir)
    {
        var root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            var slash = entry.FullName.IndexOf('/');
            if (slash < 0 || entry.Name.Length == 0) continue; // the top folder, or a folder entry
            var dest = Path.GetFullPath(Path.Combine(dir, entry.FullName[(slash + 1)..]));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    private static bool IsSourceFile(string dir, string file)
    {
        var rel = Path.GetRelativePath(dir, file);
        var top = rel.Split(Path.DirectorySeparatorChar)[0];
        return top is not ("bin" or "obj" or "publish" or "publish-update" or ".git" or ".vs" or ".yesify-backup");
    }

    /// <summary>Clears what an earlier update left behind. Called at startup, after the old copy has exited.</summary>
    public static void CleanUpAfterUpdate()
    {
        try
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            TryDelete(Path.Combine(dir, ExeName + ".download"));
            foreach (var old in Directory.EnumerateFiles(dir, "*" + OldSuffix, IsSingleFile ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories))
                TryDelete(old);
        }
        catch { /* next time */ }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Downloads to a file with progress; returns its SHA-256 (lower-case hex).</summary>
    private static async Task<string> DownloadAsync(string url, string path, long expected, IProgress<UpdateProgress> progress, CancellationToken ct)
    {
        progress.Report(new("Downloading", expected > 0 ? 0 : null, "Starting", true));
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? expected;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long done = 0;
        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            var clock = Stopwatch.StartNew();
            var lastReport = -1000L;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                hash.AppendData(buffer, 0, read);
                done += read;
                if (clock.ElapsedMilliseconds - lastReport >= 120)
                {
                    lastReport = clock.ElapsedMilliseconds;
                    var speed = done / Math.Max(0.25, clock.Elapsed.TotalSeconds);
                    var detail = total > 0
                        ? $"{FormatMb(done)} of {FormatMb(total)}  ·  {FormatMb((long)speed)}/s"
                        : $"{FormatMb(done)}  ·  {FormatMb((long)speed)}/s";
                    progress.Report(new("Downloading", total > 0 ? (double)done / total : null, detail, true));
                }
            }
        }
        if (total > 0 && done != total) throw new IOException("The download was cut short. Check your connection and try again.");
        progress.Report(new("Downloading", 1, total > 0 ? $"{FormatMb(done)} of {FormatMb(total)}" : FormatMb(done), true));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string FormatMb(long bytes) => bytes >= 1024 * 1024 * 10
        ? $"{bytes / (1024.0 * 1024):0} MB"
        : $"{bytes / (1024.0 * 1024):0.0} MB";

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>GETs a GitHub API document; null for 404 (unknown commit, no release yet).</summary>
    private static async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    /// <summary>Runs a tool with no window; throws with the end of its output when it fails.</summary>
    private static async Task RunAsync(string file, string[] args, CancellationToken ct)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) start.ArgumentList.Add(a);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0"; // never wait on a password prompt nobody can see
        using var process = Process.Start(start)!;
        var output = new StringBuilder();
        void Collect(object _, DataReceivedEventArgs e) { if (e.Data != null) lock (output) output.AppendLine(e.Data); }
        process.OutputDataReceived += Collect;
        process.ErrorDataReceived += Collect;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        if (process.ExitCode == 0) return;

        string[] lines;
        lock (output) lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tail = lines.Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).TakeLast(3).ToList();
        if (tail.Count == 0) tail = lines.TakeLast(3).ToList();
        throw new IOException($"{Path.GetFileNameWithoutExtension(file)} failed: {string.Join(" ", tail)}".Trim());
    }

    private static (string Title, string Tagline) SplitName(string name)
    {
        foreach (var sep in new[] { " — ", " – ", " - ", ": " })
        {
            var i = name.IndexOf(sep, StringComparison.Ordinal);
            if (i > 0) return (name[..i].Trim(), name[(i + sep.Length)..].Trim());
        }
        return (name.Trim(), "");
    }

    /// <summary>Just the "what's new" part of a release's notes: no download instructions or checksums.</summary>
    private static string CleanReleaseNotes(string body)
    {
        var lines = body.Replace("\r", "").Split('\n').ToList();
        var start = lines.FindIndex(l => l.TrimStart('#', ' ').StartsWith("New in", StringComparison.OrdinalIgnoreCase));
        if (start >= 0) lines = lines.Skip(start + 1).ToList();
        return string.Join("\n", lines.Where(l => !l.TrimStart().StartsWith("SHA-256", StringComparison.OrdinalIgnoreCase))).Trim();
    }

    private static string CommitNotes(IEnumerable<JsonElement> newestFirst) => string.Join("\n",
        newestFirst.Take(40).Select(c =>
        {
            var message = c.GetProperty("commit").GetProperty("message").GetString() ?? "";
            return "- " + message.Split('\n')[0].Trim();
        }));

    private static DateTimeOffset CommitDate(JsonElement commit) =>
        commit.GetProperty("commit").GetProperty("committer").GetProperty("date").GetDateTimeOffset();

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    private static void TryDeleteDir(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
}
