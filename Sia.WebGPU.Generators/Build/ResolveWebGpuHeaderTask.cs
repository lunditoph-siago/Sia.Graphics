using Microsoft.Build.Framework;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System;

public sealed class ResolveWebGpuHeaderTask : Microsoft.Build.Utilities.Task
{
    [Required]
    public string ReleaseTag { get; set; }

    [Output]
    public string HeaderUrl { get; set; }

    [Output]
    public string Revision { get; set; }

    public override bool Execute()
    {
        try {
            if (!Regex.IsMatch(ReleaseTag, @"^v\d{8}\.\d{6}$"))
                throw new InvalidDataException("Invalid WebGPU release tag.");
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) }) {
                var url = "https://raw.githubusercontent.com/google/dawn/" + ReleaseTag + "/DEPS";
                var deps = client.GetStringAsync(url).GetAwaiter().GetResult();
                Revision = Regex.Match(deps, @"webgpu-native/webgpu-headers@([a-f0-9]{40})").Groups[1].Value;
                if (Revision.Length == 0) throw new InvalidDataException("Cannot resolve the standard WebGPU header.");
                HeaderUrl = "https://raw.githubusercontent.com/webgpu-native/webgpu-headers/" + Revision + "/webgpu.h";
            }
            return true;
        }
        catch (Exception error) {
            Log.LogErrorFromException(error, true);
            return false;
        }
    }
}
