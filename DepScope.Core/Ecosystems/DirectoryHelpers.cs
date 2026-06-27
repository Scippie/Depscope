using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Threading;

namespace DepScope.Core.Ecosystems;

internal static class DirectoryHelpers
{
    private static readonly AsyncLocal<FileIndexScope?> CurrentFileIndex = new();

    // Directories we want to completely skip when scanning
    private static readonly string[] IgnoredDirectoryNames =
    {
        "node_modules",
        ".git",
        "bin",
        "obj",
        "dist",
        "build",
        ".vs",
        "node_modules",
        ".next",
        ".turbo",
        ".vercel",
        "target",
        "venv",
        ".venv",
        ".tox",
        ".pytest_cache" 
    };

    public static IDisposable UseFileIndex(string rootPath)
    {
        var previous = CurrentFileIndex.Value;
        CurrentFileIndex.Value = new FileIndexScope(
            Path.GetFullPath(rootPath),
            EnumerateAllFilesSafe(rootPath).ToArray(),
            previous);

        return CurrentFileIndex.Value;
    }

    public static IEnumerable<string> EnumerateFilesSafe(
        string rootPath,
        string searchPattern)
    {
        var currentIndex = CurrentFileIndex.Value;
        if (currentIndex is not null &&
            PathsEqualOrNested(Path.GetFullPath(rootPath), currentIndex.RootPath))
        {
            return currentIndex.Files.Where(file =>
                PathsEqualOrNested(Path.GetFullPath(file), currentIndex.RootPath) &&
                FileSystemName.MatchesSimpleExpression(
                    searchPattern,
                    Path.GetFileName(file),
                    ignoreCase: OperatingSystem.IsWindows()));
        }

        return EnumerateFilesSafe(rootPath, searchPattern, rootPath);
    }

    private static IEnumerable<string> EnumerateAllFilesSafe(string rootPath)
    {
        return EnumerateFilesSafe(rootPath, "*", rootPath);
    }

    private static IEnumerable<string> EnumerateFilesSafe(
        string currentDir,
        string searchPattern,
        string rootPath)
    {
        IEnumerable<string> files = Enumerable.Empty<string>();

        try
        {
            files = Directory.EnumerateFiles(currentDir, searchPattern, SearchOption.TopDirectoryOnly);
        }
        catch
        {
            //ignore
        }

        foreach (var file in files)
            yield return file;

        IEnumerable<string> subdirs = Enumerable.Empty<string>();

        try
        {
            subdirs = Directory.EnumerateDirectories(currentDir);
        }
        catch
        {
            //ignore
        }

        foreach (var dir in subdirs)
        {
            var name = Path.GetFileName(dir);
            if (IgnoredDirectoryNames.Any(ign =>
                    string.Equals(ign, name, StringComparison.OrdinalIgnoreCase)))
            {
                // skip this subtree 
                continue;
            }

            foreach (var f in EnumerateFilesSafe(dir, searchPattern, rootPath))
                yield return f;
        }
    }

    private static bool PathsEqualOrNested(string path, string rootPath)
    {
        var normalizedRoot = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (path.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        return path.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FileIndexScope : IDisposable
    {
        public string RootPath { get; }
        public IReadOnlyList<string> Files { get; }

        private readonly FileIndexScope? _previous;
        private bool _disposed;

        public FileIndexScope(string rootPath, IReadOnlyList<string> files, FileIndexScope? previous)
        {
            RootPath = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Files = files;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            CurrentFileIndex.Value = _previous;
            _disposed = true;
        }
    }
}


