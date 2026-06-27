using System;

namespace DepScope.Core.Models;

public static class VersionUpdateClassifier
{
    /// <summary>
    /// Classify difference between current and latest as None / Patch / Minor / Major.
    /// </summary>
    public static VersionUpdateType GetUpdateType(Version current, Version latest)
    {
        if (latest <= current)
            return VersionUpdateType.None;

        if (latest.Major > current.Major)
            return VersionUpdateType.Major;

        if (latest.Minor > current.Minor)
            return VersionUpdateType.Minor;

        // Treat changes in Build/Revision as patch updates
        return VersionUpdateType.Patch;
    }
}

