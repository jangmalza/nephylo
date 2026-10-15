using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using UsageWidget;

internal static class ObservedHistoryTests
{
    private static int checks;
    private static readonly DateTimeOffset Now=DateTimeOffset.UtcNow.AddHours(-2);
    private static void Check(bool value,string text) { ++checks; if(!value) throw new Exception(text); }
    private static bool Near(double a,double b) { return Math.Abs(a-b)<0.000001; }
    private static void Put(UsageJournal journal,DateTimeOffset at,double value,DateTimeOffset? reset=null,string source="api",string plan="Pro",double limit=100000,string label="monthly",string service="eleven")
    { journal.Observe(service,new UsageSnapshot {Plan=plan,Rows=new List<UsageRow> {new UsageRow {Label=label,Remaining=value,Limit=limit,ResetsAt=reset}}},at,source,at); }
    private static UsageObservedHistory Read(UsageJournal journal,DateTimeOffset at,string source="api",string label="monthly",string service="eleven")
    { return journal.GetObservedHistory(service,label,source,at); }
    private static double Total(UsageObservedHistory value) { return value==null ? 0 : value.Buckets.Sum(b=>b.Consumption); }
    private static int Intervals(UsageObservedHistory value) { return value==null ? 0 : value.Buckets.Sum(b=>b.Intervals); }
    private static DashboardHistoryComparison Compare(UsageJournal journal,DateTimeOffset at,int days)
    { return DashboardHistoryInsights.CompareKnownBasis(journal.GetHistory(),"eleven","monthly","api",journal.GetAnalysis("eleven","monthly","api",at),at,days,30,Read(journal,at)); }
    private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer {MaxJsonLength=16777216,RecursionLimit=32}; }
    private static string Stamp(DateTimeOffset at) { return at.ToUniversalTime().ToString("o",CultureInfo.InvariantCulture); }

    private static void PersistedComparison(string directory)
    {
        var journal=new UsageJournal(directory,false,true); var reset=Now.AddDays(16);
        const int count=14*24*12;
        for(int i=0;i<=count;i++) Put(journal,Now.AddMinutes(-5*(count-i)),100000-i,reset);
        journal.FlushAsync().GetAwaiter().GetResult();
        var loaded=new UsageJournal(directory,false);
        Check(loaded.GetHistory().Count<count/2,"Integration really exercises chart compaction before save/reload");
        var measured=Read(loaded,Now);
        Check(measured!=null && Intervals(measured)==count && Near(Total(measured),count),"Every original observed interval survives compaction, flush and reload exactly once");
        foreach(int days in new[]{1,7}) {
            var result=Compare(loaded,Now,days);
            Check(result.Comparable,"Persisted "+days+"-day comparison becomes available after normal collection");
            Check(result.Current.CoveredHours>days*24-1.1 && result.Previous.CoveredHours>days*24-1.1,"Only boundary buckets, not unobserved hours, are omitted");
            Check(Near(result.Current.Consumption,result.Current.IntervalCount) && Near(result.Previous.Consumption,result.Previous.IntervalCount),"Consumption sums original intervals instead of compacted endpoint deltas");
            Check(Near(result.Current.CoveredHours,result.Current.IntervalCount/12.0),"Coverage equals exact accepted five-minute intervals");
            Check(result.Current.PointCount<result.Current.IntervalCount || days==1,"Chart point count remains distinct from original interval count");
        }
        var copy=Read(loaded,Now); copy.Buckets[0].Consumption=999999; copy.Buckets.Clear();
        Check(Near(Total(Read(loaded,Now)),count),"Returned buckets are defensive copies");
        Put(loaded,Now.AddMinutes(5),1,reset);
        Check(Near(Total(Read(loaded,Now.AddMinutes(5))),count),"Restart does not bridge downtime with the first new reading");
        Put(loaded,Now.AddMinutes(10),0,reset);
        Check(Near(Total(Read(loaded,Now.AddMinutes(10))),count+1),"Collection resumes from the second post-restart reading");

        string file=Path.Combine(directory,"usage-journal.json");
        var document=Serializer().Deserialize<UsageJournal.JournalDocument>(File.ReadAllText(file));
        foreach(var entry in document.Analyses) { entry.ObservedSince=null; entry.ObservedUnit=null; entry.ObservedBuckets=null; }
        File.WriteAllText(file,Serializer().Serialize(document)); loaded=new UsageJournal(directory,false);
        Check(Read(loaded,Now.AddMinutes(10))==null && loaded.GetHistory().Count>0,"Pre-feature journals remain readable without fabricated aggregates");
        Check(!Compare(loaded,Now.AddMinutes(10),7).Comparable,"Legacy hourly endpoint samples do not pretend to prove continuous coverage");
        Put(loaded,Now.AddMinutes(15),1000,reset); Put(loaded,Now.AddMinutes(20),999,reset);
        measured=Read(loaded,Now.AddMinutes(20));
        Check(measured.Since==Now.AddMinutes(15) && Intervals(measured)==1 && Near(Total(measured),1),"Upgrade starts fresh exact aggregation and never backfills hidden consumption");
    }

    private static void Boundaries()
    {
        var journal=new UsageJournal(null,true); var at=Now.AddHours(-12); var reset=at.AddMinutes(47);
        Put(journal,at,1000,reset); Put(journal,at.AddMinutes(5),999,reset);
        Put(journal,at.AddMinutes(40),800,reset); Put(journal,at.AddMinutes(45),799,reset);
        Check(Intervals(Read(journal,at.AddMinutes(45)))==2 && Near(Total(Read(journal,at.AddMinutes(45))),2),"A 35-minute gap is excluded while same-hour good intervals remain");
        Put(journal,at.AddMinutes(50),1000,at.AddDays(7)); Put(journal,at.AddMinutes(55),998,at.AddDays(7));
        Check(Intervals(Read(journal,at.AddMinutes(55)))==3 && Near(Total(Read(journal,at.AddMinutes(55))),4),"Reset crossing is excluded and the following cycle resumes normally");
        Put(journal,at.AddMinutes(60),1100,at.AddDays(7)); Put(journal,at.AddMinutes(65),1097,at.AddDays(7));
        Check(Near(Total(Read(journal,at.AddMinutes(65))),7),"Top-up is excluded without losing subsequent observed consumption");
        journal.PauseAnalysis(); Put(journal,at.AddMinutes(70),1000,at.AddDays(7)); Put(journal,at.AddMinutes(75),999,at.AddDays(7));
        Check(Near(Total(Read(journal,at.AddMinutes(75))),8),"Pause prevents a hidden interval and resumes after the first new reading");
        var options=journal.Options; options.HistoryEnabled=false; journal.SaveOptions(options);
        Put(journal,at.AddMinutes(80),900,at.AddDays(7)); Check(Read(journal,at.AddMinutes(80))==null,"Disabled history exposes no aggregate series");
        options.HistoryEnabled=true; journal.SaveOptions(options); Put(journal,at.AddMinutes(85),800,at.AddDays(7)); Put(journal,at.AddMinutes(90),799,at.AddDays(7));
        Check(Near(Total(Read(journal,at.AddMinutes(90))),9),"Re-enabling history does not connect across disabled collection");
        journal.Observe("eleven",new UsageSnapshot {Rows=new List<UsageRow>()},at.AddMinutes(95),"api",at.AddMinutes(95));
        Put(journal,at.AddMinutes(100),700,at.AddDays(7)); Put(journal,at.AddMinutes(105),699,at.AddDays(7));
        Check(Near(Total(Read(journal,at.AddMinutes(105))),10),"Missing rows split collection even when they return within thirty minutes");
        Put(journal,at.AddMinutes(110),698,at.AddDays(7),"cli"); Put(journal,at.AddMinutes(115),697,at.AddDays(7),"cli");
        Check(Read(journal,at.AddMinutes(115))==null && Near(Total(Read(journal,at.AddMinutes(115),"cli")),1),"Source switches clear prior-source aggregates");
        Put(journal,at.AddMinutes(120),696,at.AddDays(7));
        Check(Intervals(Read(journal,at.AddMinutes(120)))==0,"Returning A to B to A does not resurrect original A sums");
        Put(journal,at.AddMinutes(125),695,at.AddDays(7)); Put(journal,at.AddMinutes(130),694,at.AddDays(7),"api","Team");
        Check(Intervals(Read(journal,at.AddMinutes(130)))==0,"Plan change starts a new aggregation epoch");
        Put(journal,at.AddMinutes(135),693,at.AddDays(7),"api","Team"); Put(journal,at.AddMinutes(140),692,at.AddDays(7),"api","Team",200000);
        Check(Intervals(Read(journal,at.AddMinutes(140)))==0,"Capacity change starts a new aggregation epoch");
        journal.SetAnalysisOwner(new String('a',64));
        Check(Read(journal,at.AddMinutes(140))==null,"Account ownership reset removes exact aggregates while chart history may remain");
        Put(journal,at.AddMinutes(145),691); Put(journal,at.AddMinutes(150),690); journal.ForgetService("eleven");
        Check(Read(journal,at.AddMinutes(150))==null,"Explicit reconnect clears aggregates");
        Put(journal,at.AddMinutes(155),689); Put(journal,at.AddMinutes(160),688); journal.ClearHistory();
        Check(Read(journal,at.AddMinutes(160))==null && journal.GetHistory().Count==0,"ClearHistory clears aggregates and chart points together");
        Put(journal,at.AddMinutes(160),688);
        Check(Read(journal,at.AddMinutes(160))==null,"Cached identical observation cannot recreate cleared aggregates");
        Put(journal,at.AddMinutes(165),687,null,"manual"); Put(journal,at.AddMinutes(170),686,null,"manual");
        Check(Intervals(Read(journal,at.AddMinutes(170),"manual"))==0,"Manual readings never infer consumption");

        journal=new UsageJournal(null,true);
        for(int i=0;i<20;i++) {
            var poll=at.AddMinutes(i*5); var cycleEnd=at.AddMinutes((i/6+1)*30);
            Put(journal,poll,100-i%6,cycleEnd,"code","Pro",100,"5h session","claude");
        }
        Check(Intervals(Read(journal,at.AddMinutes(95),"code","5h session","claude"))==16,"Short limits resume after each excluded reset boundary");

        var points=new List<UsageHistoryPoint> {
            new UsageHistoryPoint {ServiceId="eleven",Label="monthly",Source="api",ObservedAt=Now.AddHours(-24).AddMinutes(-5),Remaining=100,Limit=1000},
            new UsageHistoryPoint {ServiceId="eleven",Label="monthly",Source="api",ObservedAt=Now.AddHours(-24).AddMinutes(5),Remaining=90,Limit=1000}
        };
        var boundary=new UsageObservedHistory {Source="api",Unit="크레딧",Since=points[0].ObservedAt,
            Buckets=new List<UsageObservedBucket> {new UsageObservedBucket {Start=points[0].ObservedAt,End=points[1].ObservedAt,Hours=1.0/6,Consumption=10,Intervals=2,MaximumGapMinutes=5}}};
        var comparison=DashboardHistoryInsights.CompareKnownBasis(points,"eleven","monthly","api",new UsageAnalysis {Source="api",CollectionHours=48},Now,1,30,boundary);
        Check(!comparison.Current.HasConsumption && !comparison.Previous.HasConsumption,"A stored aggregate crossing a period boundary is excluded from both periods without prorating");
    }

    private static void PersistenceFailures(string directory)
    {
        var at=Now.AddHours(-1); var journal=new UsageJournal(directory,false);
        Put(journal,at,100); Put(journal,at.AddMinutes(5),99);
        string file=Path.Combine(directory,"usage-journal.json"); var saved=File.ReadAllText(file);
        using(var held=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) {
            bool failed=false; try {Put(journal,at.AddMinutes(10),98);} catch(IOException) {failed=true;}
            Check(failed && Near(Total(Read(journal,at.AddMinutes(10))),1),"Failed synchronous save rolls back aggregate data and previous-reading state");
        }
        Check(File.ReadAllText(file)==saved,"Failed synchronous save preserves the previous journal");
        Put(journal,at.AddMinutes(10),98); Check(Near(Total(Read(journal,at.AddMinutes(10))),2),"Retry counts rolled-back interval exactly once");
        journal=new UsageJournal(directory,false,true); Put(journal,at.AddMinutes(15),97); journal.FlushAsync().GetAwaiter().GetResult();
        using(var held=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) {
            Put(journal,at.AddMinutes(20),96); bool failed=false;
            try {journal.FlushAsync().GetAwaiter().GetResult();} catch(IOException) {failed=true;}
            Check(failed && Near(Total(Read(journal,at.AddMinutes(20))),3),"Failed background flush retains unsaved exact aggregate data");
            string recovery=Path.Combine(directory,"recovery.json"); journal.WriteRecoveryBackup(recovery);
            var backup=Serializer().Deserialize<UsageJournal.JournalDocument>(File.ReadAllText(recovery));
            Check(Near(backup.Analyses.Single().ObservedBuckets.Sum(b=>b.c),3),"Recovery backup includes pending observed aggregates");
        }
        journal.FlushAsync().GetAwaiter().GetResult();
        Check(Near(Total(Read(new UsageJournal(directory,false),at.AddMinutes(20))),3),"Retry flush persists pending aggregates without another reading");
        journal.ClearHistory(); journal.FlushAsync().GetAwaiter().GetResult();
        Check(Read(new UsageJournal(directory,false),at.AddMinutes(20))==null,"Queued snapshots cannot resurrect cleared aggregates");
    }

    private static void MaximumFile(string directory)
    {
        Directory.CreateDirectory(directory); string file=Path.Combine(directory,"usage-journal.json");
        var document=new UsageJournal.JournalDocument {Version=1,Options=new UsageTrackingOptions(),Analyses=new List<UsageJournal.AnalysisStateDocument>(),Points=new List<UsageJournal.PointDocument>()};
        var start=Now.AddDays(-29); string longLabel=new String('가',115); var end=Now;
        for(int row=0;row<96;row++) {
            string label=longLabel+row.ToString();
            var entry=new UsageJournal.AnalysisStateDocument {ServiceId="eleven",Label=label,Source="api",Unit="credits",Epoch=Guid.NewGuid().ToString("N"),
                TimeZoneId=TimeZoneInfo.Local.Id,Plan="Pro",FirstObserved=Stamp(start),LastObserved=Stamp(end),Remaining=1000,Limit=100000,Supported=true,
                ObservedSince=Stamp(start),ObservedUnit="크레딧",ObservedBuckets=new List<UsageJournal.ObservedBucketDocument>()};
            for(int hour=0;hour<29*24;hour++) entry.ObservedBuckets.Add(new UsageJournal.ObservedBucketDocument {s=start.AddHours(hour).UtcTicks,e=start.AddHours(hour+1).UtcTicks,c=12,h=1,g=5,n=12});
            document.Analyses.Add(entry);
        }
        // Exercise the largest chart payload too, with 96 long UTF-8 labels.
        for(int i=0;i<UsageJournal.MaximumPoints;i++) document.Points.Add(new UsageJournal.PointDocument {ServiceId="eleven",Label=longLabel+(i%96),Source="api",
            ObservedAt=Stamp(end.AddHours(-18).AddSeconds(i*3)),Remaining=10000-i%5000,Limit=100000,DisplayValue=new String('나',80)});
        // Source fixture may exceed the production cap before bounding; load
        // aggregates alone, then add chart payload to its already bounded JSON.
        var chart=document.Points; document.Points=null; File.WriteAllText(file,Serializer().Serialize(document));
        var journal=new UsageJournal(directory,false); journal.WriteRecoveryBackup(Path.Combine(directory,"bounded.json"));
        var bounded=Serializer().Deserialize<UsageJournal.JournalDocument>(File.ReadAllText(Path.Combine(directory,"bounded.json")));
        Check(bounded.Analyses.Sum(s=>s.ObservedBuckets.Count)<=UsageJournal.MaximumObservedBuckets,"Ninety-six 30-day metric series coarsen to bounded exact aggregate count");
        Check(Near(bounded.Analyses.Sum(s=>s.ObservedBuckets.Sum(b=>b.h)),96*29*24),"Coarsening preserves all observed hours without inventing coverage");
        Check(Near(bounded.Analyses.Sum(s=>s.ObservedBuckets.Sum(b=>b.c)),96*29*24*12),"Coarsening preserves exact consumption across all metrics");
        bounded.Points=chart; File.WriteAllText(file,Serializer().Serialize(bounded)); journal=new UsageJournal(directory,false);
        journal.WriteRecoveryBackup(Path.Combine(directory,"maximum.json"));
        Check(new FileInfo(Path.Combine(directory,"maximum.json")).Length<16777216,"Bounded aggregates plus maximum chart rows and long UTF-8 text stay below 16 MiB");
        var loaded=Serializer().Deserialize<UsageJournal.JournalDocument>(File.ReadAllText(Path.Combine(directory,"maximum.json")));
        Check(loaded.Analyses.Count==96 && loaded.Points.Count==UsageJournal.MaximumPoints,"File size test retains all metrics and the maximum twenty thousand chart points");
        Console.WriteLine("Maximum journal fixture bytes: "+new FileInfo(Path.Combine(directory,"maximum.json")).Length);
    }

    [STAThread] public static int Main()
    {
        string directory=Path.Combine(Path.GetTempPath(),"NephyloObservedTests-"+Guid.NewGuid().ToString("N"));
        try { PersistedComparison(Path.Combine(directory,"comparison")); Boundaries(); PersistenceFailures(Path.Combine(directory,"failures")); MaximumFile(Path.Combine(directory,"maximum"));
            Console.WriteLine("Observed history integration tests passed: "+checks+" assertions (exact aggregation, compaction/reload, boundaries, storage recovery and size)."); return 0; }
        catch(Exception error) {Console.Error.WriteLine(error);return 1;}
        finally {if(Directory.Exists(directory)) Directory.Delete(directory,true);}
    }
}
