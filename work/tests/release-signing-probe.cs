using System;
using System.IO;
using UsageWidget;

namespace UsageWidget
{
    internal static class SettingsStore
    {
        internal static string DataDirectory { get { throw new InvalidOperationException("No runtime storage in release verification."); } }
        internal static string InstallDirectory { get { return DataDirectory; } }
    }
}

internal static class ReleaseSigningProbe
{
    internal static int Main(string[] args)
    {
        try {
            if (args.Length != 2) return 2;
            var manifest = AutoUpdateManifest.ParseAndVerify(File.ReadAllText(args[0]));
            manifest.VerifyInstaller(args[1]);
            return manifest.VersionText == "1.21.0" ? 0 : 3;
        } catch { return 1; }
    }
}
