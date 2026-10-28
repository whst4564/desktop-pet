// Local, read-only conversation fallback. No credentials, hidden reasoning,
// tool output, instruction messages, network access, or persisted transcript.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexCompanion
{
    public sealed class ConversationMessage
    {
        public string Id { get; set; }
        public string Role { get; set; }
        public string Text { get; set; }
        public DateTime? ObservedUtc { get; set; }
    }

    public sealed class ConversationSnapshot
    {
        public string ThreadId { get; set; }
        public List<ConversationMessage> Messages { get; set; }
        public DateTime? ObservedUtc { get; set; }
        public string Notice { get; set; }
        public bool Truncated { get; set; }
    }

    public sealed class ConversationBridge
    {
        const int InitialTailBytes = 8 * 1024 * 1024;
        const int IncrementalReadBytes = 2 * 1024 * 1024;
        const int MaxLineBytes = 16 * 1024 * 1024;
        const int MaxMessageBytes = 128 * 1024;
        const int MaxTextBytes = 1024 * 1024;
        const int MaxMessages = 64;
        readonly object gate = new object();
        readonly string sessionsRoot;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = MaxLineBytes, RecursionLimit = 48 };
        Cursor current;
        DateTime lastDiscoveryUtc;
        string lastRequestedThread;
        static readonly string[] HiddenTags = new[] {
            "environment_context", "external_codex_apps_open_page", "system_reminder", "system-reminder",
            "app-context", "skills_instructions", "permissions", "permissions_instructions", "session_context",
            "turn_aborted", "subagent_notification", "task_notification", "tool_result", "tool_response",
            "function_result", "forwarded_tool_output", "collaboration_mode", "multi_agent_role"
        };

        public ConversationBridge(string codexHome)
        {
            if (String.IsNullOrWhiteSpace(codexHome)) codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (String.IsNullOrWhiteSpace(codexHome))
            {
                codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            }
            sessionsRoot = Path.Combine(Path.GetFullPath(codexHome), "sessions");
        }

        public ConversationSnapshot Read(string threadId)
        {
            lock (gate)
            {
                ConversationSnapshot result = new ConversationSnapshot { ThreadId = threadId, Messages = new List<ConversationMessage>() };
                if (!SafeThreadId(threadId)) { result.Notice = "尚未选择可读取的对话。"; return result; }
                bool changed = !String.Equals(lastRequestedThread, threadId, StringComparison.OrdinalIgnoreCase);
                if (changed) { current = null; lastRequestedThread = threadId; lastDiscoveryUtc = DateTime.MinValue; }
                if ((current == null || !File.Exists(current.Path)) && DateTime.UtcNow - lastDiscoveryUtc >= TimeSpan.FromSeconds(10))
                {
                    lastDiscoveryUtc = DateTime.UtcNow;
                    Cursor replacement = Find(threadId);
                    if (replacement != null) current = replacement;
                }
                if (current == null) { result.Notice = "尚未找到该对话的本地记录。"; return result; }
                try { ReadIncrement(current); }
                catch (IOException) { current.ReadFailed = true; }
                catch (UnauthorizedAccessException) { current.ReadFailed = true; }
                catch (ArgumentException) { current.ReadFailed = true; }
                foreach (Record record in current.Messages)
                    result.Messages.Add(new ConversationMessage { Id = record.Message.Id, Role = record.Message.Role,
                        Text = record.Message.Text, ObservedUtc = record.Message.ObservedUtc });
                result.ObservedUtc = result.Messages.Select(m => m.ObservedUtc).Where(t => t.HasValue).DefaultIfEmpty().Max();
                result.Truncated = current.Truncated;
                result.Notice = current.ReadFailed ? "本地记录暂时无法读取，显示上次内容。"
                    : current.Truncated ? "本地记录：仅显示最近的对话内容。" : "本地对话记录，约每秒更新；不是模型实时流。";
                return result;
            }
        }

        Cursor Find(string threadId)
        {
            if (!Directory.Exists(sessionsRoot)) return null;
            try
            {
                foreach (string path in Directory.EnumerateFiles(sessionsRoot, "rollout-*" + threadId + "*.jsonl", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc))
                {
                    using (FileStream stream = Open(path))
                    {
                        var line = new List<byte>(); int value;
                        while (line.Count < 2 * 1024 * 1024 && (value = stream.ReadByte()) >= 0 && value != 10) line.Add((byte)value);
                        Dictionary<string, object> root = Parse(Encoding.UTF8.GetString(line.ToArray()));
                        var payload = Object(root, "payload");
                        if (Text(root, "type") != "session_meta" || Text(payload, "id") != threadId) continue;
                        object source; bool child = !String.IsNullOrEmpty(Text(payload, "parent_thread_id"))
                            || (payload != null && payload.TryGetValue("source", out source) && source is Dictionary<string, object>);
                        if (child) continue;
                        return new Cursor { Path = path, CreatedUtc = File.GetCreationTimeUtc(path) };
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        void ReadIncrement(Cursor cursor)
        {
            using (FileStream input = Open(cursor.Path))
            {
                long length = input.Length;
                DateTime created = File.GetCreationTimeUtc(cursor.Path), written = File.GetLastWriteTimeUtc(cursor.Path);
                if (cursor.Initialized && (length < cursor.Offset || created != cursor.CreatedUtc
                    || (length == cursor.Offset && written != cursor.LastWrittenUtc))) cursor.Reset();
                if (!cursor.Initialized)
                {
                    cursor.CreatedUtc = created;
                    cursor.Offset = Math.Max(0, length - InitialTailBytes);
                    cursor.SkipLine = cursor.Offset > 0;
                    cursor.Truncated = cursor.SkipLine;
                    cursor.Initialized = true;
                }
                input.Position = cursor.Offset;
                byte[] buffer = new byte[16384];
                int left = cursor.Offset == 0 || cursor.Offset == Math.Max(0, length - InitialTailBytes)
                    ? InitialTailBytes : IncrementalReadBytes;
                int read;
                while (left > 0 && (read = input.Read(buffer, 0, Math.Min(buffer.Length, left))) > 0)
                {
                    long start = cursor.Offset; cursor.Offset += read; left -= read;
                    for (int i = 0; i < read; i++)
                    {
                        byte value = buffer[i];
                        if (value == 10)
                        {
                            if (!cursor.SkipLine && cursor.Pending.Count > 0)
                                Apply(cursor, Encoding.UTF8.GetString(cursor.Pending.ToArray()), start + i);
                            cursor.Pending.Clear(); cursor.SkipLine = false;
                        }
                        else if (!cursor.SkipLine)
                        {
                            if (cursor.Pending.Count < MaxLineBytes) cursor.Pending.Add(value);
                            else { cursor.Pending.Clear(); cursor.SkipLine = true; cursor.Truncated = true; }
                        }
                    }
                }
                cursor.LastWrittenUtc = written; cursor.ReadFailed = false;
            }
        }

        void Apply(Cursor cursor, string line, long endOffset)
        {
            Dictionary<string, object> root = Parse(line), payload = Object(root, "payload");
            string type = Text(root, "type");
            if (payload == null || (type != "response_item" && type != "event_msg")) return;
            string role = null, body = null, origin = null;
            if (type == "response_item")
            {
                if (Text(payload, "type") != "message") return;
                role = Text(payload, "role");
                if (role != "user" && role != "assistant") return;
                string phase = Text(payload, "phase"), channel = Text(payload, "channel");
                if (role == "assistant" && (!VisiblePhase(phase) || !VisiblePhase(channel))) return;
                object content;
                if (!payload.TryGetValue("content", out content) || !(content is IEnumerable) || content is string) return;
                var parts = new List<string>();
                foreach (object value in (IEnumerable)content)
                {
                    var part = value as Dictionary<string, object>;
                    string kind = Text(part, "type");
                    if (kind == "input_text" || kind == "output_text" || kind == "text")
                    { string text = Text(part, "text"); if (!String.IsNullOrEmpty(text)) parts.Add(text); }
                }
                body = String.Join("\n", parts); origin = "canonical";
            }
            else
            {
                string eventType = Text(payload, "type");
                if (eventType == "user_message") role = "user";
                else if (eventType == "agent_message") role = "assistant";
                else return;
                if (role == "assistant" && (!VisiblePhase(Text(payload, "phase")) || !VisiblePhase(Text(payload, "channel")))) return;
                body = Text(payload, "message"); origin = "event";
            }
            body = Clean(body);
            if (String.IsNullOrWhiteSpace(body)) return;
            if (Encoding.UTF8.GetByteCount(body) > MaxMessageBytes) { body = FitUtf8(body, MaxMessageBytes); cursor.Truncated = true; }
            DateTime parsed; DateTime? stamp = DateTime.TryParse(Text(root, "timestamp"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed) ? (DateTime?)parsed : null;
            string id = Text(payload, "id");
            if (String.IsNullOrEmpty(id)) id = "local-" + endOffset.ToString(CultureInfo.InvariantCulture);
            Record record = new Record { Origin = origin, Bytes = Encoding.UTF8.GetByteCount(body),
                Message = new ConversationMessage { Id = id, Role = role, Text = body, ObservedUtc = stamp } };
            if (cursor.Messages.Any(r => r.Origin == "canonical" && origin == "canonical" && r.Message.Id == id)) return;
            for (int i = cursor.Messages.Count - 1; i >= Math.Max(0, cursor.Messages.Count - 4); i--)
            {
                Record previous = cursor.Messages[i];
                if (previous.Origin == origin || previous.Message.Role != role || previous.Message.Text != body) continue;
                bool close = previous.Message.ObservedUtc.HasValue && stamp.HasValue
                    ? Math.Abs((previous.Message.ObservedUtc.Value - stamp.Value).TotalSeconds) <= 2 : i == cursor.Messages.Count - 1;
                if (!close) continue;
                if (origin == "canonical") { cursor.TotalBytes += record.Bytes - previous.Bytes; cursor.Messages[i] = record; }
                return;
            }
            cursor.Messages.Add(record); cursor.TotalBytes += record.Bytes;
            while (cursor.Messages.Count > MaxMessages || cursor.TotalBytes > MaxTextBytes)
            { cursor.TotalBytes -= cursor.Messages[0].Bytes; cursor.Messages.RemoveAt(0); cursor.Truncated = true; }
        }

        public static string CleanVisibleText(string text) { return Clean(text); }
        static string Clean(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return null;
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            foreach (string tag in HiddenTags) text = RemoveTag(text, tag);
            const string replyTag = "send_user_message_question_reply";
            int reply = text.IndexOf("<" + replyTag + ">", StringComparison.OrdinalIgnoreCase);
            while (reply >= 0)
            {
                int end = text.IndexOf("</" + replyTag + ">", reply, StringComparison.OrdinalIgnoreCase);
                if (end < 0) { text = text.Substring(0, reply); break; }
                string answers = Answers(text.Substring(reply + replyTag.Length + 2, end - reply - replyTag.Length - 2));
                text = text.Substring(0, reply) + answers + text.Substring(end + replyTag.Length + 3);
                reply = text.IndexOf("<" + replyTag + ">", StringComparison.OrdinalIgnoreCase);
            }
            text = text.Trim();
            if (text.StartsWith("Message Type:", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("# AGENTS.md instructions", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("<tool_call", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("<function_call", StringComparison.OrdinalIgnoreCase)) return null;
            return text.Length == 0 ? null : text;
        }
        static string RemoveTag(string text, string tag)
        {
            string open = "<" + tag, close = "</" + tag + ">";
            int start = 0;
            while ((start = text.IndexOf(open, start, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int after = start + open.Length;
                if (after < text.Length && text[after] != '>' && text[after] != '/' && !Char.IsWhiteSpace(text[after])) { start = after; continue; }
                int openEnd = text.IndexOf('>', after);
                if (openEnd < 0) return text.Substring(0, start);
                int end = text.IndexOf(close, openEnd + 1, StringComparison.OrdinalIgnoreCase);
                if (text[openEnd - 1] == '/') text = text.Remove(start, openEnd - start + 1);
                else if (end < 0) return text.Substring(0, start);
                else text = text.Remove(start, end + close.Length - start);
            }
            return text;
        }
        static string Answers(string raw)
        {
            try
            {
                object parsed = new JavaScriptSerializer { MaxJsonLength = MaxLineBytes, RecursionLimit = 48 }.DeserializeObject(raw);
                var answers = new List<string>();
                if (parsed is IEnumerable && !(parsed is string)) foreach (object item in (IEnumerable)parsed)
                { string answer = Text(item as Dictionary<string, object>, "answer"); if (!String.IsNullOrEmpty(answer)) answers.Add(answer); }
                return String.Join("\n", answers);
            }
            catch (ArgumentException) { return ""; }
            catch (InvalidOperationException) { return ""; }
        }
        static bool VisiblePhase(string value)
        { return String.IsNullOrEmpty(value) || value == "commentary" || value == "final_answer" || value == "final" || value == "answer"; }
        static bool SafeThreadId(string value)
        { return !String.IsNullOrWhiteSpace(value) && value.Length <= 160 && value.All(c => Char.IsLetterOrDigit(c) || c == '-' || c == '_'); }
        static FileStream Open(string path) { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        Dictionary<string, object> Parse(string raw)
        {
            try { return json.DeserializeObject(raw) as Dictionary<string, object>; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }
        static string Text(Dictionary<string, object> value, string key)
        { object raw; return value != null && value.TryGetValue(key, out raw) ? raw as string : null; }
        static Dictionary<string, object> Object(Dictionary<string, object> value, string key)
        { object raw; return value != null && value.TryGetValue(key, out raw) ? raw as Dictionary<string, object> : null; }
        static string FitUtf8(string value, int maximum)
        {
            int low = 0, high = Math.Min(value.Length, maximum);
            while (low < high) { int mid = (low + high + 1) / 2; if (Encoding.UTF8.GetByteCount(value.Substring(0, mid)) <= maximum - 4) low = mid; else high = mid - 1; }
            if (low > 0 && Char.IsHighSurrogate(value[low - 1])) low--;
            return value.Substring(0, low) + "\n…";
        }
        sealed class Record { public ConversationMessage Message; public string Origin; public int Bytes; }
        sealed class Cursor
        {
            public string Path; public long Offset; public bool Initialized, SkipLine, Truncated, ReadFailed;
            public DateTime CreatedUtc, LastWrittenUtc; public int TotalBytes;
            public readonly List<byte> Pending = new List<byte>();
            public readonly List<Record> Messages = new List<Record>();
            public void Reset() { Offset = TotalBytes = 0; Initialized = SkipLine = Truncated = ReadFailed = false; Pending.Clear(); Messages.Clear(); }
        }
    }
}
