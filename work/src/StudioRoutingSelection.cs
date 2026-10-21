using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace UsageWidget
{
    internal sealed class StudioRoutePlan
    {
        internal StudioRouteCandidate Candidate;
        internal string Workload,AllowanceBand;
        internal string Intent,PreferredProvider,SelectionReason;
        internal int PoolCandidates,WorkloadCandidates,SelectedBandCandidates;
    }
    internal static partial class StudioRoutingPolicy
    {
        sealed class ModelFamily
        {
            internal string Name,Version;
            internal bool Preview;
        }
        static ModelFamily Describe(StudioRouteCandidate candidate)
        {
            if(candidate==null||!StudioProviderCatalog.IsValid(candidate.Provider)||StudioQuotaParser.SafeText(candidate.Model,200)==null)return null;
            string id=candidate.Model.ToLowerInvariant();Match match;
            if(candidate.Provider=="codex")match=Regex.Match(id,@"\Agpt-(?<version>[0-9]{1,3}(?:\.[0-9]{1,3}){0,3})-(?<family>luna|sol|terra|astra)\z",RegexOptions.CultureInvariant);
            else if(candidate.Provider=="claude")match=Regex.Match(id,@"\A(?:claude-)?(?<family>haiku|sonnet|opus)(?:-(?<version>[0-9]{1,8}(?:[.-][0-9]{1,8}){0,4}))?\z",RegexOptions.CultureInvariant);
            else if(candidate.Provider=="gemini-cli")match=Regex.Match(id,@"\A(?<family>flash-lite|flash|pro)\z",RegexOptions.CultureInvariant);
            else match=Regex.Match(id,@"\A(?:models/)?gemini-(?<version>[0-9]{1,3}(?:\.[0-9]{1,3}){0,3})-(?<family>flash-lite|flash|pro)(?:-(?:preview|latest)(?:-[0-9-]{1,24})?)?\z",RegexOptions.CultureInvariant);
            if(!match.Success)return null;
            var parts=match.Groups["version"].Value.Split(new[]{'.','-'},StringSplitOptions.RemoveEmptyEntries);
            var key=new List<string>();foreach(var part in parts){int number;if(!Int32.TryParse(part,NumberStyles.None,CultureInfo.InvariantCulture,out number))return null;key.Add(number.ToString("D8",CultureInfo.InvariantCulture));}
            while(key.Count<5)key.Add("00000000");
            return new ModelFamily{Name=match.Groups["family"].Value,Version=String.Join(".",key),Preview=id.Contains("-preview")};
        }
        internal static string AllowanceBand(StudioRouteCandidate candidate)
        {
            if(!candidate.Remaining.HasValue)return "unknown";
            double remaining=candidate.Remaining.Value;
            if(Double.IsNaN(remaining)||Double.IsInfinity(remaining)||remaining<=0||remaining>100)return "blocked";
            return remaining>=20?"healthy":"low";
        }
        static int BandOrder(string band){return band=="healthy"?0:band=="unknown"?1:band=="low"?2:3;}
        static int ProviderOrder(string provider){return provider=="codex"?0:provider=="claude"?1:provider=="gemini-cli"?2:3;}
        internal static string PreferredForIntent(string intent)
        {
            if(StudioRouteRecord.TaskForIntent(intent)==null)throw new StudioRouteFailure("response");
            return intent=="writing"||intent=="text_analysis"||intent=="general_planning"?"claude":"codex";
        }
        static int FamilyOrder(string family){return family=="sol"||family=="flash"?0:1;}
        static bool MatchesProfile(ModelFamily family,string profile)
        {
            if(profile=="quick")return new[]{"luna","haiku","flash","flash-lite"}.Contains(family.Name);
            if(profile=="balanced")return new[]{"sol","terra","sonnet","pro"}.Contains(family.Name);
            if(profile=="deep")return new[]{"astra","opus","pro"}.Contains(family.Name);
            return false;
        }
        internal static string WorkloadForTask(string task)
        {
            if(task=="general")return "quick";
            if(task=="planning")return "deep";
            if(new[]{"writing","coding","debugging","analysis"}.Contains(task))return "balanced";
            throw new StudioRouteFailure("response");
        }
        static bool Eligible(StudioRouteCandidate candidate)
        {
            return candidate!=null&&!candidate.Excluded&&!String.IsNullOrWhiteSpace(candidate.Connection)&&!String.IsNullOrWhiteSpace(candidate.Account)&&candidate.Id==StudioRouteInput.CandidateId(candidate.Provider,candidate.Model)&&Describe(candidate)!=null&&AllowanceBand(candidate)!="blocked";
        }
        // Keep a current representative per provider/family/allowance band.
        // An older healthy model must not disappear behind a newer low/empty one.
        // This never changes the full catalog offered by manual selection.
        internal static List<StudioRouteCandidate> CanonicalCandidates(IEnumerable<StudioRouteCandidate> candidates)
        {
            return (candidates??Enumerable.Empty<StudioRouteCandidate>()).Where(Eligible)
                .GroupBy(c=>c.Provider+"\n"+Describe(c).Name+"\n"+AllowanceBand(c),StringComparer.Ordinal)
                .Select(g=>g.OrderByDescending(c=>Describe(c).Version,StringComparer.Ordinal).ThenBy(c=>Describe(c).Preview).ThenBy(c=>c.Model,StringComparer.Ordinal).First())
                .OrderBy(c=>ProviderOrder(c.Provider)).ThenBy(c=>Describe(c).Name,StringComparer.Ordinal).ThenBy(c=>BandOrder(AllowanceBand(c))).Take(32).ToList();
        }
        internal static StudioRoutePlan Plan(StudioRouteInput input,StudioRouteDecision decision)
        {
            if(input==null||input.Candidates==null||input.Candidates.Count==0||input.Candidates.Count>32||decision==null||decision.Task==null||decision.Files==null)throw new StudioRouteFailure("response");
            bool affinity=NormalizePolicy(input.Policy)==TaskAffinity;
            if(!decision.Task.Certain||decision.Task.Value=="unclear")throw new StudioRouteFailure("review",decision.Task,"task");
            if(!decision.Files.Certain||decision.Files.Value=="unclear")throw new StudioRouteFailure("review",decision.Files,"files");
            if(!new[]{"required","not_required"}.Contains(decision.Files.Value))throw new StudioRouteFailure("response");
            string task=affinity?StudioRouteRecord.TaskForIntent(decision.Task.Value):decision.Task.Value;
            string preferred=affinity?PreferredForIntent(decision.Task.Value):null;
            string workload=WorkloadForTask(task);
            var pool=CanonicalCandidates(input.Candidates);
            if(pool.Count==0)throw new StudioRouteFailure("candidate");
            var scoped=pool.Where(c=>decision.Files.Value!="required"||c.Provider=="codex").ToList();
            if(scoped.Count==0)throw new StudioRouteFailure("scope");
            var profiled=scoped.Where(c=>MatchesProfile(Describe(c),workload)).ToList();
            if(profiled.Count==0)throw new StudioRouteFailure("profile");
            int band=profiled.Min(c=>BandOrder(AllowanceBand(c)));
            var finalists=profiled.Where(c=>BandOrder(AllowanceBand(c))==band).ToList();
            // Percentages from different services are not comparable work units.
            // Inside one band v3 applies its disclosed intent preference, then the
            // stable order. It is not a price/quality benchmark or new confidence.
            var picked=finalists.OrderBy(c=>affinity&&c.Provider==preferred?-1:ProviderOrder(c.Provider)).ThenBy(c=>FamilyOrder(Describe(c).Name))
                .ThenByDescending(c=>Describe(c).Version,StringComparer.Ordinal).ThenBy(c=>Describe(c).Preview).ThenBy(c=>c.Model,StringComparer.Ordinal).First();
            string reason=!affinity?null:picked.Provider==preferred?"task_preference":decision.Files.Value=="required"&&preferred!="codex"?"file_access_required":profiled.Any(c=>c.Provider==preferred)?"allowance_precedence":"preferred_unavailable";
            return new StudioRoutePlan{Candidate=picked,Workload=workload,AllowanceBand=AllowanceBand(picked),PoolCandidates=pool.Count,WorkloadCandidates=profiled.Count,SelectedBandCandidates=finalists.Count,Intent=affinity?decision.Task.Value:null,PreferredProvider=preferred,SelectionReason=reason};
        }
    }
}
