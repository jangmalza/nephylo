using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using UsageWidget;

internal static class WslRuntimeTests
{
    private static int assertions;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static void Check(bool condition,string message) { assertions++; if (!condition) throw new Exception(message); }
    private static async Task<Exception> Fails(Task task) { try { await task; } catch (Exception error) { return error; } throw new Exception("Expected failure"); }
    private static ProcessStartInfo Child(params string[] args)
    {
        return new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,String.Join(" ",args.Select(WslRuntime.QuoteArgument))) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
        };
    }
    private static async Task Test()
    {
        Check(WslRuntime.IsValidDistributionName("Ubuntu") && WslRuntime.IsValidDistributionName("My Linux 한글"),"Distro names retain spaces and Unicode");
        foreach (string bad in new[] { "", " ", " Ubuntu", "../Ubuntu", "Ubuntu\\x", "A\nB", "x\0y", "x:y", new String('x',257) })
            Check(!WslRuntime.IsValidDistributionName(bad),"Invalid distro rejected");
        var names = WslRuntime.ParseDistributionList("\uFEFFUbuntu\r\nkali-linux\r\nUbuntu\r\n");
        Check(names.SequenceEqual(new[] { "Ubuntu", "kali-linux" }),"List decoding preserves exact names and deduplicates");
        var unicode = Encoding.Unicode.GetBytes("Ubuntu\r\n우분투\r\n");
        Check(WslRuntime.DecodeOutput(unicode).Contains("우분투"),"UTF16 WSL listing is decoded");
        Check(WslRuntime.DecodeOutput(Encoding.UTF8.GetBytes("/home/사용자")).EndsWith("사용자"),"UTF8 Linux paths decoded independently");
        var nonAsciiList=await WslRuntime.RunProcessAsync(Child("--list","--quiet"),null,2000,CancellationToken.None);
        Check(WslRuntime.ParseDistributionList(nonAsciiList.Output).SequenceEqual(new[] { "우분투", "Linux 😀" }),"WSL list contract decodes UTF16 even when the first distro has no ASCII prefix or BOM");
        var parsed = WslRuntime.ParseProbe("Ubuntu","NEPHYLO_WSL_V1\0/home/user\0/home/user/.local/bin/codex\0\0");
        Check(parsed.CodexPath.EndsWith("codex") && parsed.ClaudePath == "","Missing optional CLI is distinct from probe failure");
        Check(WslRuntime.ToUncPath("Ubuntu","/home/user/.claude/settings.json") == @"\\wsl$\Ubuntu\home\user\.claude\settings.json","UNC maps exact distribution");
        try { WslRuntime.ToUncPath("Ubuntu","/home/../root"); throw new Exception("Expected unsafe path"); } catch (ArgumentException) { assertions++; }

        var samples = new[] { "", "space path", "a\"quoted\"b", @"C:\a path\", "$(touch /tmp/no); & x", "한글", "\"\\\"" };
        var quoted = await WslRuntime.RunProcessAsync(Child(new[] { "--echo" }.Concat(samples).ToArray()),null,2000,CancellationToken.None);
        var roundtrip = Json.Deserialize<string[]>(quoted.Output);
        Check(samples.SequenceEqual(roundtrip),"Windows argument quoting roundtrips spaces, quotes, trailing slashes and shell metacharacters as literal arguments");
        var timer = Stopwatch.StartNew();
        var timeout = await Fails(WslRuntime.RunProcessAsync(Child("--sleep"),null,150,CancellationToken.None));
        Check(timeout is ConnectionException && timer.ElapsedMilliseconds < 3000,"Hanging child is bounded and gives a safe WSL diagnostic");
        using (var cancel = new CancellationTokenSource(100)) {
            var canceled = await Fails(WslRuntime.RunProcessAsync(Child("--sleep"),null,3000,cancel.Token));
            Check(canceled is OperationCanceledException,"Explicit cancellation remains cancellation");
        }
        var overflow = await Fails(WslRuntime.RunProcessAsync(Child("--flood"),null,1000,CancellationToken.None));
        Check(overflow is ConnectionException && !overflow.Message.Contains("PRIVATE"),"Excess output is bounded and never shown as provider diagnostics");

        int launches = 0;
        WslRuntime.RunnerOverride = (info,input,ms,ct) => {
            launches++;
            if (info.Arguments == "--list --quiet") return Task.FromResult(new WslCommandResult { Output = "Ubuntu\r\nMy Linux\r\n" });
            Check(info.Arguments.Contains("--exec /bin/sh -c"),"Execute uses fixed shell, never interactive login");
            Check(!info.Arguments.Contains("--shutdown") && !info.Arguments.Contains("--install"),"Probe cannot install or shutdown a distribution");
            return Task.FromResult(new WslCommandResult { Output = "NEPHYLO_WSL_V1\0/home/user\0/home/user/.local/bin/codex\0/home/user/.local/bin/claude\0" });
        };
        var found = await WslRuntime.DiscoverAsync();
        Check(found.Count == 2 && launches == 3,"Discovery probes each installed distribution only");
        WslDistribution cached;
        Check(WslRuntime.TryGetCachedDistribution("Ubuntu",out cached) && launches == 3,"Cached metadata does not launch a process during refresh");
        var missing = await Fails(WslRuntime.ProbeAsync("ubuntu"));
        Check(missing is ConnectionException && launches == 4,"Exact distro matching blocks wrong case and does not probe an unlisted distro");
        var infoCodex = WslRuntime.CreateCodexStartInfo("Ubuntu");
        Check(infoCodex.Arguments.Contains("unset OPENAI_API_KEY CODEX_API_KEY") && !infoCodex.UseShellExecute && infoCodex.RedirectStandardInput,"Codex authenticates inside selected Linux environment with stdio transport");
        Check(!infoCodex.Arguments.Contains(".bashrc") && !infoCodex.Arguments.Contains(".profile"),"Discovery never executes user profile scripts");
        WslRuntime.RunnerOverride = null;
    }
    public static int Main(string[] args)
    {
        if (args.Length==2 && args[0]=="--list" && args[1]=="--quiet") { var bytes=Encoding.Unicode.GetBytes("우분투\r\nLinux 😀\r\n"); Console.OpenStandardOutput().Write(bytes,0,bytes.Length); return 0; }
        if (args.Length > 0 && args[0] == "--echo") { Console.OutputEncoding = new UTF8Encoding(false); Console.Write(Json.Serialize(args.Skip(1).ToArray())); return 0; }
        if (args.Length > 0 && args[0] == "--sleep") { Thread.Sleep(10000); return 0; }
        if (args.Length > 0 && args[0] == "--flood") { Console.Write(new String('x',WslRuntime.OutputLimit+100)); return 0; }
        if (args.Length > 0 && args[0] == "--live-probe") {
            try {
                foreach (var item in WslRuntime.DiscoverAsync().GetAwaiter().GetResult()) {
                    Console.WriteLine(Json.Serialize(item));
                    if (item.Error != "") continue;
                    var literals = new[] { "space path", "a\"quoted\"b", @"C:\a path\", "$(printf BAD); & x", "한글", "'single quotes'" };
                    var result = WslRuntime.ExecuteAsync(item.Name,"printf '%s\\000' \"$@\"",literals).GetAwaiter().GetResult();
                    if (result.ExitCode != 0 || !literals.SequenceEqual(result.Output.TrimEnd('\0').Split('\0'))) throw new Exception("Live WSL literal argument roundtrip failed.");
                    Console.WriteLine("Literal argument roundtrip passed: " + item.Name);
                }
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        }
        try { Test().GetAwaiter().GetResult(); Console.WriteLine("WSL runtime tests passed: " + assertions + " (fake processes; no configuration or credentials changed)."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { WslRuntime.RunnerOverride = null; }
    }
}
