using System;
using System.IO;
using System.Linq;

namespace NomisKitchen.Reports
{
    internal static class HearthstonePaths
    {
        internal static string InstallDir(ReporterConfig config)
        {
            if (IsInstall(config?.HearthstoneDir)) return config.HearthstoneDir;
            var detected = config?.DetectedHearthstoneDir;
            if (IsInstall(detected)) return detected;
            foreach (var candidate in new[] { @"C:\Program Files (x86)\Hearthstone", @"C:\Program Files\Hearthstone" })
                if (IsInstall(candidate)) return candidate;
            return null;
        }

        internal static string NewestPowerLog(string installDir)
        {
            if (installDir == null) return null;
            var logs = Path.Combine(installDir, "Logs");
            if (!Directory.Exists(logs)) return null;
            var session = Directory.GetDirectories(logs, "Hearthstone_*")
                .Select(d => new FileInfo(Path.Combine(d, "Power.log")))
                .Where(f => f.Exists && f.Length > 0)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (session != null) return session.FullName;
            var flat = Path.Combine(logs, "Power.log");
            return File.Exists(flat) ? flat : null;
        }

        internal static string BepInExLog(string installDir) =>
            installDir == null ? null : Path.Combine(installDir, "BepInEx", "LogOutput.log");

        internal static string BepInExPlugins(string installDir) =>
            installDir == null ? null : Path.Combine(installDir, "BepInEx", "plugins");

        static bool IsInstall(string dir) =>
            !string.IsNullOrEmpty(dir) && Directory.Exists(dir) && File.Exists(Path.Combine(dir, "Hearthstone.exe"));
    }
}
