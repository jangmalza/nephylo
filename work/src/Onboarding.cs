using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace UsageWidget
{
    // This file deliberately holds no account, credential, installation path or usage data.
    internal sealed class OnboardingState
    {
        public int Version = 1;
        public bool Shown;
        public bool Completed;
        public string ServiceId = "codex";
    }

    internal static class OnboardingStore
    {
        private const string FileName = "onboarding.json";
        internal static readonly string[] ServiceIds = { "codex", "eleven", "openai", "claude", "gemini", "cursor", "higgsfield", "higgsfield-api" };
        private static readonly string[] ExistingFiles = { "settings.json", "extra-settings.json", "plan-settings.json", "membership-session.dat", "elevenlabs-key.dat", "openai-admin-key.dat", "higgsfield-settings.json", "usage-journal.json", "usage-history.json" };

        internal static bool ShouldOffer(string directory)
        {
            // Any old profile, including an unreadable/empty file, belongs to an existing
            // user. Never confuse a recovery or an upgrade with a brand-new installation.
            try { EnsureSafePath(directory); return !File.Exists(Path.Combine(directory,FileName)) && !ExistingFiles.Any(file => File.Exists(Path.Combine(directory,file))); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        internal static OnboardingState Load(string directory)
        {
            var state = new OnboardingState();
            string path = Path.Combine(directory,FileName);
            if (!File.Exists(path)) return state;
            state.Shown = true;
            try {
                EnsureSafePath(directory);
                string json;
                using (var stream = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                    if (stream.Length > 4096) return state;
                    using (var reader = new StreamReader(stream,Encoding.UTF8)) json = reader.ReadToEnd();
                }
                var values = new JavaScriptSerializer { MaxJsonLength = 4096, RecursionLimit = 4 }.Deserialize<Dictionary<string,object>>(json);
                if (values == null) return state;
                object value;
                if (values.TryGetValue("Completed",out value) && value is bool) state.Completed = (bool)value;
                if (values.TryGetValue("ServiceId",out value) && value is string && ServiceIds.Contains((string)value)) state.ServiceId = (string)value;
            } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { } catch (InvalidOperationException) { }
            return state;
        }

        internal static void Save(string directory, OnboardingState state)
        {
            if (state == null) throw new ArgumentNullException("state");
            EnsureSafePath(directory);
            var values = new Dictionary<string,object> { { "Version",1 }, { "Shown",true }, { "Completed",state.Completed }, { "ServiceId",ServiceIds.Contains(state.ServiceId) ? state.ServiceId : "codex" } };
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory,FileName), temporary = Path.Combine(directory,".onboarding-" + Guid.NewGuid().ToString("N") + ".tmp");
            byte[] bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(values));
            try {
                using (var stream = new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { stream.Write(bytes,0,bytes.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary,path,null); else File.Move(temporary,path);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static void EnsureSafePath(string directory)
        {
            var current = new DirectoryInfo(Path.GetFullPath(directory));
            while (current != null) {
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The guide storage path is redirected.");
                current = current.Parent;
            }
            string path = Path.Combine(directory,FileName);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("The guide file is redirected.");
        }

        internal static bool HasVerifiedUsage(ProviderView view, bool active, bool saved, DateTimeOffset now)
        {
            if (!active || !saved || view == null || view.Busy || !view.LastSuccess.HasValue || view.LastSuccess.Value > now.AddMinutes(1)
                || !String.IsNullOrEmpty(view.Error) || view.Issue != null || view.Snapshot == null || view.Snapshot.Rows == null) return false;
            if (view.ReceiptState == ProviderReceiptState.Waiting || view.ReceiptState == ProviderReceiptState.QuotaUnavailable) return false;
            bool manual = UsageFreshness.Source(view) == "manual";
            if (!manual && UsageFreshness.IsObservationStale(view,now)) return false;
            return view.Snapshot.Rows.Any(row => DashboardAssessment.IsUsableRow(view,row,now) || IsConfirmedCost(view,row,now));
        }

        private static bool IsConfirmedCost(ProviderView view, UsageRow row, DateTimeOffset now)
        {
            // OpenAI intentionally has no Remaining value when the user has not set a
            // budget. Accept its bounded money representation, including refunds, but
            // never arbitrary placeholder strings or unsupported quota messages.
            if (view.Id != "openai" || row == null || (row.Label != "이번 달 비용" && row.Label != "이번 달 순비용")
                || !row.ResetsAt.HasValue || row.ResetsAt.Value <= now || String.IsNullOrEmpty(row.DisplayValue)) return false;
            string text = row.DisplayValue;
            if (text == "<$0.01" || text == ">-$0.01") return true;
            if (text.StartsWith("-$",StringComparison.Ordinal)) text = text.Substring(2);
            else if (text.StartsWith("$",StringComparison.Ordinal)) text = text.Substring(1);
            else return false;
            decimal amount;
            return Decimal.TryParse(text,NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands,CultureInfo.InvariantCulture,out amount) && amount >= 0;
        }
    }
}
