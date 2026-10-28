using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using CodexCompanion;

class DataBridgeTests
{
    static int checks;
    static string Event(string stamp, string type, string fields)
    {
        return "{\"timestamp\":\"" + stamp + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"" + type + "\"" + fields + "}}\n";
    }
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
        checks++;
        Console.WriteLine("PASS " + name);
    }
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--local-readonly")
        {
            CompanionSnapshot local = new DataBridge().Poll();
            Console.WriteLine(new JavaScriptSerializer().Serialize(new {
                model=local.Model, effort=local.Effort, state=local.State.ToString(),
                primaryUsed=local.Primary == null ? null : local.Primary.UsedPercent,
                secondaryUsed=local.Secondary == null ? null : local.Secondary.UsedPercent,
                modelObservedUtc=local.ModelObservedUtc, limitsObservedUtc=local.LimitsObservedUtc,
                overlayReadSucceeded=local.OverlayReadSucceeded, overlayVisible=local.OverlayVisible,
                boundsAvailable=local.OverlayBounds != null, petSize=local.PetSize,
                notice=local.Notice
            }));
            return;
        }
        // A DLL hosted by WindowsPowerShell has the shell's installation as
        // AppDomain.BaseDirectory. Keep fixtures beside this test assembly.
        string fixtureRoot = Path.GetDirectoryName(typeof(DataBridgeTests).Assembly.Location);
        string home = Path.Combine(fixtureRoot, "databridge-fixture-" + Guid.NewGuid().ToString("N"));
        string sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        string rootPath = Path.Combine(sessions, "rollout-root-test.jsonl");
        string childPath = Path.Combine(sessions, "rollout-child-test.jsonl");
        string settings = Path.Combine(home, ".codex-global-state.json");
        string t0 = DateTime.UtcNow.AddSeconds(-30).ToString("o");
        string t1 = DateTime.UtcNow.AddSeconds(-20).ToString("o");
        string t2 = DateTime.UtcNow.AddSeconds(-10).ToString("o");
        File.WriteAllText(rootPath,
            "{\"timestamp\":\""+t0+"\",\"type\":\"session_meta\",\"payload\":{\"id\":\"root-test\",\"originator\":\"Codex Desktop\",\"source\":\"vscode\",\"base_instructions\":\"PRIVATE SHOULD NEVER BE EXPOSED\"}}\n" +
            Event(t0, "task_started", "") +
            "{\"timestamp\":\""+t1+"\",\"type\":\"turn_context\",\"payload\":{\"model\":\"root-model\",\"effort\":\"high\",\"summary\":\"private \\\" brace } [ stuff\"}}\n" +
            Event(t1,"token_count",",\"rate_limits\":{\"limit_id\":\"codex\",\"primary\":{\"used_percent\":25,\"window_minutes\":300,\"resets_at\":4102444800},\"secondary\":{\"used_percent\":5,\"window_minutes\":10080,\"resets_at\":4102444800}}"), new UTF8Encoding(false));
        // Context is deliberately pushed beyond the initial 512-KB tail.
        File.AppendAllText(rootPath,"{\"timestamp\":\""+t1+"\",\"type\":\"response_item\",\"payload\":{\"text\":\""+new String('x',700000)+"\"}}\n",new UTF8Encoding(false));
        File.WriteAllText(childPath,
            "{\"timestamp\":\""+t2+"\",\"type\":\"session_meta\",\"payload\":{\"id\":\"child-test\",\"originator\":\"Codex Desktop\",\"parent_thread_id\":\"root-test\",\"source\":{\"subagent\":{\"other\":\"guardian\"}}}}\n" +
            "{\"timestamp\":\""+t2+"\",\"type\":\"turn_context\",\"payload\":{\"model\":\"codex-auto-review\",\"effort\":\"low\"}}\n" +
            Event(t2,"token_count",",\"rate_limits\":{\"limit_id\":\"codex\",\"primary\":{\"used_percent\":30,\"window_minutes\":300,\"resets_at\":4102444800},\"secondary\":{\"used_percent\":7,\"resets_at\":4102444800}}"), new UTF8Encoding(false));
        File.WriteAllText(settings,"{\"unrelated-private\":{\"secret\":\"NEVER COPY\"},\"electron-avatar-overlay-open\":true,\"electron-avatar-overlay-bounds\":{\"x\":102,\"y\":584,\"displayBounds\":{\"width\":1707,\"height\":1067}}}",new UTF8Encoding(false));
        DataBridge bridge = new DataBridge(home, null);
        CompanionSnapshot s = bridge.Poll();
        Check(s.Model == "root-model" && s.Effort == "high", "context bootstrap past large message; subagent model excluded");
        Check(s.State == CompanionWorkState.Working, "task_started maps to Working");
        Check(s.Primary.UsedPercent == 30 && s.Primary.RemainingPercent == 70 && s.Secondary.UsedPercent == 7, "newest shared quota and remaining percent");
        Check(s.OverlayReadSucceeded && s.OverlayVisible == true && s.OverlayBounds.X == 102 && s.OverlayBounds.DisplayWidth == 1707 && !s.PetSize.HasValue, "overlay fields read; absent size stays unknown");
        string complete = Event(DateTime.UtcNow.ToString("o"), "task_complete", ",\"last_agent_message\":\"PRIVATE FINAL\"");
        using (FileStream writer = new FileStream(rootPath,FileMode.Append,FileAccess.Write,FileShare.ReadWrite))
        {
            byte[] partial = Encoding.UTF8.GetBytes(complete.TrimEnd('\n'));
            writer.Write(partial,0,partial.Length); writer.Flush();
            Check(bridge.Poll().State == CompanionWorkState.Working, "unfinished JSONL write deferred with shared handle");
            writer.WriteByte(10); writer.Flush();
            Check(bridge.Poll().State == CompanionWorkState.Idle, "incremental newline completes task without reading message");
        }
        File.AppendAllText(rootPath,"{broken}\n"+Event(DateTime.UtcNow.AddSeconds(1).ToString("o"),"turn_aborted",""),new UTF8Encoding(false));
        Check(bridge.Poll().State == CompanionWorkState.Interrupted,"malformed line isolated; later abort processed");
        File.WriteAllText(settings,"{\"electron-avatar-overlay-open\":false}",new UTF8Encoding(false));
        s=bridge.Poll();
        Check(s.OverlayReadSucceeded && s.OverlayVisible == false && s.OverlayBounds == null, "native hide toggle and missing bounds read on next poll");
        File.WriteAllText(settings,"{\"electron-avatar-overlay-open\":true",new UTF8Encoding(false));
        s=bridge.Poll();
        Check(!s.OverlayReadSucceeded && !s.OverlayVisible.HasValue,"truncated settings cannot manufacture visibility");
        bridge.PreferredThreadId="missing-thread";
        s=bridge.Poll();
        Check(s.Model == null && s.State == CompanionWorkState.Unknown,"unavailable bound thread stays unknown");
        string oldPath=Path.Combine(sessions,"rollout-old-test.jsonl");
        File.WriteAllText(oldPath,"{\"timestamp\":\"2000-01-01T00:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"old-test\",\"originator\":\"Codex Desktop\",\"source\":\"vscode\"}}\n" + Event("2000-01-01T00:00:00Z","task_started",""),new UTF8Encoding(false));
        s=new DataBridge(home,"old-test").Poll();
        Check(s.State == CompanionWorkState.Unknown && s.StateIsStale,"stale task state explicitly unknown");
        UsageWindow expired = new UsageWindow();
        // Setter is internal and this fixture is compiled into the same assembly.
        expired.UsedPercent=50; expired.ResetsAtUtc=DateTime.UtcNow.AddMinutes(-1);
        Check(expired.IsExpired && !expired.RemainingPercent.HasValue,"expired quota needs a fresh sample");
        DataBridge empty = new DataBridge(Path.Combine(home,"missing-home"),null);
        s=empty.Poll();
        Check(s.Model == null && s.Primary == null && s.State == CompanionWorkState.Unknown && !s.OverlayVisible.HasValue,"missing data never becomes invented defaults");
        Console.WriteLine("ALL " + checks + " CHECKS PASSED");
    }
}
