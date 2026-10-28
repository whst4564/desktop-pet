using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using CodexCompanion;

public static class ConversationBridgeTests
{
    static int checks;
    static string home, sessions;
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
    static readonly DateTime Now = DateTime.UtcNow;
    static void Check(bool value, string label)
    { if (!value) throw new Exception("FAIL " + label); checks++; Console.WriteLine("PASS " + label); }
    static string Line(object value) { return Json.Serialize(value) + "\n"; }
    static string Stamp(int step) { return Now.AddSeconds(step).ToString("o"); }
    static string Header(string id, object source)
    { return Line(new { timestamp = Stamp(0), type = "session_meta", payload = new { id = id, source = source, originator = "Codex Desktop" } }); }
    static string Message(string role, string text, int step, string phase, string id)
    { return Line(new { timestamp = Stamp(step), type = "response_item", payload = new { type = "message", role = role, phase = phase, id = id,
        content = new[] { new { type = role == "assistant" ? "output_text" : "input_text", text = text } } } }); }
    static string Message(string role, string text, int step)
    { return Message(role, text, step, role == "assistant" ? "final_answer" : null, "msg-" + step); }
    static string Event(string kind, string text, int step)
    { return Line(new { timestamp = Stamp(step), type = "event_msg", payload = new { type = kind, message = text } }); }
    static string Create(string id, string content)
    {
        string path = Path.Combine(sessions, "rollout-" + id + ".jsonl");
        File.WriteAllText(path, Header(id, "vscode") + content, new UTF8Encoding(false));
        return path;
    }
    static ConversationSnapshot Read(string id) { return new ConversationBridge(home).Read(id); }
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--local-readonly")
        {
            var actual = new ConversationBridge(null).Read(args.Length > 1 ? args[1] : Environment.GetEnvironmentVariable("CODEX_THREAD_ID"));
            Console.WriteLine(Json.Serialize(new { count = actual.Messages.Count, roles = actual.Messages.Select(m => m.Role).Distinct().ToArray(),
                truncated = actual.Truncated, observedUtc = actual.ObservedUtc, notice = actual.Notice }));
            return;
        }
        home = Path.Combine(Path.GetDirectoryName(typeof(ConversationBridgeTests).Assembly.Location), "conversation-fixture-" + Guid.NewGuid().ToString("N"));
        sessions = Path.Combine(home, "sessions"); Directory.CreateDirectory(sessions);
        string path = Create("main", Message("user", "你好🌼", 1) + Message("assistant", "世界你好", 2));
        var bridge = new ConversationBridge(home); var snapshot = bridge.Read("main");
        Check(snapshot.Messages.Count == 2 && snapshot.Messages[0].Text == "你好🌼" && snapshot.Messages[1].Role == "assistant", "UTF8 user and assistant display messages load in order");
        Check(snapshot.ObservedUtc == Now.AddSeconds(2) && snapshot.ThreadId == "main", "timestamp and selected thread accompany detached history");
        snapshot.Messages[0].Text = "mutated"; snapshot.Messages.Clear();
        Check(bridge.Read("main").Messages[0].Text == "你好🌼", "returned snapshots cannot mutate retained conversation");
        File.AppendAllText(path, Message("assistant", "增量完成", 3), new UTF8Encoding(false));
        Check(bridge.Read("main").Messages.Last().Text == "增量完成", "only appended complete messages extend the retained history");
        string partial = Message("user", "半行与emoji🙂", 4);
        byte[] utf8 = Encoding.UTF8.GetBytes(partial);
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.Write(utf8, 0, utf8.Length - 1); stream.Flush();
            Check(bridge.Read("main").Messages.Count == 3, "unterminated JSONL record is deferred");
            stream.WriteByte(10); stream.Flush();
        }
        Check(bridge.Read("main").Messages.Last().Text == "半行与emoji🙂", "partial bytes become one valid Unicode message after newline");
        File.AppendAllText(path, "{broken}\n" + Message("assistant", "继续有效", 5), new UTF8Encoding(false));
        Check(bridge.Read("main").Messages.Last().Text == "继续有效", "malformed record cannot hide a later valid display message");

        Create("private", Message("system", "SYSTEM SECRET", 1) + Message("developer", "DEVELOPER SECRET", 2)
            + Message("assistant", "ANALYSIS SECRET", 3, "analysis", "analysis")
            + Line(new { timestamp = Stamp(4), type = "response_item", payload = new { type = "function_call_output", output = "TOOL SECRET" } })
            + Line(new { timestamp = Stamp(5), type = "response_item", payload = new { type = "reasoning", summary = "REASONING SECRET" } })
            + Message("user", "<environment_context>ENV SECRET</environment_context>", 6)
            + Message("user", "<tool_response>TOOL FORWARD SECRET</tool_response>", 7)
            + Message("user", "Message Type: MESSAGE\nPRIVATE AGENT INSTRUCTION", 8)
            + Message("assistant", "可见进度", 9, "commentary", "visible-progress")
            + Message("assistant", "可见结论", 10));
        snapshot = Read("private");
        Check(snapshot.Messages.Count == 2 && snapshot.Messages[0].Text == "可见进度" && snapshot.Messages[1].Text == "可见结论", "system developer reasoning analysis tool and hidden context are excluded");
        Create("channel", Line(new { timestamp = Stamp(1), type = "response_item", payload = new { type = "message", role = "assistant", phase = "final_answer", channel = "analysis", content = new[] { new { type = "output_text", text = "SECRET" } } } })
            + Message("assistant", "正常", 2));
        Check(Read("channel").Messages.Count == 1, "analysis channel is rejected even when another phase claims final");
        Create("mixed", Message("user", "<external_codex_apps_open_page>{\"page_id\":null}</external_codex_apps_open_page>\n继续这个任务", 1)
            + Message("user", "前文<system_reminder>SECRET</system_reminder>后文", 2)
            + Message("user", "<send_user_message_question_reply>[{\"question\":\"是否继续\",\"answer\":\"继续\"}]</send_user_message_question_reply>", 3));
        snapshot = Read("mixed");
        Check(snapshot.Messages.Select(m => m.Text).SequenceEqual(new[] { "继续这个任务", "前文后文", "继续" }), "known hidden wrappers are removed while actual user text and answers remain");
        Check(ConversationBridge.CleanVisibleText("<environment_context>incomplete secret") == null, "unterminated hidden context does not leak trailing content");
        Check(ConversationBridge.CleanVisibleText("请解释 <custom>我的示例</custom>") == "请解释 <custom>我的示例</custom>", "unknown user markup is preserved as user content");

        Create("dupes", Event("user_message", "用户问题", 1) + Message("user", "用户问题", 1)
            + Message("assistant", "回答", 2) + Event("agent_message", "回答", 2));
        snapshot = Read("dupes");
        Check(snapshot.Messages.Count == 2 && snapshot.Messages[0].Id == "msg-1" && snapshot.Messages[1].Id == "msg-2", "event and canonical records deduplicate in both orders and prefer canonical IDs");
        Create("repeat", Message("user", "继续", 1) + Message("user", "继续", 2)
            + Message("assistant", "答", 3, "final_answer", "same-id") + Message("assistant", "答", 4, "final_answer", "same-id"));
        snapshot = Read("repeat");
        Check(snapshot.Messages.Count == 3 && snapshot.Messages.Take(2).All(m => m.Text == "继续"), "legitimate repeated user text remains while repeated canonical ID is suppressed");
        Create("emptyid", Message("assistant", "一", 1, "final_answer", "") + Message("assistant", "二", 2, "final_answer", ""));
        Check(Read("emptyid").Messages.Count == 2, "empty canonical IDs use stable local offsets rather than collapsing messages");
        Create("eventonly", Event("user_message", "event question", 1) + Event("agent_message", "event answer", 2));
        Check(Read("eventonly").Messages.Count == 2, "event-only local formats remain readable");

        Create("other", Message("user", "另一个对话", 1));
        Check(bridge.Read("other").Messages.Single().Text == "另一个对话" && bridge.Read("main").Messages.Count == 5, "switching selected threads never mixes histories");
        File.WriteAllText(Path.Combine(sessions, "rollout-spoof.jsonl"), Header("another-id", "vscode") + Message("user", "wrong source", 1));
        Check(Read("spoof").Messages.Count == 0, "filename match must also pass exact session header identity");
        File.WriteAllText(Path.Combine(sessions, "rollout-child.jsonl"), Header("child", new { subagent = new { other = "worker" } }) + Message("user", "subagent instruction", 1));
        Check(Read("child").Messages.Count == 0, "subagent instruction transcripts are not shown as user conversation");
        Check(Read("../main").Messages.Count == 0 && Read("missing").Messages.Count == 0 && new ConversationBridge(Path.Combine(home, "missing")).Read("main").Messages.Count == 0, "invalid IDs and unavailable logs remain empty with no invented messages");

        string shrinking = Create("shrink", Message("user", "original long text", 1) + Message("assistant", "second", 2));
        var shrink = new ConversationBridge(home); shrink.Read("shrink");
        File.WriteAllText(shrinking, Header("shrink", "vscode") + Message("user", "new", 1), new UTF8Encoding(false));
        Check(shrink.Read("shrink").Messages.Single().Text == "new", "file truncation resets the cursor and old visible content");
        string same = Create("replace", Message("user", "AAAA", 1));
        var replace = new ConversationBridge(home); replace.Read("replace");
        File.WriteAllText(same, Header("replace", "vscode") + Message("user", "BBBB", 1), new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(same, DateTime.UtcNow.AddSeconds(2));
        Check(replace.Read("replace").Messages.Single().Text == "BBBB", "same-size rewritten logs do not retain stale text");

        var many = new StringBuilder(); for (int i = 0; i < 100; i++) many.Append(Message(i % 2 == 0 ? "user" : "assistant", "message-" + i, i));
        Create("many", many.ToString()); snapshot = Read("many");
        Check(snapshot.Messages.Count == 64 && snapshot.Messages[0].Text == "message-36" && snapshot.Truncated, "history is capped at the newest64 display messages");
        var huge = new StringBuilder(); for (int i = 0; i < 20; i++) huge.Append(Message("user", i.ToString() + new String('中', 70000), i));
        Create("bytecap", huge.ToString()); snapshot = Read("bytecap");
        Check(snapshot.Truncated && snapshot.Messages.Sum(m => Encoding.UTF8.GetByteCount(m.Text)) <= 1024 * 1024
            && snapshot.Messages.All(m => Encoding.UTF8.GetByteCount(m.Text) <= 128 * 1024), "Unicode message and aggregate text caps are bounded in UTF8 bytes");
        Check(snapshot.Messages.All(m => !Char.IsHighSurrogate(m.Text.Last())), "truncation never leaves a dangling UTF16 high surrogate");
        Create("tail", Line(new { timestamp = Stamp(1), type = "response_item", payload = new { type = "function_call_output", output = new String('x', 9 * 1024 * 1024) } })
            + Message("user", "latest after huge output", 2));
        snapshot = Read("tail");
        Check(snapshot.Messages.Count == 1 && snapshot.Messages[0].Text == "latest after huge output" && snapshot.Truncated, "large logs bootstrap from a bounded recent tail and skip partial tool records");

        Console.WriteLine("ALL " + checks + " CONVERSATION CHECKS PASSED");
    }
}
