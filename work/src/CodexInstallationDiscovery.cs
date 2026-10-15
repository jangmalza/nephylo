using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace UsageWidget
{
    internal interface ICodexPackageApi
    {
        int GetPackages(string family, ref uint count, IntPtr[] names, ref uint length, IntPtr buffer);
        int GetPath(string fullName, ref uint length, StringBuilder path);
    }

    internal static class CodexInstallationDiscovery
    {
        internal const string PackageFamily = "OpenAI.Codex_2p2nqsd0c76g0";
        private const int InsufficientBuffer = 122;
        private const uint MaxPackages = 64;
        private const uint MaxNameBuffer = 65536;
        private const uint MaxPathBuffer = 32768;
        private static readonly string[] RelativeExecutables = { @"app\resources\codex.exe", @"resources\codex.exe", "codex.exe" };

        // These APIs resolve only this user's registered package, without listing the
        // protected WindowsApps directory or reading any Codex account/config files.
        // https://learn.microsoft.com/windows/win32/api/appmodel/nf-appmodel-getpackagesbypackagefamily
        // https://learn.microsoft.com/windows/win32/api/appmodel/nf-appmodel-getpackagepathbyfullname
        internal static string FindRegisteredExecutable()
        {
            return FindRegisteredExecutable(new NativePackageApi(), File.Exists);
        }

        internal static string FindRegisteredExecutable(ICodexPackageApi api, Func<string, bool> fileExists)
        {
            try {
                foreach (var name in GetPackageNames(api).OrderByDescending(PackageVersion).ThenByDescending(value => value, StringComparer.OrdinalIgnoreCase)) {
                    string root = GetPackagePath(api, name);
                    if (root == null) continue;
                    foreach (var relative in RelativeExecutables) {
                        string candidate = Path.Combine(root, relative);
                        try { if (fileExists(candidate)) return candidate; }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                        catch (System.Security.SecurityException) { }
                    }
                }
            } catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (BadImageFormatException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
            return null;
        }

        private static List<string> GetPackageNames(ICodexPackageApi api)
        {
            var result = new List<string>();
            uint count = 0, length = 0;
            if (api.GetPackages(PackageFamily, ref count, null, ref length, IntPtr.Zero) != InsufficientBuffer) return result;
            // One bounded retry covers package registration changes between sizing
            // and filling the buffers, without making discovery a polling loop.
            for (int attempt = 0; attempt < 2; attempt++) {
                if (count == 0 || count > MaxPackages || length == 0 || length > MaxNameBuffer) return result;
                var pointers = new IntPtr[(int)count];
                int capacity = (int)length;
                IntPtr buffer = Marshal.AllocHGlobal(capacity * 2);
                try {
                    Marshal.Copy(new byte[capacity * 2], 0, buffer, capacity * 2);
                    uint returnedCount = count, returnedLength = length;
                    int error = api.GetPackages(PackageFamily, ref returnedCount, pointers, ref returnedLength, buffer);
                    if (error == InsufficientBuffer) { count = returnedCount; length = returnedLength; continue; }
                    if (error != 0 || returnedCount > count || returnedLength > length) return result;
                    for (int index = 0; index < returnedCount; index++) {
                        long offset = pointers[index].ToInt64() - buffer.ToInt64();
                        if (offset < 0 || offset >= returnedLength * 2L || offset % 2 != 0) continue;
                        int available = Math.Min(1025, (int)returnedLength - (int)(offset / 2));
                        string value = Marshal.PtrToStringUni(pointers[index], available);
                        int end = value.IndexOf('\0');
                        if (end <= 0 || end > 1024) continue;
                        value = value.Substring(0, end);
                        if (IsCodexPackage(value) && !result.Contains(value, StringComparer.OrdinalIgnoreCase)) result.Add(value);
                    }
                    return result;
                } finally { Marshal.FreeHGlobal(buffer); }
            }
            return result;
        }

        private static bool IsCodexPackage(string value)
        {
            if (!value.StartsWith("OpenAI.Codex_", StringComparison.Ordinal) || !value.EndsWith("_2p2nqsd0c76g0", StringComparison.Ordinal)) return false;
            foreach (char character in value)
                if (!(character >= 'A' && character <= 'Z') && !(character >= 'a' && character <= 'z') && !(character >= '0' && character <= '9') && character != '.' && character != '-' && character != '_') return false;
            return true;
        }

        private static Version PackageVersion(string name)
        {
            Version version;
            var parts = name.Split('_');
            return parts.Length > 1 && Version.TryParse(parts[1], out version) ? version : new Version(0, 0);
        }

        private static string GetPackagePath(ICodexPackageApi api, string name)
        {
            uint length = 0;
            if (api.GetPath(name, ref length, null) != InsufficientBuffer) return null;
            for (int attempt = 0; attempt < 2; attempt++) {
                if (length < 4 || length > MaxPathBuffer) return null;
                int capacity = (int)length;
                var path = new StringBuilder(capacity);
                int error = api.GetPath(name, ref length, path);
                if (error == InsufficientBuffer) continue;
                if (error != 0 || length > capacity || path.Length == 0 || path.Length >= capacity) return null;
                string value = path.ToString();
                try {
                    // App packages live on an absolute local drive, which may differ
                    // from ProgramFiles. A drive-relative path must not use our CWD.
                    string drive = Path.GetPathRoot(value);
                    if (drive == null || drive.Length != 3 || drive[1] != ':' || (drive[2] != '\\' && drive[2] != '/')) return null;
                    if (value.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
                    return Path.GetFullPath(value);
                } catch (ArgumentException) { return null; }
                catch (NotSupportedException) { return null; }
                catch (IOException) { return null; }
            }
            return null;
        }

        private sealed class NativePackageApi : ICodexPackageApi
        {
            public int GetPackages(string family, ref uint count, IntPtr[] names, ref uint length, IntPtr buffer)
            {
                return GetPackagesByPackageFamily(family, ref count, names, ref length, buffer);
            }
            public int GetPath(string fullName, ref uint length, StringBuilder path)
            {
                return GetPackagePathByFullName(fullName, ref length, path);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetPackagesByPackageFamily(string packageFamilyName, ref uint count, [Out] IntPtr[] packageFullNames, ref uint bufferLength, IntPtr buffer);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetPackagePathByFullName(string packageFullName, ref uint pathLength, [Out] StringBuilder path);
    }
}
