using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;

namespace DepScope.Desktop.Services;

    public sealed class UpdateInfo
    {
        public Version LatestVersion { get; }
        public string? AssetDownloadUrl { get; }
        public string? ChecksumDownloadUrl { get; }
        public string ReleasePageUrl { get; }

        public UpdateInfo(
            Version latestVersion,
            string? assetDownloadUrl,
            string? checksumDownloadUrl,
            string releasePageUrl)
        {
            LatestVersion = latestVersion;
            AssetDownloadUrl = assetDownloadUrl;
            ChecksumDownloadUrl = checksumDownloadUrl;
            ReleasePageUrl = releasePageUrl;
        }
    }

public static class UpdateService
{
    private static readonly HttpClient Http = new HttpClient();
    private const string AppExecutableName = "DepScope.Desktop";

    public static async Task<UpdateInfo?> CheckForUpdateAsync(
        string owner,
        string repo, 
        Version currentVersion,
        CancellationToken ct = default)
    {
        var escapedOwner = Uri.EscapeDataString(owner);
        var escapedRepo = Uri.EscapeDataString(repo);
        var apiUrl = $"https://api.github.com/repos/{escapedOwner}/{escapedRepo}/releases/latest";

        using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        req.Headers.UserAgent.ParseAdd("DepScope-Updater/1.0");

        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var versionString = tag.TrimStart('v', 'V');

        if (!Version.TryParse(versionString, out var latestVersion))
            return null;

        if (latestVersion <= currentVersion)
            return null;

        var rid = GetCurrentRid();

        string? assetUrl = null;
        string? assetName = null;
        string? checksumUrl = null;
        if (root.TryGetProperty("assets", out var assetsElem))
        {
            foreach (var asset in assetsElem.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (IsUpdateArchiveForRid(name, rid))
                {
                    assetUrl = asset.GetProperty("browser_download_url").GetString();
                    assetName = name;
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(assetName))
            {
                foreach (var asset in assetsElem.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (IsChecksumForAsset(name, assetName))
                    {
                        checksumUrl = asset.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            }
        }

        var htmlUrl = root.GetProperty("html_url").GetString() ?? $"https://github.com/{escapedOwner}/{escapedRepo}/releases";

        return new UpdateInfo(latestVersion, assetUrl, checksumUrl, htmlUrl);
    }

    private static bool IsUpdateArchiveForRid(string assetName, string rid)
    {
        return assetName.Contains(rid, StringComparison.OrdinalIgnoreCase) &&
               assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
               !assetName.EndsWith(".sha256.zip", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsChecksumForAsset(string checksumName, string assetName)
    {
        return checksumName.Equals(assetName + ".sha256", StringComparison.OrdinalIgnoreCase) ||
               checksumName.Equals(assetName + ".sha256.txt", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCurrentRid()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "win-x64";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "linux-x64";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // simple heuristic: assume arm64 if ARM, else x64
            return RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? "osx-arm64"
                : "osx-x64";
        }

        return "";
    }
    public static string GetDefaultUpdateFolder(Version latestVersion)
    {
        var baseDir = GetBaseUpdateRoot();
        var target = Path.Combine(baseDir, $"v{latestVersion}");
        Directory.CreateDirectory(target);
        return target;
    }

    public static string GetDefaultInstallFolder()
    {
        var localAppData = GetLocalAppDataRoot();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Path.Combine(localAppData, "Programs", "DepScope");

        return Path.Combine(localAppData, "DepScope", "Programs", "DepScope");
    }

    private static string GetBaseUpdateRoot()
    {
        var localAppData = GetLocalAppDataRoot();
        var dir = Path.Combine(localAppData, "DepScope", "Updates");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string GetLocalAppDataRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            return localAppData;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            return Path.Combine(home, ".local", "share");

        return AppContext.BaseDirectory;
    }

    public static async Task<string?> DownloadAndExtractAsync(
        UpdateInfo info,
        string targetDir,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(info.AssetDownloadUrl) ||
            string.IsNullOrWhiteSpace(info.ChecksumDownloadUrl))
            return null;

        var uri = new Uri(info.AssetDownloadUrl);
        var fileName = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrEmpty(fileName))
            fileName = "DepScope-update.zip";

        Directory.CreateDirectory(targetDir);
        var zipPath = Path.Combine(targetDir, fileName);

        using (var resp = await Http.GetAsync(info.AssetDownloadUrl, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(zipPath);
            await resp.Content.CopyToAsync(fs, ct);
        }

        var expectedSha256 = await DownloadExpectedSha256Async(info.ChecksumDownloadUrl, ct);
        if (string.IsNullOrWhiteSpace(expectedSha256) ||
            !await HasExpectedSha256Async(zipPath, expectedSha256, ct))
        {
            return null;
        }

        // If it's a zip, extract
        if (zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(zipPath, targetDir, overwriteFiles: true);
        }

        CleanupOldUpdateFolders(targetDir);

        return targetDir;
    }

    private static async Task<string?> DownloadExpectedSha256Async(string checksumUrl, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(checksumUrl, ct);
        resp.EnsureSuccessStatusCode();

        var text = await resp.Content.ReadAsStringAsync(ct);
        var firstToken = text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        if (firstToken is null || firstToken.Length != 64)
            return null;

        return firstToken.All(Uri.IsHexDigit) ? firstToken : null;
    }

    private static async Task<bool> HasExpectedSha256Async(
        string filePath,
        string expectedSha256,
        CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await SHA256.HashDataAsync(stream, ct);
        var actualSha256 = Convert.ToHexString(hashBytes);

        return actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    public static bool InstallAndRestart(string extractedUpdateFolder)
    {
        var sourceDir = GetExtractedPayloadDirectory(extractedUpdateFolder);
        if (sourceDir is null)
            return false;

        var installDir = GetDefaultInstallFolder();
        Directory.CreateDirectory(Path.GetDirectoryName(installDir) ?? installDir);

        var scriptPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? CreateWindowsUpdaterScript(sourceDir, installDir, extractedUpdateFolder)
            : CreateUnixUpdaterScript(sourceDir, installDir, extractedUpdateFolder);

        var psi = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }
            : new ProcessStartInfo
            {
                FileName = "/bin/sh",
                UseShellExecute = false,
                CreateNoWindow = true
            };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(scriptPath);
        }
        else
        {
            psi.ArgumentList.Add(scriptPath);
        }

        Process.Start(psi);
        return true;
    }

    private static string? GetExtractedPayloadDirectory(string extractedUpdateFolder)
    {
        var rid = GetCurrentRid();
        var ridFolder = Path.Combine(extractedUpdateFolder, rid);
        if (Directory.Exists(ridFolder) && ContainsAppExecutable(ridFolder))
            return ridFolder;

        var singlePayloadFolder = Directory
            .EnumerateDirectories(extractedUpdateFolder)
            .FirstOrDefault(ContainsAppExecutable);

        if (singlePayloadFolder is not null)
            return singlePayloadFolder;

        return ContainsAppExecutable(extractedUpdateFolder)
            ? extractedUpdateFolder
            : null;
    }

    private static bool ContainsAppExecutable(string folder)
    {
        return File.Exists(Path.Combine(folder, GetAppExecutableFileName()));
    }

    private static string GetAppExecutableFileName()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? AppExecutableName + ".exe"
            : AppExecutableName;
    }

    private static string CreateWindowsUpdaterScript(
        string sourceDir,
        string installDir,
        string stagingDir)
    {
        var scriptPath = GetUpdaterScriptPath(".cmd");
        var exeName = GetAppExecutableFileName();
        var pid = Environment.ProcessId;

        var script = $"""
@echo off
setlocal
set "PID={pid}"
set "SOURCE={sourceDir}"
set "TARGET={installDir}"
set "EXE={exeName}"
set "STAGING={stagingDir}"

:wait
tasklist /FI "PID eq %PID%" 2>NUL | find "%PID%" >NUL
if not errorlevel 1 (
    timeout /T 1 /NOBREAK >NUL
    goto wait
)

if exist "%TARGET%" rmdir /S /Q "%TARGET%"
mkdir "%TARGET%"
xcopy "%SOURCE%\*" "%TARGET%\" /E /I /Y /Q
start "" "%TARGET%\%EXE%"
rmdir /S /Q "%STAGING%"
del "%~f0"
""";

        File.WriteAllText(scriptPath, script);
        return scriptPath;
    }

    private static string CreateUnixUpdaterScript(
        string sourceDir,
        string installDir,
        string stagingDir)
    {
        var scriptPath = GetUpdaterScriptPath(".sh");
        var exeName = GetAppExecutableFileName();
        var pid = Environment.ProcessId;

        var script = $"""
#!/bin/sh
PID='{pid}'
SOURCE='{EscapeSingleQuotedShellValue(sourceDir)}'
TARGET='{EscapeSingleQuotedShellValue(installDir)}'
EXE='{EscapeSingleQuotedShellValue(exeName)}'
STAGING='{EscapeSingleQuotedShellValue(stagingDir)}'

while kill -0 "$PID" 2>/dev/null; do
    sleep 1
done

rm -rf "$TARGET"
mkdir -p "$TARGET"
cp -R "$SOURCE"/. "$TARGET"/
chmod +x "$TARGET/$EXE" 2>/dev/null || true
nohup "$TARGET/$EXE" >/dev/null 2>&1 &
rm -rf "$STAGING"
rm -- "$0"
""";

        File.WriteAllText(scriptPath, script);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                File.SetUnixFileMode(
                    scriptPath,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
            }
            catch
            {
                // The script is still launched through /bin/sh, so executable mode is best-effort.
            }
        }

        return scriptPath;
    }

    private static string GetUpdaterScriptPath(string extension)
    {
        var dir = Path.Combine(Path.GetTempPath(), "DepScope", "Updater");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"update-{Guid.NewGuid():N}{extension}");
    }

    private static string EscapeSingleQuotedShellValue(string value)
    {
        return value.Replace("'", "'\"'\"'");
    }

    private static void CleanupOldUpdateFolders(string keepFolder)
    {
        var updateRoot = GetBaseUpdateRoot();
        var keepFullPath = Path.GetFullPath(keepFolder);
        var updateRootFullPath = Path.GetFullPath(updateRoot);

        if (!keepFullPath.StartsWith(updateRootFullPath, StringComparison.OrdinalIgnoreCase))
            return;

        foreach (var directory in Directory.EnumerateDirectories(updateRootFullPath))
        {
            var directoryFullPath = Path.GetFullPath(directory);
            if (directoryFullPath.Equals(keepFullPath, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                Directory.Delete(directoryFullPath, recursive: true);
            }
            catch
            {
                // Cleanup is best-effort; stale update folders should not block installation.
            }
        }
    }

    public static void OpenFolder(string folderPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = folderPath,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch
        {
            // ignore
        }
    }

}

