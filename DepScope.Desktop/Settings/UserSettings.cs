using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DepScope.Desktop.Settings
{
    public sealed class UserSettings
    {
        public List<string> RecentRoots { get; set; } = new();
        public bool StartWithSystem { get; set; } = false;
        public bool UseSystemTray { get; set; } = false;
        public bool DarkMode { get; set; } = true;
        public bool OfflineMode { get; set; } = false;
        public string NuGetSourceUrl { get; set; } = string.Empty;
        public string NpmRegistryBaseUrl { get; set; } = string.Empty;
        public string GoProxyBaseUrl { get; set; } = string.Empty;
        public string PythonPackageIndexBaseUrl { get; set; } = string.Empty;
        public string PackagistMetadataBaseUrl { get; set; } = string.Empty;
        public string MavenSearchBaseUrl { get; set; } = string.Empty;
        public string CratesApiBaseUrl { get; set; } = string.Empty;
        public string GitHubApiBaseUrl { get; set; } = string.Empty;
        public bool AutoRescan { get; set; } = false;
        public bool CheckUpdatesOnStart { get; set; } = false;
        public bool AutoDownloadUpdates { get; set; } = false;
        public bool EnableNotifications { get; set; } = true;
    }
}
