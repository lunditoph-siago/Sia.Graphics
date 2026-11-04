using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization.Json;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System;

public sealed class ResolveNativeReleaseTask : Microsoft.Build.Utilities.Task
{
    [Required]
    public string ReleaseTag { get; set; }

    [Required]
    public string MetadataFile { get; set; }

    [Output]
    public string Revision { get; set; }

    [Output]
    public ITaskItem HeaderAsset { get; set; }

    [Output]
    public ITaskItem[] SdkAssets { get; set; }

    public override bool Execute()
    {
        try {
            MetadataFile = Path.GetFullPath(MetadataFile);
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) }) {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Sia.WebGPU");
                var release = FindRelease(client);
                var header = release.Assets.Single(asset =>
                    asset != null && Regex.IsMatch(asset.Name ?? "", @"^dawn-headers-[a-f0-9]{40}\.tar\.gz$"));
                Revision = Regex.Match(header.Name, "[a-f0-9]{40}").Value;
                HeaderAsset = ToItem(header);
                var prefix = "Dawn-" + Revision + "-";
                SdkAssets = release.Assets.Where(asset => asset?.Name != null &&
                    asset.Name.StartsWith(prefix, StringComparison.Ordinal) &&
                    asset.Name.EndsWith("-Release.tar.gz", StringComparison.Ordinal)).Select(ToItem).ToArray();
            }
            return true;
        }
        catch (Exception error) {
            Log.LogErrorFromException(error, true);
            return false;
        }
    }

    private Release FindRelease(HttpClient client)
    {
        if (string.IsNullOrEmpty(ReleaseTag))
            throw new InvalidOperationException("A WebGPU release tag is required.");
        if (!Regex.IsMatch(ReleaseTag, @"^v\d{8}\.\d{6}$"))
            throw new InvalidDataException("Invalid native release tag.");

        var path = MetadataFile;
        try {
            var cached = ReadRelease(path);
            if (MatchesRelease(cached) && cached.Tag == ReleaseTag) return cached;
        }
        catch (SerializationException) {
            Log.LogMessage(MessageImportance.Normal, "Ignoring invalid release cache: {0}", path);
        }
        catch (IOException) {
            Log.LogMessage(MessageImportance.Normal, "Cannot read release cache: {0}", path);
        }

        var url = "https://api.github.com/repos/google/dawn/releases/tags/" + Uri.EscapeDataString(ReleaseTag);
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrEmpty(token))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using (var stream = client.GetStreamAsync(url).GetAwaiter().GetResult()) {
            var serializer = new DataContractJsonSerializer(typeof(Release));
            var release = (Release)serializer.ReadObject(stream);
            if (!MatchesRelease(release) || release.Tag != ReleaseTag)
                throw new InvalidDataException("Invalid native release metadata.");

            CacheRelease(release);
            return release;
        }
    }

    private void CacheRelease(Release release)
    {
        var path = MetadataFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var file = File.Create(temporary)) {
                var serializer = new DataContractJsonSerializer(typeof(Release));
                serializer.WriteObject(file, release);
            }

            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static Release ReadRelease(string path)
    {
        using (var stream = File.OpenRead(path)) {
            var serializer = new DataContractJsonSerializer(typeof(Release));
            return (Release)serializer.ReadObject(stream);
        }
    }

    private static bool MatchesRelease(Release release) =>
        release != null && Regex.IsMatch(release.Tag ?? "", @"^v\d{8}\.\d{6}$") && release.Assets != null &&
        release.Assets.Count(asset =>
            asset != null && Regex.IsMatch(asset.Name ?? "", @"^dawn-headers-[a-f0-9]{40}\.tar\.gz$")) == 1;

    private static ITaskItem ToItem(AssetInfo asset)
    {
        if (Path.GetFileName(asset.Name) != asset.Name || !Regex.IsMatch(asset.Digest ?? "", "^sha256:[a-f0-9]{64}$"))
            throw new InvalidDataException("Invalid release asset metadata.");
        var item = new TaskItem(asset.Name);
        item.SetMetadata("Url", asset.Url);
        item.SetMetadata("Digest", asset.Digest);
        return item;
    }

    [DataContract]
    private sealed class Release
    {
        [DataMember(Name = "tag_name")]
        public string Tag { get; set; }

        [DataMember(Name = "assets")]
        public AssetInfo[] Assets { get; set; }
    }

    [DataContract]
    private sealed class AssetInfo
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "digest")]
        public string Digest { get; set; }

        [DataMember(Name = "browser_download_url")]
        public string Url { get; set; }
    }
}
