using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using UsageWidget;

internal static class ExtraStorageTests
{
    private static int assertions;
    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }

    public static int Main()
    {
        string testParent = Path.GetFullPath(Path.GetDirectoryName(typeof(ExtraStorageTests).Assembly.Location)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string isolated = Path.Combine(testParent, "extra-storage-test-data-" + Guid.NewGuid().ToString("N"));
        SettingsStore.DataDirectoryOverride = isolated;
        try
        {
            ExtraSettings fresh = ExtraSettingsStore.Load();
            Check(fresh.ShowOpenAi && fresh.ShowClaude && fresh.ShowGemini && fresh.ShowCursor, "Missing settings show all services");
            Check(!fresh.OpenAiMonthlyBudgetUsd.HasValue && fresh.ClaudeManual != null && fresh.GeminiManual != null && fresh.CursorManual != null, "Optional data initialized without fabricated numbers");
            Check(!fresh.ClaudeManual.ObservedAt.HasValue && !fresh.GeminiManual.RemainingPercent.HasValue, "No fabricated manual timestamp or value");
            Check(ExtraSettingsStore.LoadOpenAiKey() == String.Empty, "Missing secret is empty");
            Check(!Directory.Exists(isolated), "Reads do not create storage");

            Directory.CreateDirectory(isolated);
            string path = Path.Combine(isolated, "extra-settings.json");
            File.WriteAllText(path, "{}");
            fresh = ExtraSettingsStore.Load();
            Check(fresh.ShowOpenAi && fresh.ShowClaude && fresh.ShowGemini && fresh.ShowCursor && fresh.GeminiManual != null, "Empty legacy document receives defaults");
            File.WriteAllText(path, "{\"ShowClaude\":false,\"UnknownFutureField\":123}");
            fresh = ExtraSettingsStore.Load();
            Check(!fresh.ShowClaude && fresh.ShowOpenAi && fresh.ShowGemini, "Known partial settings preserved with defaults");

            DateTimeOffset observed = new DateTimeOffset(2026, 10, 10, 9, 15, 30, TimeSpan.FromHours(9)).AddTicks(1234567);
            DateTimeOffset reset = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.FromHours(-7));
            ExtraSettings custom = new ExtraSettings
            {
                ShowOpenAi = false, ShowClaude = false, ShowGemini = true, ShowCursor = false,
                OpenAiMonthlyBudgetUsd = 25.75,
                ClaudeManual = new ManualUsageRecord { Label = "  주간 전체 모델  ", RemainingPercent = 0, ObservedAt = observed, ResetsAt = reset },
                GeminiManual = new ManualUsageRecord { Label = "Flash 요청 / 일일", RemainingPercent = 83.25, ObservedAt = observed.AddDays(-1) },
                CursorManual = new ManualUsageRecord { Label = "월간 사용량", RemainingPercent = 100 }
            };
            ExtraSettingsStore.Save(custom);
            ExtraSettings loaded = ExtraSettingsStore.Load();
            Check(!loaded.ShowOpenAi && !loaded.ShowClaude && loaded.ShowGemini && !loaded.ShowCursor, "Visibility flags round trip");
            Check(loaded.OpenAiMonthlyBudgetUsd == 25.75, "Budget fractional amount round trip");
            Check(loaded.ClaudeManual.RemainingPercent == 0 && loaded.CursorManual.RemainingPercent == 100 && loaded.GeminiManual.RemainingPercent == 83.25, "Percent boundaries and fractional values round trip");
            Check(loaded.ClaudeManual.Label == "주간 전체 모델" && loaded.GeminiManual.Label == "Flash 요청 / 일일", "Unicode labels preserved and trimmed");
            Check(loaded.ClaudeManual.ObservedAt.Value.EqualsExact(observed) && loaded.ClaudeManual.ResetsAt.Value.EqualsExact(reset), "ISO timestamps retain offsets and fractional precision");
            Check(loaded.GeminiManual.ObservedAt.Value.EqualsExact(observed.AddDays(-1)) && !loaded.CursorManual.ObservedAt.HasValue, "Independent manual timestamps preserved");
            string json = File.ReadAllText(path);
            Check(json.IndexOf(observed.ToString("o"), StringComparison.Ordinal) >= 0 && json.IndexOf("/Date(", StringComparison.Ordinal) < 0, "Dates stored as ISO8601 strings");
            ExtraSettingsStore.Save(loaded);
            Check(ExtraSettingsStore.Load().GeminiManual.ObservedAt.Value.EqualsExact(observed.AddDays(-1)), "Saving unchanged values never advances manual timestamp");
            Check(custom.ClaudeManual.Label == "  주간 전체 모델  ", "Save does not mutate caller data");

            ExtraSettingsStore.Save(new ExtraSettings { OpenAiMonthlyBudgetUsd = double.NaN, ClaudeManual = new ManualUsageRecord { Label = new String('가', 100), RemainingPercent = double.PositiveInfinity }, GeminiManual = new ManualUsageRecord { RemainingPercent = -1 }, CursorManual = new ManualUsageRecord { RemainingPercent = 101 } });
            loaded = ExtraSettingsStore.Load();
            Check(!loaded.OpenAiMonthlyBudgetUsd.HasValue && !loaded.ClaudeManual.RemainingPercent.HasValue && !loaded.GeminiManual.RemainingPercent.HasValue && !loaded.CursorManual.RemainingPercent.HasValue, "Invalid numbers normalized to unknown");
            Check(loaded.ClaudeManual.Label.Length == 80, "Label length bound");
            json = File.ReadAllText(path);
            Check(json.IndexOf("NaN", StringComparison.Ordinal) < 0 && json.IndexOf("Infinity", StringComparison.Ordinal) < 0, "No invalid JSON numeric tokens");
            new JavaScriptSerializer().DeserializeObject(json);
            ExtraSettingsStore.Save(new ExtraSettings { OpenAiMonthlyBudgetUsd = -5, ClaudeManual = null, GeminiManual = new ManualUsageRecord { Label = new String('x', 79) + "\U0001F600" } });
            loaded = ExtraSettingsStore.Load();
            Check(!loaded.OpenAiMonthlyBudgetUsd.HasValue && loaded.ClaudeManual != null && loaded.ClaudeManual.Label == "", "Negative budget and null records recover");
            Check(loaded.GeminiManual.Label.Length == 79, "Truncation does not split surrogate pair");
            ExtraSettingsStore.Save(new ExtraSettings { OpenAiMonthlyBudgetUsd = 0 });
            Check(ExtraSettingsStore.Load().OpenAiMonthlyBudgetUsd == 0, "Explicit zero budget preserved");

            File.WriteAllText(path, "{\"ShowOpenAi\":\"false\",\"ShowClaude\":false,\"OpenAiMonthlyBudgetUsd\":\"99\",\"ClaudeManual\":{\"Label\":3,\"RemainingPercent\":\"42\",\"ObservedAt\":\"yesterday\",\"ResetsAt\":\"2026-10-11T00:00:00Z\"},\"GeminiManual\":false,\"CursorManual\":{\"RemainingPercent\":true,\"ObservedAt\":\"2026-10-10T12:00:00\"}}");
            loaded = ExtraSettingsStore.Load();
            Check(loaded.ShowOpenAi && !loaded.ShowClaude && !loaded.OpenAiMonthlyBudgetUsd.HasValue, "Invalid field types do not coerce");
            Check(loaded.ClaudeManual.Label == "" && !loaded.ClaudeManual.RemainingPercent.HasValue && !loaded.ClaudeManual.ObservedAt.HasValue, "Invalid manual field types become unknown");
            Check(loaded.ClaudeManual.ResetsAt.Value.Offset == TimeSpan.Zero && loaded.ClaudeManual.ResetsAt.Value.Year == 2026, "UTC ISO timestamp accepted");
            Check(!loaded.CursorManual.ObservedAt.HasValue && !loaded.CursorManual.RemainingPercent.HasValue && loaded.GeminiManual != null, "Ambiguous timezone-less dates rejected and wrong record type recovers");
            File.WriteAllText(path, "{not json");
            Check(ExtraSettingsStore.Load().ShowOpenAi, "Corrupt JSON defaults");
            File.WriteAllText(path, "null");
            Check(ExtraSettingsStore.Load().CursorManual != null, "Null JSON defaults");
            File.WriteAllText(path, new String('x', 65537));
            Check(ExtraSettingsStore.Load().ShowGemini, "Oversized settings defaults");

            const string testKey = "synthetic-openai-admin-test-key-never-a-real-credential";
            SettingsStore.SaveApiKey("synthetic-independent-elevenlabs-test-key");
            ExtraSettingsStore.SaveOpenAiKey("  " + testKey + "  ");
            Check(ExtraSettingsStore.LoadOpenAiKey() == testKey, "DPAPI key round trip and trimming");
            byte[] encrypted = File.ReadAllBytes(Path.Combine(isolated, "openai-admin-key.dat"));
            Check(Encoding.UTF8.GetString(encrypted).IndexOf(testKey, StringComparison.Ordinal) < 0, "Secret encrypted at rest");
            Check(SettingsStore.LoadApiKey() == "synthetic-independent-elevenlabs-test-key", "ElevenLabs key remains independent");
            File.Copy(Path.Combine(isolated, "elevenlabs-key.dat"), Path.Combine(isolated, "openai-admin-key.dat"), true);
            Check(ExtraSettingsStore.LoadOpenAiKey() == "", "Separate entropy rejects other provider ciphertext");
            ExtraSettingsStore.SaveOpenAiKey("synthetic-replacement-key");
            Check(ExtraSettingsStore.LoadOpenAiKey() == "synthetic-replacement-key", "Atomic secret replacement");
            bool oversizedRejected = false;
            try { ExtraSettingsStore.SaveOpenAiKey(new String('x', 8193)); }
            catch (ArgumentException) { oversizedRejected = true; }
            Check(oversizedRejected && ExtraSettingsStore.LoadOpenAiKey() == "synthetic-replacement-key", "Oversized secret rejected without replacing existing key");
            File.WriteAllText(Path.Combine(isolated, "openai-admin-key.dat"), "corrupt secret");
            Check(ExtraSettingsStore.LoadOpenAiKey() == "", "Corrupt secret recovery");
            File.WriteAllBytes(Path.Combine(isolated, "openai-admin-key.dat"), new byte[32769]);
            Check(ExtraSettingsStore.LoadOpenAiKey() == "", "Oversized encrypted secret recovery");
            ExtraSettingsStore.SaveOpenAiKey(null);
            Check(!File.Exists(Path.Combine(isolated, "openai-admin-key.dat")) && ExtraSettingsStore.LoadOpenAiKey() == "", "Empty secret removes file");
            Check(Directory.GetFiles(isolated, "*.tmp").Length == 0, "No atomic-write staging files left");
            Check(!File.Exists(Path.Combine(isolated, "settings.json")), "Core settings never written by extra storage");

            Console.WriteLine("Extra storage tests passed: " + assertions + " assertions. Only isolated synthetic data was used; real settings, credentials and startup registration were untouched.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Extra storage test failed: " + ex.Message);
            return 1;
        }
        finally
        {
            SettingsStore.DataDirectoryOverride = null;
            string fullIsolated = Path.GetFullPath(isolated);
            if (fullIsolated.StartsWith(testParent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(fullIsolated).StartsWith("extra-storage-test-data-", StringComparison.Ordinal) && Directory.Exists(fullIsolated)) Directory.Delete(fullIsolated, true);
        }
    }
}
