using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace UsageWidget
{
    public sealed class ManualUsageRecord
    {
        public string Label = String.Empty;
        public double? RemainingPercent;
        public DateTimeOffset? ResetsAt;
        public DateTimeOffset? ObservedAt;
    }

    public sealed class ExtraSettings
    {
        public bool ShowOpenAi = true;
        public bool ShowClaude = true;
        public bool ShowGemini = true;
        public bool ShowCursor = true;
        public double? OpenAiMonthlyBudgetUsd;
        public ManualUsageRecord ClaudeManual = new ManualUsageRecord();
        public ManualUsageRecord GeminiManual = new ManualUsageRecord();
        public ManualUsageRecord CursorManual = new ManualUsageRecord();
    }

    public static class ExtraSettingsStore
    {
        private const int MaxSettingsBytes = 65536;
        private static readonly object Gate = new object();
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("UsageWidget.OpenAi.AdminKey.v1");
        private static readonly string[] DateFormats = new string[]
        {
            "o", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss'Z'",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"
        };

        public static ExtraSettings Load()
        {
            lock (Gate)
            {
                string path = Path.Combine(SettingsStore.DataDirectory, "extra-settings.json");
                try
                {
                    if (!File.Exists(path) || new FileInfo(path).Length > MaxSettingsBytes)
                        return new ExtraSettings();
                    Dictionary<string, object> values = Serializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                    if (values == null) return new ExtraSettings();
                    return new ExtraSettings
                    {
                        ShowOpenAi = ReadBoolean(values, "ShowOpenAi"),
                        ShowClaude = ReadBoolean(values, "ShowClaude"),
                        ShowGemini = ReadBoolean(values, "ShowGemini"),
                        ShowCursor = ReadBoolean(values, "ShowCursor"),
                        OpenAiMonthlyBudgetUsd = ValidNumber(ReadNumber(values, "OpenAiMonthlyBudgetUsd"), false),
                        ClaudeManual = ReadManual(values, "ClaudeManual"),
                        GeminiManual = ReadManual(values, "GeminiManual"),
                        CursorManual = ReadManual(values, "CursorManual")
                    };
                }
                catch (IOException) { return new ExtraSettings(); }
                catch (UnauthorizedAccessException) { return new ExtraSettings(); }
                catch (ArgumentException) { return new ExtraSettings(); }
                catch (InvalidOperationException) { return new ExtraSettings(); }
            }
        }

        public static void Save(ExtraSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            Dictionary<string, object> values = new Dictionary<string, object>();
            values["ShowOpenAi"] = settings.ShowOpenAi;
            values["ShowClaude"] = settings.ShowClaude;
            values["ShowGemini"] = settings.ShowGemini;
            values["ShowCursor"] = settings.ShowCursor;
            values["OpenAiMonthlyBudgetUsd"] = ValidNumber(settings.OpenAiMonthlyBudgetUsd, false);
            values["ClaudeManual"] = WriteManual(settings.ClaudeManual);
            values["GeminiManual"] = WriteManual(settings.GeminiManual);
            values["CursorManual"] = WriteManual(settings.CursorManual);
            byte[] bytes = new UTF8Encoding(false).GetBytes(Serializer().Serialize(values));
            if (bytes.Length > MaxSettingsBytes) throw new ArgumentException("Settings are too large.", "settings");
            lock (Gate)
            {
                WriteAtomically(Path.Combine(SettingsStore.DataDirectory, "extra-settings.json"), bytes);
            }
        }

        public static string LoadOpenAiKey()
        {
            lock (Gate)
            {
                string path = Path.Combine(SettingsStore.DataDirectory, "openai-admin-key.dat");
                byte[] encrypted = null;
                byte[] clear = null;
                try
                {
                    if (!File.Exists(path) || new FileInfo(path).Length > 32768) return String.Empty;
                    encrypted = File.ReadAllBytes(path);
                    clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(clear);
                }
                catch (IOException) { return String.Empty; }
                catch (UnauthorizedAccessException) { return String.Empty; }
                catch (CryptographicException) { return String.Empty; }
                finally
                {
                    if (encrypted != null) Array.Clear(encrypted, 0, encrypted.Length);
                    if (clear != null) Array.Clear(clear, 0, clear.Length);
                }
            }
        }

        public static void SaveOpenAiKey(string key)
        {
            string normalized = (key ?? String.Empty).Trim();
            if (normalized.Length > 8192) throw new ArgumentException("The API key is too long.", "key");
            lock (Gate)
            {
                string path = Path.Combine(SettingsStore.DataDirectory, "openai-admin-key.dat");
                if (normalized.Length == 0)
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                byte[] clear = Encoding.UTF8.GetBytes(normalized);
                byte[] encrypted = null;
                try
                {
                    encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
                    WriteAtomically(path, encrypted);
                }
                finally
                {
                    Array.Clear(clear, 0, clear.Length);
                    if (encrypted != null) Array.Clear(encrypted, 0, encrypted.Length);
                }
            }
        }

        private static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = MaxSettingsBytes, RecursionLimit = 10 };
        }

        private static double? ValidNumber(double? value, bool percentage)
        {
            return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value) && value.Value >= 0 && (!percentage || value.Value <= 100) ? value : null;
        }

        private static string Label(string value)
        {
            string result = (value ?? String.Empty).Trim();
            if (result.Length <= 80) return result;
            // Do not split a UTF-16 surrogate pair when shortening a label.
            int length = char.IsHighSurrogate(result[79]) ? 79 : 80;
            return result.Substring(0, length);
        }

        private static bool ReadBoolean(Dictionary<string, object> values, string name)
        {
            object value;
            return values.TryGetValue(name, out value) && value is bool ? (bool)value : true;
        }

        private static double? ReadNumber(Dictionary<string, object> values, string name)
        {
            object value;
            if (!values.TryGetValue(name, out value) || !(value is int || value is long || value is decimal || value is double || value is float)) return null;
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        private static DateTimeOffset? ReadDate(Dictionary<string, object> values, string name)
        {
            object value;
            DateTimeOffset date;
            if (values.TryGetValue(name, out value) && value is string && DateTimeOffset.TryParseExact((string)value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date)) return date;
            return null;
        }

        private static ManualUsageRecord ReadManual(Dictionary<string, object> values, string name)
        {
            object value;
            Dictionary<string, object> record;
            if (!values.TryGetValue(name, out value) || (record = value as Dictionary<string, object>) == null) return new ManualUsageRecord();
            object label;
            return new ManualUsageRecord
            {
                Label = Label(record.TryGetValue("Label", out label) ? label as string : null),
                RemainingPercent = ValidNumber(ReadNumber(record, "RemainingPercent"), true),
                ResetsAt = ReadDate(record, "ResetsAt"),
                ObservedAt = ReadDate(record, "ObservedAt")
            };
        }

        private static Dictionary<string, object> WriteManual(ManualUsageRecord manual)
        {
            manual = manual ?? new ManualUsageRecord();
            Dictionary<string, object> values = new Dictionary<string, object>();
            values["Label"] = Label(manual.Label);
            values["RemainingPercent"] = ValidNumber(manual.RemainingPercent, true);
            values["ResetsAt"] = manual.ResetsAt.HasValue ? manual.ResetsAt.Value.ToString("o", CultureInfo.InvariantCulture) : null;
            values["ObservedAt"] = manual.ObservedAt.HasValue ? manual.ObservedAt.Value.ToString("o", CultureInfo.InvariantCulture) : null;
            return values;
        }

        private static void WriteAtomically(string path, byte[] bytes)
        {
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
