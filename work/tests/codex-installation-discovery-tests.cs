using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using UsageWidget;

internal static class CodexInstallationDiscoveryTests
{
    private const string Package = "OpenAI.Codex_26.1007.2314.0_x64__2p2nqsd0c76g0";
    private const string Root = @"Z:\Registered Apps\Codex";
    private static int checks;

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
        checks++;
    }

    private sealed class FakeApi : ICodexPackageApi
    {
        internal string[] Names = { Package };
        internal readonly Dictionary<string, string> Roots = new Dictionary<string, string> { { Package, Root } };
        internal readonly List<string> PathQueries = new List<string>();
        internal Exception Failure;
        internal int QueryError = 122, FillError, PathError;
        internal uint? CountOverride, LengthOverride, PathLengthOverride;
        internal bool GrowNamesOnce, GrowNamesAlways, GrowPathOnce, GrowPathAlways;
        internal bool InvalidPointer, OddPointer, Unterminated, OversizedResultCount, OversizedResultLength, OversizedPathResult;
        internal int NameCalls, PathCalls;

        public int GetPackages(string family, ref uint count, IntPtr[] names, ref uint length, IntPtr buffer)
        {
            Check(family == CodexInstallationDiscovery.PackageFamily, "Only the known Codex package family is queried");
            NameCalls++;
            if (Failure != null) throw Failure;
            char[] characters = String.Concat(Names.Select(value => value + "\0")).ToCharArray();
            if (names == null) {
                Check(buffer == IntPtr.Zero, "Sizing call supplies no character buffer");
                count = CountOverride ?? (uint)Names.Length;
                length = LengthOverride ?? (uint)characters.Length;
                return Names.Length == 0 ? 0 : QueryError;
            }
            if (GrowNamesAlways || GrowNamesOnce && NameCalls == 2) { length += 32; return 122; }
            if (FillError != 0) return FillError;
            if (Unterminated) for (int index = 0; index < characters.Length; index++) if (characters[index] == '\0') characters[index] = 'X';
            Marshal.Copy(characters, 0, buffer, characters.Length);
            int offset = 0;
            for (int index = 0; index < Names.Length; index++) { names[index] = IntPtr.Add(buffer, offset * 2); offset += Names[index].Length + 1; }
            if (InvalidPointer) names[0] = IntPtr.Add(buffer, -2);
            if (OddPointer) names[0] = IntPtr.Add(buffer, 1);
            count = OversizedResultCount ? count + 1 : (uint)Names.Length;
            length = OversizedResultLength ? length + 1 : (uint)characters.Length;
            return 0;
        }

        public int GetPath(string name, ref uint length, StringBuilder path)
        {
            PathCalls++;
            string root;
            if (!Roots.TryGetValue(name, out root)) return 1168;
            if (PathError != 0) return PathError;
            if (path == null) {
                PathQueries.Add(name);
                length = PathLengthOverride ?? (uint)(root.Length + 1);
                return 122;
            }
            if (GrowPathAlways || GrowPathOnce && PathCalls == 2) { length += 32; return 122; }
            path.Append(root);
            length = OversizedPathResult ? length + 1 : (uint)(root.Length + 1);
            return 0;
        }
    }

    private static string Find(FakeApi api, params string[] files)
    {
        var available = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        return CodexInstallationDiscovery.FindRegisteredExecutable(api, candidate => available.Contains(candidate));
    }

    private static void Test()
    {
        string primary = Path.Combine(Root, @"app\resources\codex.exe");
        var api = new FakeApi();
        Check(Find(api, primary) == primary, "A current-user registered app is discovered without listing WindowsApps or reading account files");
        Check(api.NameCalls == 2 && api.PathCalls == 2, "Stable registration uses one sizing and one fill call per API");
        foreach (string relative in new[] { @"resources\codex.exe", "codex.exe" }) {
            string file = Path.Combine(Root, relative);
            Check(Find(new FakeApi(), file) == file, "Known package executable layout is accepted: " + relative);
        }
        Check(Find(new FakeApi(), Path.Combine(Root, "CodexGUI.exe")) == null, "An unrelated executable in the registered package is never selected");
        Check(Find(new FakeApi()) == null, "A registered app with no CLI binary is not reported as connected");
        api = new FakeApi { Names = new string[0] };
        Check(Find(api, primary) == null && api.PathCalls == 0, "No registered package returns no match without filesystem discovery");

        foreach (int error in new[] { 5, 87, 1168 }) {
            api = new FakeApi { QueryError = error };
            Check(Find(api, primary) == null && api.NameCalls == 1, "Enumeration error safely falls back: " + error);
            api = new FakeApi { FillError = error };
            Check(Find(api, primary) == null && api.PathCalls == 0, "Enumeration fill error safely falls back: " + error);
            api = new FakeApi { PathError = error };
            Check(Find(api, primary) == null, "Path resolution error safely falls back: " + error);
        }
        foreach (Exception error in new Exception[] { new UnauthorizedAccessException(), new DllNotFoundException(), new EntryPointNotFoundException(), new BadImageFormatException(), new IOException(), new System.Security.SecurityException() }) {
            api = new FakeApi { Failure = error };
            Check(Find(api, primary) == null, "Unavailable/denied package API does not prevent legacy discovery: " + error.GetType().Name);
        }
        api = new FakeApi();
        string secondary = Path.Combine(Root, @"resources\codex.exe");
        Check(CodexInstallationDiscovery.FindRegisteredExecutable(api, candidate => { if (candidate == primary) throw new UnauthorizedAccessException(); return candidate == secondary; }) == secondary,
            "A denied candidate does not prevent checking another known binary in that package");

        foreach (uint value in new uint[] { 0, 65, UInt32.MaxValue }) {
            api = new FakeApi { CountOverride = value };
            Check(Find(api, primary) == null && api.NameCalls == 1, "Package count is bounded before allocation: " + value);
        }
        foreach (uint value in new uint[] { 0, 65537, UInt32.MaxValue }) {
            api = new FakeApi { LengthOverride = value };
            Check(Find(api, primary) == null && api.NameCalls == 1, "Package character buffer is bounded before allocation: " + value);
        }
        foreach (uint value in new uint[] { 0, 3, 32769, UInt32.MaxValue }) {
            api = new FakeApi { PathLengthOverride = value };
            Check(Find(api, primary) == null && api.PathCalls == 1, "Install path buffer is bounded before allocation: " + value);
        }
        foreach (var invalid in new[] {
            new FakeApi { InvalidPointer = true }, new FakeApi { OddPointer = true }, new FakeApi { Unterminated = true },
            new FakeApi { OversizedResultCount = true }, new FakeApi { OversizedResultLength = true }, new FakeApi { OversizedPathResult = true }
        }) Check(Find(invalid, primary) == null, "Malformed native result is rejected before use");

        api = new FakeApi { GrowNamesOnce = true };
        Check(Find(api, primary) == primary && api.NameCalls == 3, "One package registration resize is retried");
        api = new FakeApi { GrowNamesAlways = true };
        Check(Find(api, primary) == null && api.NameCalls == 3, "Repeated package changes cannot create an unbounded loop");
        api = new FakeApi { GrowPathOnce = true };
        Check(Find(api, primary) == primary && api.PathCalls == 3, "One install path resize is retried");
        api = new FakeApi { GrowPathAlways = true };
        Check(Find(api, primary) == null && api.PathCalls == 3, "Repeated path changes cannot create an unbounded loop");

        foreach (string path in new[] { "relative", @"Z:relative", @"\root-relative", @"\\server\share\Codex", "Z:\\bad\"path", "Z:\\bad\0path" }) {
            api = new FakeApi(); api.Roots[Package] = path;
            bool probed = false;
            Check(CodexInstallationDiscovery.FindRegisteredExecutable(api, candidate => { probed = true; return true; }) == null && !probed,
                "Only absolute local package roots reach the executable check");
        }
        api = new FakeApi { Names = new[] { "Other.App_1.0.0.0_x64__2p2nqsd0c76g0", "OpenAI.Codex_1.0.0.0_x64__differentpublisher", "OpenAI.Codex_../_x64__2p2nqsd0c76g0" } };
        Check(Find(api, primary) == null && api.PathCalls == 0, "Unexpected package families and malformed full names cannot supply executable roots");
        api = new FakeApi { Names = new[] { Package, Package } };
        Check(Find(api) == null && api.PathQueries.Count == 1, "Duplicate registration names are deduplicated");

        const string older = "OpenAI.Codex_9.1007.2314.0_x64__2p2nqsd0c76g0";
        const string newer = "OpenAI.Codex_10.1007.2314.0_x64__2p2nqsd0c76g0";
        api = new FakeApi { Names = new[] { older, newer } };
        api.Roots[older] = @"Z:\Old Codex"; api.Roots[newer] = @"Z:\New Codex";
        string oldBinary = Path.Combine(api.Roots[older], @"app\resources\codex.exe");
        string newBinary = Path.Combine(api.Roots[newer], @"app\resources\codex.exe");
        Check(Find(api, oldBinary, newBinary) == newBinary, "Registered versions sort numerically rather than lexically (10 before 9)");
        api.PathQueries.Clear();
        Check(Find(api, oldBinary) == oldBinary && api.PathQueries.SequenceEqual(new[] { newer, older }), "Missing binary in a newer registration falls back to the next registered package");
        api.Roots.Remove(newer); api.PathQueries.Clear();
        Check(Find(api, oldBinary) == oldBinary, "A registration removed before path lookup does not hide the remaining package");
    }

    public static int Main()
    {
        try { Test(); Console.WriteLine("Codex installation discovery tests passed: " + checks + " (synthetic native API; no installed packages, accounts, processes or network queried)."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
