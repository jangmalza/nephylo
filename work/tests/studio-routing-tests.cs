using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UsageWidget;

internal static class StudioRoutingTests
{
    const string Key="synthetic-key-for-routing-tests-only";
    static int count;static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    static readonly BindingFlags Flags=BindingFlags.NonPublic|BindingFlags.Instance;
    static void Check(bool value,string message){count++;if(!value)throw new Exception(message);}
    static bool Fails(Action action){try{action();return false;}catch{return true;}}
    static void Pump(Task task){var frame=new DispatcherFrame();var start=DateTime.UtcNow;var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(5)};timer.Tick+=(s,e)=>{if(task.IsCompleted||DateTime.UtcNow-start>TimeSpan.FromSeconds(20))frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);timer.Stop();if(!task.IsCompleted)throw new TimeoutException();task.GetAwaiter().GetResult();}
    static void Drain(){Pump(Dispatcher.CurrentDispatcher.InvokeAsync(()=>{},DispatcherPriority.Background).Task);}
    static StudioRouteCandidate Candidate(string provider,string model){return new StudioRouteCandidate{Provider=provider,Model=model,Id=StudioRouteInput.CandidateId(provider,model),Label=model,Connection="fixture-connection",Account="fixture-account"};}
    static StudioRouteInput Input(){var code=Candidate("codex","gpt-6-sol");code.Remaining=5;var chat=Candidate("claude","sonnet");chat.Remaining=80;return new StudioRouteInput{Prompt="Explain a small function",Candidates=new List<StudioRouteCandidate>{code,chat}};}
    static object Answer(StudioRouteQuestion question,string choice,bool openai,double probability=.96)
    {
        var probabilities=question.Options.Keys.ToDictionary(id=>id,id=>id==choice?probability:(1-probability)/(question.Options.Count-1));
        if(openai)return new{name=question.Name,type="choice",choice=choice,confidence=.95,probabilities=probabilities.Select(pair=>new{value=pair.Key,probability=pair.Value}).ToArray()};
        return new{type="choice",choice=choice,confidence=.95,probabilities=probabilities};
    }
    static string Response(StudioRouteInput input,string engine,string task="analysis",string files="not_required",double probability=.96)
    {
        bool openai=engine=="openai-decisions";var picks=new Dictionary<string,string>{{"task",task},{"files",files}};
        var answers=input.Questions().ToDictionary(q=>q.Name,q=>Answer(q,picks[q.Name],openai,probability));
        return Json.Serialize(new{answers=openai?(object)answers.Values.ToArray():answers,usage=new{input_tokens=123}});
    }
    sealed class HttpFixture:HttpMessageHandler
    {
        internal int Calls;internal HttpStatusCode Status=HttpStatusCode.OK;internal string Body,RequestBody,Authorization,ApiKey;internal Uri Uri;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        {Calls++;Uri=request.RequestUri;RequestBody=await request.Content.ReadAsStringAsync();Authorization=request.Headers.Authorization==null?null:request.Headers.Authorization.ToString();ApiKey=request.Headers.Contains("api-key")?request.Headers.GetValues("api-key").Single():null;return new HttpResponseMessage(Status){Content=new StringContent(Body??"{}",Encoding.UTF8,"application/json")};}
    }
    sealed class CancellationHttpFixture:HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation){Calls++;await Task.Delay(Timeout.Infinite,cancellation);return new HttpResponseMessage(HttpStatusCode.OK);}
    }
    static void Protocol(string root)
    {
        var input=Input();
        using(var transport=StudioDecisionClient.CreateHandler()){
            Check(transport.SslProtocols==System.Security.Authentication.SslProtocols.Tls12&&transport.CheckCertificateRevocationList,"decision HTTPS does not depend on the legacy process-wide TLS defaults");
            Check(!transport.AllowAutoRedirect&&!transport.UseCookies&&!transport.UseProxy&&!transport.UseDefaultCredentials,"decision transport preserves credential and redirect boundaries");
        }
        foreach(var engine in StudioRoutingPolicy.Engines){
            var settings=new StudioRoutingSettings{Engine=engine,AzureEndpoint="https://fixture.services.ai.azure.com/",AzureDeployment="decision-fixture"};
            var transport=new HttpFixture{Body=Response(input,engine)};
            using(var client=new StudioDecisionClient(transport)){
                var decision=client.DecideAsync(settings,Key,input,CancellationToken.None).GetAwaiter().GetResult();
                Check(transport.Calls==1,"exactly one decision request per explicit send");
                Check(StudioRoutingPolicy.Select(input,decision).Provider=="claude","each engine selects the same authorized candidate");
                Check(decision.InputTokens==123&&decision.Task.Confidence==.95,"measured metadata parsed without cost estimates");
                Check(!transport.RequestBody.Contains("fixture-account")&&!transport.RequestBody.Contains("fixture-connection")&&!transport.RequestBody.Contains(Key)&&!transport.RequestBody.Contains("gpt-6-sol")&&!transport.RequestBody.Contains("sonnet")&&!transport.RequestBody.Contains("remaining_percent"),"classification body excludes credentials, model catalog and allowance; those decisions stay local");
                var body=Json.Deserialize<Dictionary<string,object>>(transport.RequestBody);
                if(engine=="openai-decisions"){
                    Check(transport.Uri.ToString()=="https://api.openai.com/v1/decisions"&&transport.Authorization=="Bearer "+Key&&transport.ApiKey==null,"OpenAI dedicated decision endpoint and API authorization");
                    Check(StudioCodexClient.Items(body,"questions").Count()==2&&StudioCodexClient.Str(body,"model")=="gpt-6-luna","OpenAI uses the two independent guarded questions and the decision model schema");
                }else{
                    Check(StudioCodexClient.Obj(body,"questions").Count==2&&body.ContainsKey("state"),"System One state and independent question-map schema");
                    if(engine=="jev")Check(transport.Uri.ToString()=="https://api.typesafe.ai/v1/systemone"&&transport.Authorization=="Bearer "+Key,"Jev official direct API endpoint");
                    else Check(transport.Uri.ToString()=="https://fixture.services.ai.azure.com/providers/microsoft/v1/systemone"&&transport.ApiKey==Key&&transport.Authorization==null&&StudioCodexClient.Str(body,"model")=="decision-fixture","Azure resource route, deployment name and api-key header");
                }
            }
            foreach(var status in new[]{HttpStatusCode.Unauthorized,HttpStatusCode.Forbidden,(HttpStatusCode)429,HttpStatusCode.Redirect,HttpStatusCode.InternalServerError}){
                var failed=new HttpFixture{Status=status,Body="PRIVATE_RESPONSE_TOKEN"};
                using(var client=new StudioDecisionClient(failed)){
                    string message="";try{client.DecideAsync(settings,Key,input,CancellationToken.None).GetAwaiter().GetResult();}catch(Exception error){message=error.Message;}
                    Check(failed.Calls==1&&message.Length>0&&!message.Contains("PRIVATE_RESPONSE_TOKEN"),"API failures are sanitized and never auto-retried");
                }
            }
            var valid=Response(input,engine);
            foreach(var malformed in new[]{"{}","[]","null",valid.Replace("analysis","unlisted-task"),valid.Replace("\"confidence\":0.95","\"confidence\":\"0.95\""),valid.Replace("\"type\":\"choice\"","\"type\":\"refusal\""),valid.Replace("0.96","1.96")})
                Check(Fails(()=>StudioDecisionClient.Parse(settings,input,malformed)),"malformed, refused, unknown or nonnumeric decisions fail closed");
            var low=StudioDecisionClient.Parse(settings,input,Response(input,engine,"analysis","not_required",.55));
            Check(Fails(()=>StudioRoutingPolicy.Select(input,low)),"uncertain choices require manual review");
            var mixed=new StudioRouteInput{Prompt="17+25",Candidates=new List<StudioRouteCandidate>{Candidate("codex","gpt-6-luna"),Candidate("codex","gpt-5.6-luna"),Candidate("claude","haiku")}};
            var split=StudioDecisionClient.Parse(settings,mixed,Response(mixed,engine,"general","not_required",.50));
            var certainParts=StudioDecisionClient.Parse(settings,mixed,Response(mixed,engine,"general"));split.Files=certainParts.Files;
            Check(Fails(()=>StudioRoutingPolicy.Select(mixed,split)),"generation preferences never override split probabilities or lower automatic execution thresholds");
            StudioRouteFailure splitReason=null;try{StudioRoutingPolicy.Select(mixed,split);}catch(StudioRouteFailure error){splitReason=error;}
            Check(splitReason!=null&&splitReason.Subject=="task"&&splitReason.Probability==.50,"task uncertainty retains a safe numeric reason; deterministic model choice cannot manufacture confidence");
            var outside=StudioDecisionClient.Parse(settings,input,Response(input,engine,"analysis","required"));
            Check(StudioRoutingPolicy.Select(input,outside).Provider=="codex","required project-file tasks exclude chat-only models even when they have more allowance");
            var chatOnly=new StudioRouteInput{Prompt=input.Prompt,Candidates=new List<StudioRouteCandidate>{input.Candidates[1]}};
            Check(Fails(()=>StudioRoutingPolicy.Select(chatOnly,outside)),"chat-only pool cannot receive a project-file task");
            var missingConfidence=Response(input,engine).Replace("\"confidence\":0.95","\"confidence\":null");
            if(engine=="ms-decision")Check(StudioRoutingPolicy.Select(input,StudioDecisionClient.Parse(settings,input,missingConfidence)).Provider=="claude","Microsoft missing confidence uses explicit choice-probability policy");
            else Check(Fails(()=>StudioDecisionClient.Parse(settings,input,missingConfidence)),"engines promising confidence cannot silently omit it");
        }
        foreach(var endpoint in new[]{"http://fixture.services.ai.azure.com/","https://127.0.0.1/","https://example.com/","https://fixture.services.ai.azure.com.evil.invalid/","https://fixture.services.ai.azure.com:444/","https://key@fixture.services.ai.azure.com/","https://fixture.services.ai.azure.com/path","https://fixture.services.ai.azure.com/?key=secret","https://fixture.services.ai.azure.com/#fragment"})
            Check(Fails(()=>StudioDecisionClient.Endpoint(new StudioRoutingSettings{Engine="ms-decision",AzureEndpoint=endpoint})),"Azure endpoints cannot redirect keys to arbitrary origins or paths");
        var unknown=new StudioRoutingSettings{Engine="invented"};Check(Fails(()=>StudioDecisionClient.Endpoint(unknown)),"unlisted engine rejected");
        var cancelledTransport=new CancellationHttpFixture();using(var cancellation=new CancellationTokenSource())using(var client=new StudioDecisionClient(cancelledTransport)){
            cancellation.CancelAfter(80);bool cancelled=false;try{client.DecideAsync(new StudioRoutingSettings(),Key,input,cancellation.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
            Check(cancelled&&cancelledTransport.Calls==1,"actual HTTP decision cancellation propagates without retry");
        }
    }
    static void QuotasAndPersistence(string root)
    {
        var identity=StudioAccountIdentity.FromResponse(new Dictionary<string,object>{{"account",new Dictionary<string,object>{{"type","chatgpt"},{"email","routing@example.invalid"}}}});
        Func<DateTime,double,string,StudioQuotaSnapshot> quota=(observed,remaining,scope)=>new StudioQuotaSnapshot(observed,"quota-connection",identity.Fingerprint,null,new[]{new StudioQuotaBucket("codex","Codex",scope,null,null,new StudioQuotaWindow("primary",remaining,300,DateTime.UtcNow.AddHours(1)),null)},true,true);
        var candidate=Candidate("codex","fixture-code");
        StudioRoutingPolicy.ApplyQuota(candidate,quota(DateTime.UtcNow,0,null),identity,"quota-connection",DateTime.UtcNow);Check(candidate.Excluded,"confirmed exhausted account excluded");
        StudioRoutingPolicy.ApplyQuota(candidate,quota(DateTime.UtcNow,7,null),identity,"quota-connection",DateTime.UtcNow);Check(!candidate.Excluded&&candidate.Remaining==7,"low real allowance carried into router options");
        StudioRoutingPolicy.ApplyQuota(candidate,quota(DateTime.UtcNow.AddMinutes(-6),0,null),identity,"quota-connection",DateTime.UtcNow);Check(!candidate.Excluded&&!candidate.Remaining.HasValue,"stale allowance is unknown, never exhausted or unlimited");
        StudioRoutingPolicy.ApplyQuota(candidate,quota(DateTime.UtcNow,0,null),identity,"wrong-connection",DateTime.UtcNow);Check(!candidate.Excluded&&!candidate.Remaining.HasValue,"another connection's allowance is not used");
        StudioRoutingPolicy.ApplyQuota(candidate,quota(DateTime.UtcNow,0,"other-model"),identity,"quota-connection",DateTime.UtcNow);Check(!candidate.Excluded&&!candidate.Remaining.HasValue,"model-specific quota is not applied to another model");
        var now=DateTime.UtcNow;
        var restriction=StudioQuotaParser.Parse(new Dictionary<string,object>{{"ordinaryUsageAllowed",false}},identity,"quota-connection",now);
        Check(restriction.Available&&restriction.Buckets.Count==0&&restriction.State(now,identity,"quota-connection")==StudioQuotaState.Unknown,"a restriction-only response does not invent a confirmed percentage");
        candidate=Candidate("codex","gpt-6-sol");
        StudioRoutingPolicy.ApplyQuota(candidate,restriction,identity,"quota-connection",now);
        Check(candidate.Excluded&&candidate.Exclusion=="usage_restricted"&&!candidate.Remaining.HasValue,"current account-wide restriction excludes the model even without quota buckets");
        var restrictedInput=new StudioRouteInput{Prompt="Explain supplied code",Candidates=new List<StudioRouteCandidate>{candidate,Candidate("claude","sonnet")}};
        Check(StudioRoutingPolicy.Select(restrictedInput,Classified(restrictedInput,"balanced")).Provider=="claude","restricted Codex is not retained as an unknown-allowance routing fallback");
        foreach(var moment in new[]{now.AddMinutes(5),now.AddSeconds(-1)}){
            StudioRoutingPolicy.ApplyQuota(candidate,restriction,identity,"quota-connection",moment);
            Check(!candidate.Excluded&&!candidate.Remaining.HasValue,"expired or future restriction is not used as current entitlement evidence");
        }
        StudioRoutingPolicy.ApplyQuota(candidate,restriction,identity,"another-connection",now);
        Check(!candidate.Excluded&&!candidate.Remaining.HasValue,"restriction-only evidence cannot cross connections");
        var otherIdentity=StudioAccountIdentity.FromResponse(new Dictionary<string,object>{{"account",new Dictionary<string,object>{{"type","chatgpt"},{"email","other-routing@example.invalid"}}}});
        StudioRoutingPolicy.ApplyQuota(candidate,restriction,otherIdentity,"quota-connection",now);
        Check(!candidate.Excluded&&!candidate.Remaining.HasValue,"restriction-only evidence cannot cross accounts");
        var identified=StudioAccountIdentity.FromResponse(new Dictionary<string,object>{{"account",new Dictionary<string,object>{{"type","chatgpt"},{"accountId","workspace-a"},{"email","routing@example.invalid"}}}});
        var foreignSource=StudioQuotaParser.Parse(new Dictionary<string,object>{{"ordinaryUsageAllowed",false},{"accountId","workspace-b"}},identified,"quota-connection",now);
        StudioRoutingPolicy.ApplyQuota(candidate,foreignSource,identified,"quota-connection",now);
        Check(!candidate.Excluded&&!candidate.Remaining.HasValue,"reported source account mismatch cannot impose a restriction");
        var allowed=StudioQuotaParser.Parse(new Dictionary<string,object>{{"ordinaryUsageAllowed",true}},identity,"quota-connection",now);
        StudioRoutingPolicy.ApplyQuota(candidate,allowed,identity,"quota-connection",now);
        Check(!candidate.Excluded&&!candidate.Remaining.HasValue,"allowed without percentages stays unknown rather than becoming unlimited");
        var scopedRestriction=StudioQuotaParser.Parse(new Dictionary<string,object>{{"rateLimits",new Dictionary<string,object>{{"normalModelSlug","other-model"},{"spendControlReached",true}}}},identity,"quota-connection",now);
        StudioRoutingPolicy.ApplyQuota(candidate,scopedRestriction,identity,"quota-connection",now);
        Check(!candidate.Excluded&&!candidate.Remaining.HasValue,"a restriction on another model scope does not exhaust this model");
        var secrets=new StudioRoutingStore(Path.Combine(root,"routing"));secrets.SaveSettings(new StudioRoutingSettings{Engine="openai-decisions",AutoForNew=true});
        foreach(var engine in StudioRoutingPolicy.Engines){secrets.SaveKey(engine,Key+engine);Check(secrets.LoadKey(engine)==Key+engine,"engine key round trip");Check(!Encoding.UTF8.GetString(File.ReadAllBytes(secrets.KeyPath(engine))).Contains(Key),"key is encrypted on disk");}
        Check(secrets.LoadSettings().Engine=="openai-decisions"&&secrets.LoadSettings().AutoForNew,"routing preferences survive restart");
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(secrets.SettingsPath)).Contains("openai-decisions"),"routing settings encrypted");
        File.WriteAllBytes(secrets.KeyPath("jev"),Encoding.UTF8.GetBytes("corrupted-key-file"));var before=File.ReadAllBytes(secrets.KeyPath("jev"));
        Check(Fails(()=>secrets.SaveKey("jev",Key)),"corrupt key cannot be silently overwritten");Check(File.ReadAllBytes(secrets.KeyPath("jev")).SequenceEqual(before),"corrupt key bytes preserved");secrets.ClearKey("jev");secrets.SaveKey("jev",Key);Check(secrets.LoadKey("jev")==Key,"explicit key replacement after removal works");
        Check(Fails(()=>secrets.SaveKey("../escape",Key)),"key filenames cannot escape engine allowlist");
        File.WriteAllText(secrets.SettingsPath,"corrupt-routing-settings");var invalidSettings=File.ReadAllBytes(secrets.SettingsPath);
        Check(Fails(()=>secrets.LoadSettings())&&Fails(()=>secrets.SaveSettings(new StudioRoutingSettings())),"corrupt routing settings require recovery instead of silent reset");
        Check(File.ReadAllBytes(secrets.SettingsPath).SequenceEqual(invalidSettings),"corrupt routing settings bytes preserved");
        var legacyStore=new StudioStore(Path.Combine(root,"legacy"));var legacy=new StudioSession{Draft="Legacy draft must remain",PreferredProvider="claude"};legacyStore.Save(legacy);
        string legacyFile=Path.Combine(root,"legacy",legacy.Id+".dat");var decoded=System.Security.Cryptography.ProtectedData.Unprotect(File.ReadAllBytes(legacyFile),Encoding.UTF8.GetBytes("Nephylo.Studio.v1"),System.Security.Cryptography.DataProtectionScope.CurrentUser);
        var legacyJson=Json.Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(decoded));legacyJson.Remove("AutoRouting");legacyJson.Remove("Route");
        File.WriteAllBytes(legacyFile,System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes(Json.Serialize(legacyJson)),Encoding.UTF8.GetBytes("Nephylo.Studio.v1"),System.Security.Cryptography.DataProtectionScope.CurrentUser));
        int unreadable;var restoredLegacy=legacyStore.Load(out unreadable).Single();Check(unreadable==0&&restoredLegacy.Draft==legacy.Draft&&restoredLegacy.PreferredProvider=="claude"&&!restoredLegacy.AutoRouting&&restoredLegacy.Route==null,"existing schema-2 conversations keep drafts and manual mode without routing fields");
    }
    static StudioRouteInput Catalog(double? codex,double? claude)
    {
        var candidates=new List<StudioRouteCandidate>();
        foreach(var id in new[]{"gpt-5.6-luna","gpt-6-luna","gpt-5.6-sol","gpt-6-sol","gpt-5.6-terra","gpt-6-astra"}){var item=Candidate("codex",id);item.Remaining=codex;candidates.Add(item);}
        foreach(var id in new[]{"haiku","sonnet","opus"}){var item=Candidate("claude",id);item.Remaining=claude;candidates.Add(item);}
        return new StudioRouteInput{Prompt="Independent classification fixture",Candidates=candidates};
    }
    static void ClaudeQuotaRouting()
    {
        var now=DateTime.UtcNow;
        var identity=StudioAccountIdentity.FromClaudeStatus(new Dictionary<string,object>{{"loggedIn",true},{"authMethod","claude.ai"},{"email","quota@example.invalid"}});
        Func<double,double,double,StudioQuotaSnapshot> observed=(five,weekly,opus)=>StudioClaudeQuotaParser.Parse(new Dictionary<string,object>{{"subscription_type","max"},{"rate_limits_available",true},{"rate_limits",new Dictionary<string,object>{{"five_hour",new Dictionary<string,object>{{"utilization",five},{"resets_at",now.AddHours(1).ToString("o")}}},{"seven_day",new Dictionary<string,object>{{"utilization",weekly},{"resets_at",now.AddDays(1).ToString("o")}}},{"seven_day_opus",new Dictionary<string,object>{{"utilization",opus},{"resets_at",now.AddDays(1).ToString("o")}}}}}},identity,"claude-live",now);
        foreach(string profile in new[]{"quick","balanced","deep"}){
            var input=Catalog(5,null);var quota=observed(1,3,5);
            foreach(var candidate in input.Candidates.Where(c=>c.Provider=="claude"))StudioRoutingPolicy.ApplyQuota(candidate,quota,identity,"claude-live",now);
            var plan=StudioRoutingPolicy.Plan(input,Classified(input,profile));
            Check(plan.Candidate.Provider=="claude"&&plan.Candidate.Remaining==(profile=="deep"?95:97),"official Claude quota parser feeds the same automatic workload policy: "+profile);
            foreach(var candidate in input.Candidates.Where(c=>c.Provider=="claude"))StudioRoutingPolicy.ApplyQuota(candidate,observed(100,3,0),identity,"claude-live",now);
            Check(StudioRoutingPolicy.Select(input,Classified(input,profile)).Provider=="codex","common Claude exhaustion excludes every Claude alias despite paid extra usage: "+profile);
        }
        var scoped=Catalog(5,null);
        foreach(var candidate in scoped.Candidates.Where(c=>c.Provider=="claude"))StudioRoutingPolicy.ApplyQuota(candidate,observed(1,3,100),identity,"claude-live",now);
        Check(StudioRoutingPolicy.Select(scoped,Classified(scoped,"balanced")).Provider=="claude"&&StudioRoutingPolicy.Select(scoped,Classified(scoped,"deep")).Provider=="codex","Opus-only exhaustion cannot exhaust Sonnet or transfer its quota to another model");
        var healthy=Catalog(80,null);foreach(var candidate in healthy.Candidates.Where(c=>c.Provider=="claude"))StudioRoutingPolicy.ApplyQuota(candidate,observed(1,3,0),identity,"claude-live",now);
        Check(StudioRoutingPolicy.Select(healthy,Classified(healthy,"balanced")).Provider=="codex","99 versus 80 percent from different services is not a work-capacity comparison");
        var foreign=Candidate("claude","sonnet");StudioRoutingPolicy.ApplyQuota(foreign,observed(1,3,0),identity,"other",now);
        Check(!foreign.Remaining.HasValue&&!foreign.Excluded,"foreign Claude connection contributes no numeric allowance");
    }
    static StudioRouteDecision Classified(StudioRouteInput input,string profile,string files="not_required")
    {return StudioDecisionClient.Parse(new StudioRoutingSettings(),input,Response(input,"jev",profile=="quick"?"general":profile=="deep"?"planning":"analysis",files));}
    static void SelectionPolicy(string root)
    {
        foreach(var profile in new[]{"quick","balanced","deep"}){
            var input=Catalog(80,5);var classified=Classified(input,profile);var plan=StudioRoutingPolicy.Plan(input,classified);
            string model=profile=="quick"?"gpt-6-luna":profile=="balanced"?"gpt-6-sol":"gpt-6-astra";
            Check(plan.Candidate.Provider=="codex"&&plan.Candidate.Model==model&&plan.AllowanceBand=="healthy","healthy Codex confirms the requested workload locally: "+profile);
            var reverse=Catalog(5,80);var reversedPlan=StudioRoutingPolicy.Plan(reverse,classified);
            Check(reversedPlan.Candidate.Provider=="claude"&&reversedPlan.Candidate.Model==(profile=="quick"?"haiku":profile=="balanced"?"sonnet":"opus"),"reversed allowance selects the matching Claude workload: "+profile);
            var empty=Catalog(0,null);Check(StudioRoutingPolicy.Select(empty,classified).Provider=="claude","exhausted models are never selected: "+profile);
            Check(Fails(()=>StudioRoutingPolicy.Select(Catalog(0,0),classified)),"all exhausted blocks the workload: "+profile);
            Check(StudioRoutingPolicy.Select(Catalog(null,5),classified).Provider=="codex","explicit conservation policy prefers unknown over confirmed low without calling it unlimited: "+profile);
            Check(StudioRoutingPolicy.Select(Catalog(5,null),classified).Provider=="claude","confirmed low allowance does not defeat unknown eligible alternatives: "+profile);
            Check(StudioRoutingPolicy.Select(Catalog(30,99),classified).Provider=="codex","different healthy service percentages are not treated as interchangeable work units: "+profile);
            input.Candidates.Reverse();Check(StudioRoutingPolicy.Select(input,classified).Id==plan.Candidate.Id,"catalog order cannot change deterministic confirmation: "+profile);
        }
        var healthy=Catalog(80,5);var equal=Catalog(null,null);var config=new StudioRoutingSettings();
        Check(Json.Serialize(StudioDecisionClient.RequestBody(config,healthy))==Json.Serialize(StudioDecisionClient.RequestBody(config,equal)),"quota or catalog changes cannot bias the independent classification request");
        var quick=Classified(healthy,"quick");quick.Task.Probabilities["general"]=.799;quick.Task.Probabilities["analysis"]+=.161;
        Check(Fails(()=>StudioRoutingPolicy.Select(healthy,quick)),"79.9 percent classification still fails closed");
        quick=Classified(healthy,"quick");quick.Task.Confidence=.799;
        Check(Fails(()=>StudioRoutingPolicy.Select(healthy,quick)),"79.9 percent engine confidence still fails closed");
        quick=Classified(healthy,"quick");quick.Task.Value="unclear";quick.Task.Probabilities=quick.Task.Probabilities.ToDictionary(p=>p.Key,p=>p.Key=="unclear"?.96:.04/5);Check(Fails(()=>StudioRoutingPolicy.Select(healthy,quick)),"explicit uncertainty cannot be bypassed by deterministic selection");
        var onlyQuick=new StudioRouteInput{Prompt="Large design",Candidates=new List<StudioRouteCandidate>{Candidate("codex","gpt-6-luna")}};
        Check(Fails(()=>StudioRoutingPolicy.Select(onlyQuick,Classified(onlyQuick,"deep"))),"no automatic downgrade from deep work to a quick-only catalog");
        var withUnknown=new StudioRouteInput{Prompt="Work",Candidates=new List<StudioRouteCandidate>{Candidate("codex","new-unrecognized-model")}};
        Check(Fails(()=>StudioRoutingPolicy.Select(withUnknown,Classified(withUnknown,"balanced"))),"unknown model families remain outside automatic eligibility");
        var noBinding=Candidate("codex","gpt-6-sol");noBinding.Account=null;Check(StudioRoutingPolicy.CanonicalCandidates(new[]{noBinding}).Count==0,"unknown account binding cannot become an automatic candidate");
        noBinding=Candidate("codex","gpt-6-sol");noBinding.Connection=null;Check(StudioRoutingPolicy.CanonicalCandidates(new[]{noBinding}).Count==0,"missing connection binding cannot become an automatic candidate");
        var invalidId=Candidate("codex","gpt-6-sol");invalidId.Id="not-the-catalog-id";Check(StudioRoutingPolicy.CanonicalCandidates(new[]{invalidId}).Count==0,"tampered local candidate ids are rejected");
        var older=Candidate("codex","gpt-5.6-sol");older.Remaining=80;var newer=Candidate("codex","gpt-6-sol");newer.Remaining=5;
        var scoped=new StudioRouteInput{Prompt="Bounded code task",Candidates=new List<StudioRouteCandidate>{newer,older}};
        Check(StudioRoutingPolicy.Select(scoped,Classified(scoped,"balanced")).Model==older.Model,"new generation with a low scoped budget cannot hide an older healthy model");
        newer.Remaining=80;Check(StudioRoutingPolicy.CanonicalCandidates(scoped.Candidates).Count==1&&StudioRoutingPolicy.Select(scoped,Classified(scoped,"balanced")).Model==newer.Model,"equally eligible same-family generations reduce to the newer returned model");
        var versioned=new StudioRouteInput{Prompt="Work",Candidates=new List<StudioRouteCandidate>{Candidate("codex","gpt-6.9-sol"),Candidate("codex","gpt-6.10-sol")}};
        Check(StudioRoutingPolicy.Select(versioned,Classified(versioned,"balanced")).Model=="gpt-6.10-sol","generation comparison is numeric rather than lexicographic");
        var claudeVersioned=new StudioRouteInput{Prompt="Work",Candidates=new List<StudioRouteCandidate>{Candidate("claude","claude-sonnet-4-5"),Candidate("claude","claude-sonnet-4-6")}};
        Check(StudioRoutingPolicy.Select(claudeVersioned,Classified(claudeVersioned,"balanced")).Model=="claude-sonnet-4-6","official versioned Claude names are recognized only within an explicit family");
        var gemini=new StudioRouteInput{Prompt="Work",Candidates=new List<StudioRouteCandidate>{Candidate("gemini-cli","flash"),Candidate("gemini-cli","pro"),Candidate("gemini-api","gemini-2.5-pro")}};
        Check(StudioRoutingPolicy.Select(gemini,Classified(gemini,"quick")).Model=="flash","opted-in Gemini quick group retains its explicit alias");
        Check(StudioRoutingPolicy.Select(gemini,Classified(gemini,"deep")).Provider=="gemini-cli","opted-in Gemini pro can explicitly serve deep work without implicit API billing");
        Check(Fails(()=>StudioRoutingPolicy.Select(gemini,Classified(gemini,"deep","required"))),"Gemini text-only scope cannot inspect project files");
        var persistence=new StudioStore(Path.Combine(root,"grouped-audit"));var session=new StudioSession();var planRecord=StudioRoutingPolicy.Plan(healthy,Classified(healthy,"balanced"));
        session.Route=new StudioRouteRecord{Engine="jev",Task="analysis",Provider=planRecord.Candidate.Provider,Model=planRecord.Candidate.Model,PromptHash=StudioRouteInput.Hash("fixture"),CreatedUtc=DateTime.UtcNow.ToString("o"),Probability=.96,Confidence=.95,SelectionMode="task-policy-v2",Workload=planRecord.Workload,AllowanceBand=planRecord.AllowanceBand,PoolCandidates=planRecord.PoolCandidates,WorkloadCandidates=planRecord.WorkloadCandidates,SelectedBandCandidates=planRecord.SelectedBandCandidates};
        persistence.Save(session);int unreadable;var restored=persistence.Load(out unreadable).Single();
        Check(unreadable==0&&restored.Route.SelectionMode=="task-policy-v2"&&restored.Route.Workload=="balanced"&&restored.Route.Probability==.96,"new audit persists actual task confidence rather than fabricated model certainty");
        var trial=new StudioSession{Route=new StudioRouteRecord{Engine="jev",Task="analysis",Provider="codex",Model="gpt-6-astra",PromptHash=StudioRouteInput.Hash("trial"),CreatedUtc=DateTime.UtcNow.ToString("o"),Probability=.96,SelectionMode="profile-v2",Workload="deep",AllowanceBand="unknown",PoolCandidates=3,WorkloadCandidates=1,SelectedBandCandidates=1}};
        persistence.Save(trial);Check(persistence.Load(out unreadable).Any(s=>s.Id==trial.Id&&s.Route.SelectionMode=="profile-v2"&&s.Route.Workload=="deep")&&unreadable==0,"any earlier profile-classifier trial conversations remain readable without rewriting their audit meaning");
        var auditFile=Path.Combine(root,"grouped-audit",session.Id+".dat");var original=File.ReadAllBytes(auditFile);session.Route.SelectedBandCandidates=99;
        Check(Fails(()=>persistence.Save(session))&&File.ReadAllBytes(auditFile).SequenceEqual(original),"invalid refinement audit cannot replace a previously saved conversation");
    }
    static void TaskAffinityPolicy(string root)
    {
        var intents=new[]{"general","writing","coding","debugging","code_analysis","text_analysis","technical_planning","general_planning"};
        foreach(var engine in StudioRoutingPolicy.Engines)foreach(var intent in intents){
            var settings=new StudioRoutingSettings{Engine=engine,Policy=StudioRoutingPolicy.TaskAffinity,AzureEndpoint="https://fixture.services.ai.azure.com/",AzureDeployment="decision-fixture"};var input=Catalog(90,97);input.Policy=settings.Policy;
            var wire=Response(input,engine,intent);var transport=new HttpFixture{Body=wire};
            using(var client=new StudioDecisionClient(transport)){
                var decision=client.DecideAsync(settings,Key,input,CancellationToken.None).GetAwaiter().GetResult();var plan=StudioRoutingPolicy.Plan(input,decision);
                bool prose=intent=="writing"||intent=="text_analysis"||intent=="general_planning";
                string model=intent=="general"?"gpt-6-luna":intent=="technical_planning"?"gpt-6-astra":intent=="general_planning"?"opus":prose?"sonnet":"gpt-6-sol";
                Check(plan.Candidate.Provider==(prose?"claude":"codex")&&plan.Candidate.Model==model,"same healthy quota supports distinct requested-output affinity: "+engine+" / "+intent);
                Check(plan.Intent==intent&&plan.PreferredProvider==plan.Candidate.Provider&&plan.SelectionReason=="task_preference","selection trace retains confirmed intent and explicit preference");
                Check(transport.Calls==1&&input.Questions().Count==2&&input.Questions()[0].Options.Count==9,"refined intent keeps one request with two independent questions");
                Check(!transport.RequestBody.Contains("fixture-account")&&!transport.RequestBody.Contains("remaining_percent")&&!transport.RequestBody.Contains("sonnet")&&!transport.RequestBody.Contains("gpt-6-sol"),"task affinity is local; model identities and quotas are absent from classification evidence");
                var uncertain=StudioDecisionClient.Parse(settings,input,Response(input,engine,intent,"not_required",.799));
                Check(Fails(()=>StudioRoutingPolicy.Plan(input,uncertain)),"task affinity never bypasses a low classification probability");
            }
        }
        var config=new StudioRoutingSettings{Policy=StudioRoutingPolicy.TaskAffinity};
        Func<double?,double?,string,string,StudioRoutePlan> select=(code,claude,intent,files)=>{var input=Catalog(code,claude);input.Policy=config.Policy;return StudioRoutingPolicy.Plan(input,StudioDecisionClient.Parse(config,input,Response(input,"jev",intent,files)));};
        Check(select(90,5,"writing","not_required").Candidate.Provider=="codex"&&select(90,5,"writing","not_required").SelectionReason=="allowance_precedence","healthy allowance precedes the writing preference for a low Claude account");
        Check(select(5,90,"code_analysis","not_required").Candidate.Provider=="claude"&&select(5,90,"code_analysis","not_required").SelectionReason=="allowance_precedence","healthy Claude may serve supplied code when preferred Codex is low");
        Check(select(5,90,"writing","required").Candidate.Provider=="codex"&&select(5,90,"writing","required").SelectionReason=="file_access_required","project file access precedes both task affinity and provider allowance");
        Check(select(0,90,"debugging","not_required").Candidate.Provider=="claude"&&select(90,0,"writing","not_required").Candidate.Provider=="codex","exhaustion still excludes preferred providers");
        Check(Fails(()=>select(0,0,"writing","not_required")),"all exhausted still fails closed under affinity");
        Check(select(null,90,"code_analysis","not_required").Candidate.Provider=="claude"&&select(90,null,"writing","not_required").Candidate.Provider=="codex","fresh confirmed healthy allowance outranks unknown preferred-provider allowance");
        Check(select(null,null,"writing","not_required").Candidate.Provider=="claude","unknown allowance is not fabricated; an explicit same-band preference may break the tie");
        Check(select(90,97,"coding","not_required").Candidate.Model=="gpt-6-sol","implementation is a balanced coding task, not cheap general conversion");
        Check(select(5,90,"coding","not_required").Candidate.Provider=="claude","supplied self-contained implementation can use healthy Claude when Codex is low");
        Check(select(5,90,"coding","required").Candidate.Provider=="codex","implementation needing project files preserves existing read-only scope");
        var unavailable=Catalog(90,97);unavailable.Policy=config.Policy;unavailable.Candidates.RemoveAll(c=>c.Provider=="claude");
        var fallback=StudioRoutingPolicy.Plan(unavailable,StudioDecisionClient.Parse(config,unavailable,Response(unavailable,"jev","writing")));
        Check(fallback.Candidate.Provider=="codex"&&fallback.SelectionReason=="preferred_unavailable","unavailable preferred provider has an honest pre-send eligibility explanation");
        var missingDeep=Catalog(90,97);missingDeep.Policy=config.Policy;missingDeep.Candidates.RemoveAll(c=>c.Model=="opus"||c.Model=="gpt-6-astra");
        Check(Fails(()=>StudioRoutingPolicy.Plan(missingDeep,StudioDecisionClient.Parse(config,missingDeep,Response(missingDeep,"jev","general_planning")))),"affinity does not replace missing deep models with a quick/balanced tier");
        var consistent=Catalog(90,97);consistent.Policy=config.Policy;var classified=StudioDecisionClient.Parse(config,consistent,Response(consistent,"jev","writing"));var expected=StudioRoutingPolicy.Plan(consistent,classified).Candidate.Id;consistent.Candidates.Reverse();
        Check(StudioRoutingPolicy.Plan(consistent,classified).Candidate.Id==expected,"candidate iteration order does not override intent preference");
        foreach(var legacyName in new[]{"analysis","planning","invented"})Check(Fails(()=>StudioDecisionClient.Parse(config,consistent,Response(consistent,"jev",legacyName))),"v3 cannot accept a category not requested in its schema");
        classified.Task.Confidence=.799;Check(Fails(()=>StudioRoutingPolicy.Plan(consistent,classified)),"v3 retains engine-confidence guard");
        classified=StudioDecisionClient.Parse(config,consistent,Response(consistent,"jev","writing"));classified.Files.Probabilities["not_required"]=.79;Check(Fails(()=>StudioRoutingPolicy.Plan(consistent,classified)),"v3 retains file-classification guard");
        var mismatched=new HttpFixture{Body="{}"};using(var client=new StudioDecisionClient(mismatched)){
            Check(Fails(()=>client.DecideAsync(new StudioRoutingSettings(),Key,consistent,CancellationToken.None).GetAwaiter().GetResult())&&mismatched.Calls==0,"request policy mismatch blocks before HTTP");
        }
        var store=new StudioRoutingStore(Path.Combine(root,"affinity-settings"));store.SaveKey("jev",Key);var originalKey=File.ReadAllBytes(store.KeyPath("jev"));
        store.SaveSettings(new StudioRoutingSettings{Policy=StudioRoutingPolicy.TaskAffinity,AutoForNew=true});Check(store.LoadSettings().Policy==StudioRoutingPolicy.TaskAffinity&&store.LoadSettings().AutoForNew,"affinity policy preferences persist");
        Check(File.ReadAllBytes(store.KeyPath("jev")).SequenceEqual(originalKey),"policy changes do not rewrite the engine key");
        var originalSettings=File.ReadAllBytes(store.SettingsPath);Check(Fails(()=>store.SaveSettings(new StudioRoutingSettings{Policy="invented"}))&&File.ReadAllBytes(store.SettingsPath).SequenceEqual(originalSettings),"unsupported policy cannot overwrite good settings");
        var old=new Dictionary<string,object>{{"Engine","jev"},{"AutoForNew",true},{"AzureEndpoint",""},{"AzureDeployment","Microsoft-Decision-1"}};
        File.WriteAllBytes(store.SettingsPath,System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes(Json.Serialize(old)),Encoding.UTF8.GetBytes("Nephylo.ADE.Routing.v1/settings"),System.Security.Cryptography.DataProtectionScope.CurrentUser));var oldBytes=File.ReadAllBytes(store.SettingsPath);
        Check(store.LoadSettings().Policy==StudioRoutingPolicy.LegacyTaskPolicy&&File.ReadAllBytes(store.SettingsPath).SequenceEqual(oldBytes),"settings without a policy remain legacy and are not silently rewritten");
        var persistence=new StudioStore(Path.Combine(root,"affinity-audit"));var session=new StudioSession();var auditPlan=select(90,97,"text_analysis","not_required");
        session.Route=new StudioRouteRecord{Engine="jev",Task="analysis",Intent="text_analysis",PreferredProvider=auditPlan.PreferredProvider,SelectionReason=auditPlan.SelectionReason,Provider=auditPlan.Candidate.Provider,Model=auditPlan.Candidate.Model,PromptHash=StudioRouteInput.Hash("fixture"),CreatedUtc=DateTime.UtcNow.ToString("o"),Probability=.96,Confidence=.95,SelectionMode=StudioRoutingPolicy.TaskAffinity,Workload=auditPlan.Workload,AllowanceBand=auditPlan.AllowanceBand,PoolCandidates=auditPlan.PoolCandidates,WorkloadCandidates=auditPlan.WorkloadCandidates,SelectedBandCandidates=auditPlan.SelectedBandCandidates};
        persistence.Save(session);int unreadable;var restored=persistence.Load(out unreadable).Single();Check(unreadable==0&&restored.Route.Intent=="text_analysis"&&restored.Route.Task=="analysis"&&restored.Route.PreferredProvider=="claude","new intent audit round-trips with the old coarse task semantics");
        var auditPath=Path.Combine(root,"affinity-audit",session.Id+".dat");var auditBytes=File.ReadAllBytes(auditPath);
        session.Route.Intent="technical_planning";Check(Fails(()=>persistence.Save(session))&&File.ReadAllBytes(auditPath).SequenceEqual(auditBytes),"intent/task mismatch cannot replace a valid encrypted audit");session.Route.Intent="text_analysis";
        session.Route.SelectionReason="model-is-better";Check(Fails(()=>persistence.Save(session)),"unmeasured model-superiority claims are not valid selection-reason metadata");session.Route.SelectionReason=auditPlan.SelectionReason;
        session.Route.PreferredProvider="unlisted";Check(Fails(()=>persistence.Save(session)),"unlisted preferred provider cannot enter saved audit");
    }
    sealed class Agent:IStudioAgentClient
    {
        internal readonly string Kind;internal bool Live;internal int Runs,AccountReads;internal bool InvalidateInitialRead,HideAccount;internal double? Remaining;internal List<StudioModel> Catalog;internal List<string> Prompts=new List<string>();internal string AccountSuffix="";internal Action OnConnect,OnThread,OnRun;
        public string Provider{get{return Kind;}}public string Profile{get{return StudioProviderCatalog.Profile(Kind);}}public string ConnectionId{get;private set;}public bool Connected{get{return Live;}}
        public StudioAccountIdentity AccountIdentity{get;private set;}
        public event Action<string,string> Notice{add{}remove{}}
        public event Action<string,string,string> Message;
        internal Agent(string provider){Kind=provider;ConnectionId=Guid.NewGuid().ToString("N");}
        public Task ConnectAsync(string executable,string home){if(OnConnect!=null)OnConnect();Live=true;return Task.FromResult(0);}
        public Task<Dictionary<string,object>> AccountAsync(){AccountReads++;var response=new Dictionary<string,object>{{"account",new Dictionary<string,object>{{"type","chatgpt"},{"email",HideAccount?null:Kind+AccountSuffix+"@example.invalid"}}}};AccountIdentity=StudioAccountIdentity.FromResponse(response);if(InvalidateInitialRead&&AccountReads==1)AccountIdentity=null;return Task.FromResult(response);}
        public Task<List<StudioModel>> ModelsAsync(){return Task.FromResult(Catalog??(Kind=="codex"?new[]{"gpt-6-luna","gpt-6-sol","gpt-6-astra"}:new[]{"haiku","sonnet","opus"}).Select(id=>new StudioModel{Id=id,Label=id}).ToList());}
        public Task<string> ThreadAsync(string id,string project,string model){if(OnThread!=null)OnThread();return Task.FromResult(id??Kind+"-thread");}
        public Task<string> RunBoundAsync(string thread,string project,string model,string prompt,string expected){Runs++;Prompts.Add(prompt);if(OnRun!=null)OnRun();if(Message!=null)Message("answer",Kind+" response",Boolean.TrueString);return Task.FromResult("completed");}
        public Task InterruptAsync(){return Task.FromResult(0);}public Task<StudioQuotaSnapshot> QuotaAsync(){return Task.FromResult(Remaining.HasValue?new StudioQuotaSnapshot(DateTime.UtcNow,ConnectionId,AccountIdentity.Fingerprint,null,new[]{new StudioQuotaBucket(Kind,Kind,null,null,null,new StudioQuotaWindow("primary",Remaining,300,DateTime.UtcNow.AddHours(1)),null)},true,true):StudioQuotaSnapshot.Unknown(ConnectionId,AccountIdentity));}
        public void Dispose(){Live=false;}
    }
    sealed class Decider:IStudioDecisionClient
    {
        internal int Calls;internal string[] CandidateProviders;internal string TaskType="analysis";internal string Files="not_required";internal double Probability=.96;internal Exception Failure=null;internal TaskCompletionSource<bool> Hold;internal Action BeforeReturn;
        public async Task<StudioRouteDecision> DecideAsync(StudioRoutingSettings settings,string key,StudioRouteInput input,CancellationToken cancellation)
        {Calls++;CandidateProviders=input.Candidates.Select(c=>c.Provider).Distinct().OrderBy(p=>p).ToArray();if(Failure!=null)throw Failure;if(Hold!=null){using(cancellation.Register(()=>Hold.TrySetCanceled()))await Hold.Task;}if(BeforeReturn!=null)BeforeReturn();return StudioDecisionClient.Parse(settings,input,Response(input,settings.Engine,TaskType,Files,Probability));}
        public void Dispose(){}
    }
    sealed class UiFixture:IDisposable
    {
        internal StudioWindow Window;internal Decider Judge=new Decider();internal Dictionary<string,Agent> Agents=new Dictionary<string,Agent>();internal readonly string Root;
        readonly SynchronizationContext previous;
        internal UiFixture(string root){Root=root;Directory.CreateDirectory(root);SettingsStore.DataDirectoryOverride=Path.Combine(root,"profile");previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));StudioWindow.ProviderFactoryOverride=p=>{var a=new Agent(p);Agents[p]=a;return a;};StudioWindow.ProviderExecutableOverride=p=>"synthetic-"+p;StudioWindow.DecisionClientFactoryOverride=()=>Judge;WidgetPalette.SetTheme("dark");Open();Session.Project=root;var store=Get<StudioRoutingStore>("routingStore");store.SaveKey("jev",Key);}
        internal void Open(){Window=new StudioWindow("",false,WidgetWindow.MakeResources(),WidgetWindow.Back,WidgetWindow.Fore,new FontFamily("Segoe UI"));}
        internal T Get<T>(string field){return (T)typeof(StudioWindow).GetField(field,Flags).GetValue(Window);}
        internal object Call(string name,params object[] args){try{return typeof(StudioWindow).GetMethod(name,Flags|BindingFlags.DeclaredOnly).Invoke(Window,args);}catch(TargetInvocationException error){throw error.InnerException;}}
        internal StudioSession Session{get{return Get<StudioSession>("selected");}}
        internal void Automatic(){Get<ComboBox>("routingMode").SelectedIndex=1;}
        internal void Affinity(){var settings=Get<StudioRoutingSettings>("routingSettings");settings.Policy=StudioRoutingPolicy.TaskAffinity;Get<StudioRoutingStore>("routingStore").SaveSettings(settings);}
        internal void Send(string text){Get<TextBox>("input").Text=text;Call("Enable");Check(Get<Button>("send").IsEnabled,"send ready for fixture request");Pump((Task)Call("Send"));Drain();}
        internal void Restart(){Window.Close();Open();}
        public void Dispose(){if(Window!=null)Window.Close();SettingsStore.DataDirectoryOverride=null;StudioWindow.ProviderFactoryOverride=null;StudioWindow.ProviderExecutableOverride=null;StudioWindow.DecisionClientFactoryOverride=null;SynchronizationContext.SetSynchronizationContext(previous);}
    }
    static void Ui(string root)
    {
        using(var fixture=new UiFixture(Path.Combine(root,"empty-automatic"))){
            fixture.Session.Project=null;fixture.Automatic();string placeholder=fixture.Session.Id;
            for(int i=0;i<150;i++)fixture.Call("NewSession");
            Check(fixture.Get<List<StudioSession>>("sessions").Count==1&&fixture.Session.Id==placeholder&&fixture.Session.AutoRouting,"automatic-mode preference alone does not consume conversation capacity");
            Check(fixture.Judge.Calls==0,"new automatic placeholders do not call a decision service");
        }
        using(var fixture=new UiFixture(Path.Combine(root,"success"))){
            StudioWindow.ProviderFactoryOverride=p=>{var agent=new Agent(p){Remaining=p=="claude"?80:5};fixture.Agents[p]=agent;return agent;};
            fixture.Automatic();fixture.Send("Route this first question");
            Check(fixture.Judge.Calls==1&&fixture.Agents["claude"].Runs==1&&fixture.Agents["codex"].Runs==0,"automatic selection executes only the chosen provider once");
            Check(fixture.Session.Route!=null&&fixture.Session.Route.Provider=="claude"&&fixture.Session.Route.AnswerMilliseconds.HasValue,"decision provenance and observed answer time persisted");
            Check(fixture.Session.Messages.Count==2&&fixture.Session.Messages[0].Text=="Route this first question","only original question becomes the user message");
            fixture.Send("Continue the same task");Check(fixture.Judge.Calls==1&&fixture.Agents["claude"].Runs==2,"subsequent questions stay pinned without rerouting");
            string id=fixture.Session.Id;fixture.Restart();Check(fixture.Session.Id==id&&fixture.Session.AutoRouting&&fixture.Session.Route.Provider=="claude","routing mode and audit survive window restart");
        }
        using(var fixture=new UiFixture(Path.Combine(root,"manual"))){
            Pump((Task)fixture.Call("ConnectProvider","codex",true,false));fixture.Get<ComboBox>("models").SelectedIndex=0;fixture.Send("Manual request");
            Check(fixture.Judge.Calls==0&&fixture.Agents["codex"].Runs==1,"manual selection never calls a decision service");
        }
        using(var fixture=new UiFixture(Path.Combine(root,"manual-unknown-family"))){
            StudioWindow.ProviderFactoryOverride=p=>{var agent=new Agent(p){Catalog=new List<StudioModel>{new StudioModel{Id="unrecognized-returned-model",Label="Returned manual model"}}};fixture.Agents[p]=agent;return agent;};
            Pump((Task)fixture.Call("ConnectProvider","codex",true,false));fixture.Get<ComboBox>("models").SelectedIndex=0;fixture.Send("Explicit manual model remains usable");
            Check(fixture.Agents["codex"].Runs==1&&fixture.Judge.Calls==0,"automatic family pruning never removes an explicit manual model");
        }
        using(var fixture=new UiFixture(Path.Combine(root,"all-zero"))){
            StudioWindow.ProviderFactoryOverride=p=>{var agent=new Agent(p){Remaining=0};fixture.Agents[p]=agent;return agent;};fixture.Automatic();fixture.Send("All balances exhausted");
            Check(fixture.Judge.Calls==0&&fixture.Agents.Values.All(a=>a.Runs==0)&&fixture.Get<TextBox>("input").Text=="All balances exhausted","all exhausted candidates block before judge and inference while preserving input");
        }
        using(var fixture=new UiFixture(Path.Combine(root,"unknown-account"))){
            StudioWindow.ProviderFactoryOverride=p=>{var agent=new Agent(p){HideAccount=true};fixture.Agents[p]=agent;return agent;};fixture.Automatic();fixture.Send("An unknown execution identity needs confirmation");
            Check(fixture.Judge.Calls==0&&fixture.Agents.Values.All(a=>a.Runs==0),"authenticated but unidentified accounts cannot silently join automatic selection");
        }
        using(var fixture=new UiFixture(Path.Combine(root,"refined-deep"))){
            fixture.Judge.TaskType="planning";fixture.Automatic();fixture.Send("Deep workload classification");
            Check(fixture.Session.Model=="gpt-6-astra"&&fixture.Session.Route.Workload=="deep"&&fixture.Session.Route.SelectionMode=="task-policy-v2","actual UI selects the configured family for a confirmed planning task and writes refinement provenance");
            Check(fixture.Get<TextBlock>("routingSummary").ToolTip.ToString().Contains("작업 유형 분류 확률"),"the UI does not present task confidence as model-quality confidence");
            fixture.Restart();Check(fixture.Session.Model=="gpt-6-astra"&&fixture.Session.Route.Workload=="deep","restart keeps the chosen model and workload provenance");
        }
        using(var fixture=new UiFixture(Path.Combine(root,"large-catalog"))){
            StudioWindow.ProviderFactoryOverride=p=>{var catalog=p=="codex"?Enumerable.Range(1,20).Select(i=>new StudioModel{Id="gpt-"+i+"-luna",Label="Fixture generation"}).Concat(new[]{new StudioModel{Id="gpt-6-astra",Label="Deep fixture"},new StudioModel{Id="gpt-6-sol",Label="Balanced fixture"}}).ToList():null;var agent=new Agent(p){Catalog=catalog};fixture.Agents[p]=agent;return agent;};
            fixture.Judge.TaskType="planning";fixture.Automatic();fixture.Send("Large catalogs must retain the deep family");
            Check(fixture.Session.Model=="gpt-6-astra"&&fixture.Agents["codex"].Runs==1,"legacy generations before the first eight entries cannot hide a matching deep family");
        }
        using(var fixture=new UiFixture(Path.Combine(root,"no-tier-downgrade"))){
            StudioWindow.ProviderFactoryOverride=p=>{var agent=new Agent(p){Catalog=new List<StudioModel>{new StudioModel{Id=p=="codex"?"gpt-6-luna":"haiku",Label="Quick only"}}};fixture.Agents[p]=agent;return agent;};fixture.Judge.TaskType="planning";fixture.Automatic();fixture.Send("Do not downgrade this design");
            Check(fixture.Agents.Values.All(a=>a.Runs==0)&&fixture.Session.Route==null,"a missing deep model is not replaced by a quick model");
        }
        using(var fixture=new UiFixture(Path.Combine(root,"quota-changed-after-classification"))){
            fixture.Automatic();fixture.Judge.BeforeReturn=()=>{var slots=fixture.Get<IDictionary>("providerConnections");var slot=slots["codex"];slot.GetType().GetField("Quota",Flags).SetValue(slot,new StudioQuotaSnapshot(DateTime.UtcNow,fixture.Agents["codex"].ConnectionId,fixture.Agents["codex"].AccountIdentity.Fingerprint,null,new[]{new StudioQuotaBucket("codex","Codex",null,null,null,new StudioQuotaWindow("primary",0,300,DateTime.UtcNow.AddHours(1)),null)},true,true));};
            fixture.Send("Quota changed during classification");Check(fixture.Agents.Values.All(a=>a.Runs==0),"a new quota result cannot silently change the selected model after classification");
        }
        using(var fixture=new UiFixture(Path.Combine(root,"initial-account-notification"))){
            StudioWindow.ProviderFactoryOverride=p=>{var agent=new Agent(p){InvalidateInitialRead=p=="codex"};fixture.Agents[p]=agent;return agent;};
            fixture.Automatic();fixture.Send("Use both authenticated providers after startup");
            Check(fixture.Judge.CandidateProviders.SequenceEqual(new[]{"claude","codex"}),"initial account metadata invalidation cannot silently omit the signed-in Codex candidate");
            Check(fixture.Agents["codex"].Runs==1&&fixture.Agents["claude"].Runs==0,"fresh authenticated selection executes once without a model retry");
        }
        using(var fixture=new UiFixture(Path.Combine(root,"pending-caption"))){
            fixture.Automatic();fixture.Session.Route=new StudioRouteRecord{Engine="jev",Task="analysis",Provider="claude",Model="claude-model",PromptHash=StudioRouteInput.Hash("prior proposal"),CreatedUtc=DateTime.UtcNow.ToString("o"),Probability=.95};
            fixture.Get<StudioRoutingSettings>("routingSettings").Engine="openai-decisions";fixture.Call("Enable");
            Check(fixture.Get<TextBlock>("routingSummary").Text.StartsWith("OpenAI Decisions",StringComparison.Ordinal),"a prior unsent proposal does not conceal the newly selected decision service");
        }
        using(var fixture=new UiFixture(Path.Combine(root,"uncertain"))){
            fixture.Judge.Probability=.55;fixture.Automatic();fixture.Send("Uncertain route");Check(fixture.Agents.Values.All(a=>a.Runs==0)&&fixture.Session.Messages.Count==0,"uncertain decision sends no model question");Check(fixture.Get<TextBox>("input").Text=="Uncertain route","uncertain decision preserves unsent input");
            Check(fixture.Get<TextBlock>("status").Text.Contains("작업 분류")&&fixture.Get<TextBlock>("status").Text.Contains("55"),"manual-review guidance identifies the uncertain decision and its measured probability");
            Check(fixture.Get<string>("workflowPhase")=="review"&&fixture.Get<int>("workflowReached")<3,"uncertain classification never shows a completed answer stage");
        }
        using(var fixture=new UiFixture(Path.Combine(root,"scope"))){fixture.Judge.Files="required";fixture.Automatic();fixture.Send("Inspect project files");Check(fixture.Agents["codex"].Runs==1&&fixture.Agents["claude"].Runs==0,"file inspection deterministically selects only the read-only-capable provider");}
        using(var fixture=new UiFixture(Path.Combine(root,"engine-failure"))){fixture.Judge.Failure=new StudioRouteFailure("network");fixture.Automatic();fixture.Send("Do not retry this question");Check(fixture.Judge.Calls==1&&fixture.Agents.Values.All(a=>a.Runs==0)&&fixture.Get<TextBox>("input").Text=="Do not retry this question","decision service failure has no fallback, inference or draft loss");}
        using(var fixture=new UiFixture(Path.Combine(root,"missing-key"))){fixture.Get<StudioRoutingStore>("routingStore").ClearKey("jev");fixture.Automatic();fixture.Send("Keep this question");Check(fixture.Judge.Calls==0&&fixture.Agents.Count==0&&fixture.Session.Messages.Count==0,"missing key fails before provider discovery or inference");}
        using(var fixture=new UiFixture(Path.Combine(root,"cancel"))){
            fixture.Judge.Hold=new TaskCompletionSource<bool>();fixture.Automatic();fixture.Get<TextBox>("input").Text="Cancel classification";fixture.Call("Enable");var pending=(Task)fixture.Call("Send");
            Check(fixture.Get<bool>("routingInProgress")&&!fixture.Get<Button>("send").IsEnabled,"classification locks duplicate sends");var originalId=fixture.Session.Id;fixture.Call("NewSession");Check(fixture.Session.Id==originalId,"new conversation shortcut cannot replace an in-flight routing task");
            Check(fixture.Get<string>("workflowPhase")=="classifying"&&fixture.Get<int>("workflowReached")==1,"pending decision shows the actual classification stage");
            Pump((Task)fixture.Call("Stop"));Pump(pending);Check(fixture.Agents.Values.All(a=>a.Runs==0)&&fixture.Get<TextBox>("input").Text=="Cancel classification","cancelled decision sends nothing and preserves the draft");
            Check(fixture.Get<string>("workflowPhase")=="cancelled"&&fixture.Get<int>("workflowReached")==1,"cancelled decision does not invent a selected model or received answer");
        }
        using(var fixture=new UiFixture(Path.Combine(root,"changed-connection"))){fixture.Automatic();fixture.Judge.BeforeReturn=()=>fixture.Agents["codex"].Dispose();fixture.Send("Connection changed");Check(fixture.Agents.Values.All(a=>a.Runs==0),"connection changing during classification invalidates selection");}
        using(var fixture=new UiFixture(Path.Combine(root,"changed-account"))){fixture.Automatic();fixture.Judge.BeforeReturn=()=>{fixture.Agents["codex"].AccountSuffix="-changed";fixture.Agents["codex"].AccountAsync().GetAwaiter().GetResult();};fixture.Send("Account changed");Check(fixture.Agents.Values.All(a=>a.Runs==0),"account changing during classification cannot receive the original request");}
        using(var fixture=new UiFixture(Path.Combine(root,"changed-input"))){fixture.Automatic();fixture.Judge.BeforeReturn=()=>fixture.Get<TextBox>("input").Text="New intent must be reviewed";fixture.Send("Original intent");Check(fixture.Agents.Values.All(a=>a.Runs==0)&&fixture.Get<TextBox>("input").Text=="New intent must be reviewed","changed input invalidates old decision and preserves new text");}
        using(var fixture=new UiFixture(Path.Combine(root,"save-failure"))){
            fixture.Automatic();var file=Path.Combine(SettingsStore.DataDirectory,"studio",fixture.Session.Id+".dat");
            FileStream locked=null;fixture.Judge.BeforeReturn=()=>locked=File.Open(file,FileMode.Open,FileAccess.Read,FileShare.Read);
            try{fixture.Send("Save before sending");Check(fixture.Agents.Values.All(a=>a.Runs==0)&&fixture.Get<bool>("storageBlocked"),"save failure after decision blocks all inference");Check(fixture.Session.Route==null,"failed route save restores prior selection audit");Check(fixture.Get<string>("workflowPhase")=="review","failed route save replaces selecting with an actionable attention state");}
            finally{if(locked!=null)locked.Dispose();fixture.Call("SaveCurrent");}
        }
        using(var fixture=new UiFixture(Path.Combine(root,"layout"))){
            fixture.Automatic();fixture.Get<TextBox>("input").Text="Synthetic layout preview only";
            var output=Path.Combine(Directory.GetCurrentDirectory(),"outputs","studio-routing-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
            foreach(var theme in new[]{"dark","light"}){
            fixture.Window.Close();WidgetPalette.SetTheme(theme);fixture.Open();
            foreach(var size in new[]{new[]{1180,800},new[]{720,500}}){
                fixture.Window.Width=size[0];fixture.Window.Height=size[1];fixture.Call("ApplySidebar");fixture.Call("Enable");
                var view=(FrameworkElement)fixture.Window.Content;view.Width=size[0];view.Height=size[1];view.Measure(new Size(size[0],size[1]));view.Arrange(new Rect(0,0,size[0],size[1]));view.UpdateLayout();
                foreach(var field in new[]{"send","routingMode","routingSettingsButton"}){
                    var control=fixture.Get<FrameworkElement>(field);var bounds=control.TransformToAncestor(view).TransformBounds(new Rect(control.RenderSize));
                    Check(bounds.Left>=0&&bounds.Top>=0&&bounds.Right<=size[0]+1&&bounds.Bottom<=size[1]+1,"routing and send controls remain within narrow and wide layouts");
                }
                var bitmap=new RenderTargetBitmap(size[0],size[1],96,96,PixelFormats.Pbgra32);var background=new DrawingVisual();using(var drawing=background.RenderOpen())drawing.DrawRectangle(fixture.Window.Background,null,new Rect(0,0,size[0],size[1]));bitmap.Render(background);bitmap.Render(view);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(Path.Combine(output,theme+"-"+size[0]+"x"+size[1]+".png")))encoder.Save(stream);
            }
            }
            Console.WriteLine("Routing UI fixture renders: "+output);
        }
    }
    static void TaskAffinityUi(string root)
    {
        foreach(var intent in new[]{"writing","coding","text_analysis","code_analysis","technical_planning","general_planning"})using(var fixture=new UiFixture(Path.Combine(root,"affinity-ui-"+intent))){
            fixture.Affinity();fixture.Judge.TaskType=intent;fixture.Automatic();fixture.Send("Synthetic requested-output routing: "+intent);
            string preferred=StudioRoutingPolicy.PreferredForIntent(intent);var record=fixture.Session.Route;
            Check(record!=null&&record.Intent==intent&&record.PreferredProvider==preferred&&record.Provider==preferred&&record.SelectionMode==StudioRoutingPolicy.TaskAffinity,"real UI routes each fine-grained intent through the intended official-provider abstraction");
            Check(fixture.Agents[preferred].Runs==1&&fixture.Agents.Values.Sum(a=>a.Runs)==1&&fixture.Judge.Calls==1,"one classification and one selected-provider execution");
            string tooltip=fixture.Get<TextBlock>("routingSummary").ToolTip.ToString();Check(tooltip.Contains("세부 요청")&&tooltip.Contains("질문별 선호 AI")&&tooltip.Contains("선택 근거")&&tooltip.Contains("모델 품질"),"UI explains observed intent and preference without making a quality claim");
            fixture.Get<StudioRoutingSettings>("routingSettings").Policy=StudioRoutingPolicy.LegacyTaskPolicy;fixture.Get<StudioRoutingStore>("routingStore").SaveSettings(fixture.Get<StudioRoutingSettings>("routingSettings"));
            fixture.Send("Continue this already pinned execution");Check(fixture.Judge.Calls==1&&fixture.Agents[preferred].Runs==2,"changing global routing policy cannot reroute an existing conversation");
            fixture.Restart();Check(fixture.Session.Route.Intent==intent&&fixture.Session.PreferredProvider==preferred&&fixture.Get<StudioRoutingSettings>("routingSettings").Policy==StudioRoutingPolicy.LegacyTaskPolicy,"restart keeps the v3 task audit independently of current policy");
        }
        using(var fixture=new UiFixture(Path.Combine(root,"workflow-real-events"))){
            var phases=new List<string>();
            StudioWindow.ProviderFactoryOverride=p=>{
                var agent=new Agent(p){OnConnect=()=>phases.Add("connect:"+fixture.Get<string>("workflowPhase")),OnThread=()=>phases.Add("thread:"+fixture.Get<string>("workflowPhase")),OnRun=()=>phases.Add("run:"+fixture.Get<string>("workflowPhase"))};
                fixture.Agents[p]=agent;return agent;
            };
            fixture.Judge.BeforeReturn=()=>phases.Add("decision:"+fixture.Get<string>("workflowPhase"));
            fixture.Affinity();fixture.Judge.TaskType="coding";fixture.Automatic();fixture.Send("Write a self-contained C# function");
            Check(phases.Count(p=>p=="connect:connecting")==2&&phases.Contains("decision:classifying"),"connection and classification stages follow actual provider/decision callbacks");
            Check(phases.Contains("thread:sending")&&phases.Contains("run:answer")&&fixture.Get<string>("workflowPhase")=="completed","selection, send, response wait and completion follow real execution boundaries");
            Check(fixture.Get<TextBlock>("detailRoute").Text.Contains("코드 작성")&&fixture.Session.Route.Task=="coding","new implementation intent is persisted and visible in task details");
        }
        using(var fixture=new UiFixture(Path.Combine(root,"affinity-quota-override"))){
            StudioWindow.ProviderFactoryOverride=p=>{var agent=new Agent(p){Remaining=p=="claude"?5:90};fixture.Agents[p]=agent;return agent;};fixture.Affinity();fixture.Judge.TaskType="writing";fixture.Automatic();fixture.Send("Conserve the low preferred-provider quota");
            Check(fixture.Session.Route.Provider=="codex"&&fixture.Session.Route.SelectionReason=="allowance_precedence"&&fixture.Get<TextBlock>("routingSummary").ToolTip.ToString().Contains("잔량 구간을 우선"),"quota override is enforced and explained in the actual UI");
        }
        using(var fixture=new UiFixture(Path.Combine(root,"affinity-policy-changed"))){
            fixture.Affinity();fixture.Judge.TaskType="writing";fixture.Automatic();fixture.Judge.BeforeReturn=()=>fixture.Get<StudioRoutingSettings>("routingSettings").Policy=StudioRoutingPolicy.LegacyTaskPolicy;fixture.Send("Do not apply a decision made under another policy");
            Check(fixture.Agents.Values.All(a=>a.Runs==0)&&fixture.Session.Route==null&&fixture.Get<TextBox>("input").Text.Contains("another policy"),"policy changes during classification preserve the draft and block execution");
        }
        using(var fixture=new UiFixture(Path.Combine(root,"affinity-account-changed"))){
            fixture.Affinity();fixture.Judge.TaskType="writing";fixture.Automatic();fixture.Judge.BeforeReturn=()=>{fixture.Agents["claude"].AccountSuffix="-changed";fixture.Agents["claude"].AccountAsync().GetAwaiter().GetResult();};fixture.Send("Keep account binding for the newly preferred provider");
            Check(fixture.Agents.Values.All(a=>a.Runs==0)&&fixture.Session.Route==null,"preferred Claude account change cannot receive a stale decision");
        }
        using(var fixture=new UiFixture(Path.Combine(root,"affinity-quota-changed"))){
            fixture.Affinity();fixture.Judge.TaskType="writing";fixture.Automatic();fixture.Judge.BeforeReturn=()=>{var slot=fixture.Get<IDictionary>("providerConnections")["claude"];var agent=fixture.Agents["claude"];slot.GetType().GetField("Quota",Flags).SetValue(slot,new StudioQuotaSnapshot(DateTime.UtcNow,agent.ConnectionId,agent.AccountIdentity.Fingerprint,null,new[]{new StudioQuotaBucket("claude","Claude",null,null,null,new StudioQuotaWindow("primary",0,300,DateTime.UtcNow.AddHours(1)),null)},true,true));};fixture.Send("Recheck the preferred provider after classification");
            Check(fixture.Agents.Values.All(a=>a.Runs==0)&&fixture.Session.Route==null,"post-classification exhaustion cannot silently fail over to another model");
        }
    }
    [STAThread]static int Main()
    {
        var root=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"nephylo-routing-tests-"+Guid.NewGuid().ToString("N")));
        try{Directory.CreateDirectory(root);Protocol(root);QuotasAndPersistence(root);ClaudeQuotaRouting();SelectionPolicy(root);TaskAffinityPolicy(root);Ui(root);TaskAffinityUi(root);Console.WriteLine("Studio routing tests passed: "+count+"; synthetic API/agents only, no real keys or inference.");return 0;}
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{if(root.StartsWith(Path.Combine(Path.GetFullPath(Path.GetTempPath()),"nephylo-routing-tests-"),StringComparison.OrdinalIgnoreCase)&&Directory.Exists(root))Directory.Delete(root,true);}
    }
}
