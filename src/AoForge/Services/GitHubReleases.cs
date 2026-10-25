using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace AoForge.Services;

[DataContract]
public sealed class GhRelease
{
    [DataMember(Name = "tag_name")] public string TagName { get; set; }
    [DataMember(Name = "name")] public string Name { get; set; }
    [DataMember(Name = "body")] public string Body { get; set; }
    [DataMember(Name = "html_url")] public string HtmlUrl { get; set; }
    [DataMember(Name = "published_at")] public string PublishedAt { get; set; }
    [DataMember(Name = "draft")] public bool Draft { get; set; }
    [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
    [DataMember(Name = "assets")] public List<GhAsset> Assets { get; set; }
}

[DataContract]
public sealed class GhAsset
{
    [DataMember(Name = "name")] public string Name { get; set; }
    [DataMember(Name = "browser_download_url")] public string DownloadUrl { get; set; }
    [DataMember(Name = "size")] public long Size { get; set; }
    [DataMember(Name = "digest")] public string Digest { get; set; } // "sha256:<hex>" on newer releases
}

// Public GitHub releases, no login. Unauthenticated API calls are limited to 60 an hour, so
// answers are cached with their ETag: a "304 Not Modified" doesn't count.
public sealed class GitHubReleases
{
    static readonly HttpClient Http = CreateClient();

    readonly string _cacheDir = Path.Combine(AppPaths.DataDir, "cache");

    static HttpClient CreateClient()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; // TLS 1.3
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"AoForge/{AppPaths.Version}");
        return http;
    }

    // The latest release, or null if the repo has none yet.
    public Task<GhRelease> GetLatestAsync(string repo) =>
        GetCachedAsync<GhRelease>($"repos/{repo}/releases/latest", repo.Replace('/', '_'));

    // The repo's recent releases, newest first, for their patch notes. One API call, the same as
    // GetLatestAsync. Empty if the repo has none yet.
    public async Task<List<GhRelease>> GetReleasesAsync(string repo) =>
        await GetCachedAsync<List<GhRelease>>($"repos/{repo}/releases?per_page=30", repo.Replace('/', '_') + ".releases").ConfigureAwait(false)
        ?? new List<GhRelease>();

    // The release GitHub calls latest: the newest one that is neither a draft nor a prerelease.
    public static GhRelease Latest(IEnumerable<GhRelease> releases) =>
        releases.FirstOrDefault(r => !r.Draft && !r.Prerelease);

    // GET api.github.com/<path>, cached as <cacheName>.json with its ETag. Null on 404.
    async Task<T> GetCachedAsync<T>(string path, string cacheName) where T : class
    {
        Directory.CreateDirectory(_cacheDir);
        string key = Path.Combine(_cacheDir, cacheName);
        string cacheFile = key + ".json", etagFile = key + ".etag";

        var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/" + path);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (File.Exists(cacheFile) && File.Exists(etagFile))
            req.Headers.TryAddWithoutValidation("If-None-Match", File.ReadAllText(etagFile));

        using var resp = await Http.SendAsync(req).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotModified)
            return Json.ReadFile<T>(cacheFile);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (resp.StatusCode == HttpStatusCode.Forbidden || (int)resp.StatusCode == 429) {
            if (File.Exists(cacheFile))
                return Json.ReadFile<T>(cacheFile);
            throw new HttpRequestException("GitHub's limit for checks without a login was reached. Try again in an hour.");
        }
        resp.EnsureSuccessStatusCode();

        byte[] body = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        var result = Json.Parse<T>(body);
        File.WriteAllBytes(cacheFile, body);
        if (resp.Headers.ETag != null)
            File.WriteAllText(etagFile, resp.Headers.ETag.ToString());
        return result;
    }

    public async Task<string> GetStringAsync(string url)
    {
        using var resp = await Http.GetAsync(url).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    // Downloads to %TEMP%\AoForge and checks the size and, when GitHub provides it, the SHA-256.
    public async Task<string> DownloadAsync(GhAsset asset, IProgress<double> progress)
    {
        string dir = Path.Combine(Path.GetTempPath(), "AoForge");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Path.GetFileName(asset.Name));
        string part = path + ".part";

        using (var resp = await Http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false)) {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? asset.Size;
            using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var dst = File.Create(part);
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0) {
                await dst.WriteAsync(buf, 0, n).ConfigureAwait(false);
                done += n;
                if (total > 0)
                    progress?.Report((double)done / total);
            }
        }

        if (asset.Size > 0 && new FileInfo(part).Length != asset.Size)
            throw new IOException($"The download of {asset.Name} is incomplete. Try again.");
        if (asset.Digest != null && asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) {
            string expected = asset.Digest.Substring(7);
            if (!string.Equals(InstallEngine.Sha256(part), expected, StringComparison.OrdinalIgnoreCase)) {
                File.Delete(part);
                throw new CryptographicException($"{asset.Name} doesn't match the checksum GitHub published for it. Nothing was installed.");
            }
        }
        if (File.Exists(path))
            File.Delete(path);
        File.Move(part, path);
        return path;
    }
}
