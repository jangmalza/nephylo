using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using UsageWidget;

internal static class CodexLoginTests
{
    private const string Id = "c131b050-4a51-4689-825a-3e0313e52e26";
    private static int assertions;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    private static void Send(object message) { Console.WriteLine(Json.Serialize(message)); Console.Out.Flush(); }
    private static void Notify(string id, bool success) { Send(new { method = "account/login/completed", @params = new { loginId = id, success = success, error = success ? null : "PRIVATE_PROVIDER_ERROR" } }); }
    private static int Server(string source)
    {
        string scenario = Environment.GetEnvironmentVariable("USAGE_WIDGET_CODEX_TEST_SCENARIO");
        if (String.IsNullOrEmpty(scenario)) return 2;
        string line;
        while ((line = Console.ReadLine()) != null) {
            var request = Json.Deserialize<Dictionary<string,object>>(line);
            if (!request.ContainsKey("id")) continue;
            object id = request["id"]; string method = (string)request["method"];
            if (method == "initialize") Send(new { id = id, result = new {} });
            else if (method == "account/login/start") {
                if (scenario == "early") Notify(Id,true);
                if (scenario == "start-cancel") { Thread.Sleep(10000); continue; }
                Send(new { id = id, result = new { type = "chatgpt", loginId = Id, authUrl = scenario == "url" ? "https://example.com/not-allowed" : "https://auth.openai.com/synthetic-login" } });
                if (scenario == "disconnect") return 0;
                if (scenario == "success") Notify(Id,true);
                if (scenario == "failure") Notify(Id,false);
                if (scenario == "wrong-id") Notify("3c90baf7-1a91-42e9-8f6e-caa6a86802b2",true);
            }
            else if (method == "account/read") Send(new { id = id, result = new { account = new { type = "chatgpt", planType = "plus" } } });
            else if (method == "account/rateLimits/read") Send(new { id = id, result = new { rateLimits = new { primary = new { usedPercent = source == "Ubuntu" ? 12 : source == "Debian" ? 24 : 63, windowDurationMins = 300 } } } });
        }
        return 0;
    }
    private static async Task<Exception> Fails(Task task)
    {
        try { await task; } catch (Exception ex) { return ex; }
        throw new Exception("Expected login failure");
    }
    private static async Task Test()
    {
        var recovery = new CodexBrowserRecovery();
        foreach (string bad in new[] { "http://auth.openai.com/", "https://openai.com.evil.example/", "https://auth.openai.com@evil.example/", "https://user@auth.openai.com/", "https://auth.openai.com:8443/", "javascript:alert(1)", "file:///C:/test", "https://evil.example/?next=https://auth.openai.com/" })
            Check(!CodexBrowserRecovery.IsAllowed(bad),"Unsafe recovery destination rejected: " + bad);
        recovery.Set("https://auth.openai.com/synthetic-login");
        try { recovery.Use(url => { throw new InvalidOperationException("synthetic browser launch error"); }); } catch (InvalidOperationException) { }
        string copied = null; recovery.Use(url => copied = url);
        Check(recovery.Available && copied == "https://auth.openai.com/synthetic-login","Failed browser launch retains a validated URL for explicit copy or reopen");
        recovery.Clear(); int uses = 0; recovery.Use(url => uses++);
        Check(!recovery.Available && uses == 0,"Finished or canceled recovery cannot reopen or copy an old login URL");
        recovery.Set("https://chatgpt.com/synthetic-login");
        try { recovery.Set("https://evil.example/"); } catch (ConnectionException) { }
        Check(!recovery.Available,"Rejected replacement clears the previous login destination");
        string exe = Assembly.GetExecutingAssembly().Location;
        foreach (string scenario in new[] { "success", "early" }) {
            Environment.SetEnvironmentVariable("USAGE_WIDGET_CODEX_TEST_SCENARIO",scenario);
            using (var service = new CodexService()) {
                int opened = 0;
                var snapshot = await service.LoginAsync(exe,url => { opened++; Check(url == "https://auth.openai.com/synthetic-login","Only validated auth URL passed to browser delegate"); },CancellationToken.None);
                Check(opened == 1 && snapshot.Rows[0].RemainingPercent == 37,"Successful matching notification queries quota automatically: " + scenario);
                var refreshed = await service.ReadAsync(exe);
                Check(refreshed.Rows[0].RemainingPercent == 37,"Login releases gate and preserves working server for refresh");
            }
        }
        foreach (string scenario in new[] { "failure", "disconnect", "url" }) {
            Environment.SetEnvironmentVariable("USAGE_WIDGET_CODEX_TEST_SCENARIO",scenario);
            using (var service = new CodexService()) {
                int opened = 0;
                var error = await Fails(service.LoginAsync(exe,url => opened++,CancellationToken.None));
                Check(!error.Message.Contains("PRIVATE"),"Raw provider error never leaks");
                Check(scenario != "url" || opened == 0,"Invalid URL never reaches browser delegate");
                Check(!(error is OperationCanceledException),"Provider failure is distinguished from user cancellation");
            }
        }
        Environment.SetEnvironmentVariable("USAGE_WIDGET_CODEX_TEST_SCENARIO","wait");
        using (var service = new CodexService()) using (var cancellation = new CancellationTokenSource()) {
            var error = await Fails(service.LoginAsync(exe,url => cancellation.Cancel(),cancellation.Token));
            Check(error is OperationCanceledException,"Explicit cancellation terminates waiting login");
            Environment.SetEnvironmentVariable("USAGE_WIDGET_CODEX_TEST_SCENARIO","success");
            var retry = await service.LoginAsync(exe,url => {},CancellationToken.None);
            Check(retry.Rows[0].RemainingPercent == 37,"Canceled flow releases process/gate and a fresh login succeeds");
        }
        Environment.SetEnvironmentVariable("USAGE_WIDGET_CODEX_TEST_SCENARIO","start-cancel");
        using (var service = new CodexService()) using (var cancellation = new CancellationTokenSource(250)) {
            int opened = 0;
            var error = await Fails(service.LoginAsync(exe,url => opened++,cancellation.Token));
            Check(error is OperationCanceledException && opened == 0,"Cancellation during start RPC never opens a late browser");
        }
        foreach (string scenario in new[] { "wait", "wrong-id" }) {
            Environment.SetEnvironmentVariable("USAGE_WIDGET_CODEX_TEST_SCENARIO",scenario);
            CodexService.LoginTimeoutMs = 500;
            using (var service = new CodexService()) {
                var error = await Fails(service.LoginAsync(exe,url => {},CancellationToken.None));
                Check(error is TimeoutException,"Uncompleted or unrelated login times out: " + scenario);
            }
        }
        var attempt = new CodexLoginAttempt();
        attempt.Bind(Id);
        attempt.Notify(new Dictionary<string,object> { { "loginId",Id }, { "success","true" } });
        Check(!attempt.Completion.IsCompleted,"Malformed success value ignored");
        attempt.Notify(new Dictionary<string,object> { { "loginId",Id }, { "success",true } });
        Check(await attempt.Completion,"Valid matching result accepted after malformed notification");

        // WSL uses a distinct child for each exact distribution, even when CLI paths match.
        CodexService.LoginTimeoutMs = 180000;
        Environment.SetEnvironmentVariable("USAGE_WIDGET_CODEX_TEST_SCENARIO","success");
        WslRuntime.RunnerOverride = (info,input,ms,ct) => Task.FromResult(new WslCommandResult { Output = info.Arguments == "--list --quiet"
            ? "Ubuntu\r\nDebian\r\n" : "NEPHYLO_WSL_V1\0/home/user\0/usr/bin/codex\0\0" });
        int wslStarted = 0;
        WslRuntime.CodexStartInfoOverride = distro => {
            wslStarted++;
            return new ProcessStartInfo(exe,"app-server " + WslRuntime.QuoteArgument(distro)) {
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true
            };
        };
        using (var service = new CodexService()) {
            Check((await service.ReadAsync(exe)).Rows[0].RemainingPercent == 37,"Windows transport baseline");
            Check((await service.ReadAsync("invalid Windows path ignored for WSL","Ubuntu")).Rows[0].RemainingPercent == 88,"WSL selection executes that distro, independent of Windows path");
            Check((await service.ReadAsync("","Ubuntu")).Rows[0].RemainingPercent == 88 && wslStarted == 1,"Same exact distro reuses only its own process");
            var invalidDistro = await Fails(service.ReadAsync("","ubuntu"));
            Check(invalidDistro is ConnectionException && wslStarted == 1,"Unlisted distribution cannot fall back to another source");
            Check((await service.ReadAsync("","Ubuntu")).Rows[0].RemainingPercent == 88 && wslStarted == 1,"Failed candidate probe preserves the current working process");
            Check((await service.ReadAsync("","Debian")).Rows[0].RemainingPercent == 76 && wslStarted == 2,"Changing distro cannot reuse another account's app-server");
            Check((await service.ReadAsync(exe)).Rows[0].RemainingPercent == 37,"Returning to Windows cannot reuse a WSL account");
            Check((await service.LoginAsync("","Ubuntu",url=>{},CancellationToken.None)).Rows[0].RemainingPercent == 88,"Browser login runs in selected WSL account");
        }
        WslRuntime.RunnerOverride = async (info,input,ms,ct) => { await Task.Delay(10000,ct); return new WslCommandResult(); };
        using (var service = new CodexService()) using (var cancel = new CancellationTokenSource(100)) {
            int oldStarted = wslStarted;
            var canceledProbe = await Fails(service.LoginAsync("","Ubuntu",url=>{},cancel.Token));
            Check(canceledProbe is OperationCanceledException && oldStarted == wslStarted,"Cancel while probing WSL never launches a late login process");
        }
        WslRuntime.RunnerOverride = null; WslRuntime.CodexStartInfoOverride = null;
    }
    public static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "app-server") return Server(args.Length > 1 ? args[1] : "");
        try { Test().GetAwaiter().GetResult(); Console.WriteLine("Codex login tests passed: " + assertions + " (fake app-server; no accounts, network or browser)."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Environment.SetEnvironmentVariable("USAGE_WIDGET_CODEX_TEST_SCENARIO",null); CodexService.LoginTimeoutMs = 180000; WslRuntime.RunnerOverride = null; WslRuntime.CodexStartInfoOverride = null; }
    }
}
