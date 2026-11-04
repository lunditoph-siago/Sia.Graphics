using Microsoft.Build.Framework;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System;

public sealed class ResolveDxcRuntimeTask : Microsoft.Build.Utilities.Task
{
    [Required]
    public string RuntimeIdentifier { get; set; }

    [Output]
    public string RuntimeDirectory { get; set; }

    [Output]
    public string Version { get; set; }

    [Output]
    public string LicenseUrl { get; set; }

    [Output]
    public string LicenseName { get; set; }

    public override bool Execute()
    {
        try {
            if (!RuntimeIdentifier.StartsWith("win-", StringComparison.Ordinal))
                throw new InvalidOperationException("DXC redistribution requires a Windows target.");
            var arch = RuntimeIdentifier.Substring(4);
            RuntimeDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Windows Kits", "10", "Redist", "D3D", arch);
            Version = FileVersionInfo.GetVersionInfo(Path.Combine(RuntimeDirectory, "dxcompiler.dll")).ProductVersion;
            var revision = Regex.Match(Version, @"\(([a-f0-9]{7,40})\)");
            if (!revision.Success) throw new InvalidDataException("Cannot identify DXC revision.");
            LicenseName = "LICENSE.dxc-" + revision.Groups[1].Value;
            LicenseUrl = "https://raw.githubusercontent.com/microsoft/DirectXShaderCompiler/" +
                revision.Groups[1].Value + "/LICENSE.TXT";
            return true;
        }
        catch (Exception error) {
            Log.LogErrorFromException(error, true);
            return false;
        }
    }
}
