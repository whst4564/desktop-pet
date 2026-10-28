// Read-only, best-effort bridge for the local Codex session log format.
// Requires .NET Framework 4.x and System.Web.Extensions; C# 5 compatible.
// No network, credential access, client changes, or message persistence.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexCompanion
{
    public enum CompanionWorkState { Unknown, Working, Idle, Interrupted }

    public sealed class UsageWindow
    {
        public double? UsedPercent { get; internal set; }
        public int? WindowMinutes { get; internal set; }
        public DateTime? ResetsAtUtc { get; internal set; }
        public bool IsExpired { get { return ResetsAtUtc.HasValue && ResetsAtUtc.Value <= DateTime.UtcNow; } }
        // Once a reset has passed, a new sample is needed. Do not invent 100%.
        public double? RemainingPercent
        {
            get { return !IsExpired && UsedPercent.HasValue ? 100.0 - UsedPercent.Value : (double?)null; }
        }
    }

    public sealed class CompanionSnapshot
    {
        public string Model { get; internal set; }
        public string Effort { get; internal set; }
        public string SourceThreadId { get; internal set; }
        public string SelectionMode { get; internal set; }
        public CompanionWorkState State { get; internal set; }
        public UsageWindow Primary { get; internal set; }
        public UsageWindow Secondary { get; internal set; }
        public DateTime? ModelObservedUtc { get; internal set; }
        public DateTime? LimitsObservedUtc { get; internal set; }
        public DateTime? StateObservedUtc { get; internal set; }
        public DateTime? LastUpdatedUtc { get; internal set; }
        public DateTime LastPolledUtc { get; internal set; }
        public bool StateIsStale { get; internal set; }
        public string Notice { get; internal set; }
        public string LimitsSource { get; internal set; }
        public string ConnectionNotice { get; internal set; }
        public bool? OverlayVisible { get; internal set; }
        public bool OverlayReadSucceeded { get; internal set; }
        public DateTime? OverlayReadUtc { get; internal set; }
        public CompanionOverlayBounds OverlayBounds { get; internal set; }
        // No unit is assumed. PetSizeKey identifies the source key if present.
        public double? PetSize { get; internal set; }
        public string PetSizeKey { get; internal set; }
    }

    public sealed class CompanionOverlayBounds
    {
        public double? X { get; internal set; }
        public double? Y { get; internal set; }
        public double? Width { get; internal set; }
        public double? Height { get; internal set; }
        public double? DisplayX { get; internal set; }
        public double? DisplayY { get; internal set; }
        public double? DisplayWidth { get; internal set; }
        public double? DisplayHeight { get; internal set; }
    }

    /// <summary>
    /// Poll on a background thread every 2-5 seconds. The returned object is a
    /// detached snapshot. Model means "last recorded model for this task";
    /// it does not mean the currently selected model in the Codex UI.
    /// By default the most recently active non-subagent desktop session wins.
    /// Set PreferredThreadId to bind a specific session instead.
    /// </summary>
    public sealed class DataBridge
    {
        const int MaxTrackedFiles = 48;
        const int InitialTailBytes = 512 * 1024;
        const int IncrementalReadBytes = 512 * 1024;
        const int MaxLineBytes = 2 * 1024 * 1024;
        readonly object gate = new object();
        readonly string sessionsRoot;
        readonly Dictionary<string, LogCursor> files = new Dictionary<string, LogCursor>(StringComparer.OrdinalIgnoreCase);
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = MaxLineBytes, RecursionLimit = 40 };
        DateTime lastDiscoveryUtc = DateTime.MinValue;
        string discoveryError;

        public string PreferredThreadId { get; set; }
        public TimeSpan StateFreshness { get; set; }
        public string CodexHome { get; private set; }

        public DataBridge() : this(null, null) { }
        public DataBridge(string codexHome, string preferredThreadId)
        {
            CodexHome = ResolveHome(codexHome);
            sessionsRoot = Path.Combine(CodexHome, "sessions");
            PreferredThreadId = preferredThreadId;
            StateFreshness = TimeSpan.FromMinutes(20);
        }

        static string ResolveHome(string explicitHome)
        {
            if (!String.IsNullOrWhiteSpace(explicitHome)) return Path.GetFullPath(explicitHome);
            string env = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!String.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            return local;
        }

        public CompanionSnapshot Poll()
        {
            lock (gate)
            {
                DateTime now = DateTime.UtcNow;
                if ((now - lastDiscoveryUtc).TotalSeconds >= 10) Discover(now);
                foreach (LogCursor cursor in files.Values) ReadIncrement(cursor);

                IEnumerable<LogCursor> candidates = files.Values.Where(delegate(LogCursor c) { return c.IsDesktopRoot; });
                if (!String.IsNullOrWhiteSpace(PreferredThreadId))
                    candidates = candidates.Where(delegate(LogCursor c) { return String.Equals(c.ThreadId, PreferredThreadId, StringComparison.OrdinalIgnoreCase); });
                LogCursor selected = candidates.OrderByDescending(delegate(LogCursor c) { return c.ActivityUtc; }).FirstOrDefault();
                if (selected != null && !selected.Bootstrapped)
                {
                    BootstrapRecentMetadata(selected);
                    selected.Bootstrapped = true;
                }
                // Account quota is shared. Use the latest observed Codex quota
                // from these local log files, including background tasks.
                LogCursor quota = files.Values.Where(delegate(LogCursor c) { return c.LimitsUtc.HasValue; })
                    .OrderByDescending(delegate(LogCursor c) { return c.LimitsUtc; }).FirstOrDefault();
                CompanionSnapshot result = new CompanionSnapshot();
                result.LastPolledUtc = now;
                result.SelectionMode = String.IsNullOrWhiteSpace(PreferredThreadId) ? "latest-recorded-desktop-task" : "bound-thread";
                result.State = CompanionWorkState.Unknown;
                result.Notice = discoveryError;
                if (selected != null)
                {
                    result.Model = selected.Model;
                    result.Effort = selected.Effort;
                    result.SourceThreadId = selected.ThreadId;
                    result.ModelObservedUtc = selected.ModelUtc;
                    result.StateObservedUtc = selected.StateUtc;
                    result.LastUpdatedUtc = selected.ActivityUtc == DateTime.MinValue ? (DateTime?)null : selected.ActivityUtc;
                    result.StateIsStale = !selected.ActivityUtc.Equals(DateTime.MinValue) && now - selected.ActivityUtc > StateFreshness;
                    result.State = result.StateIsStale ? CompanionWorkState.Unknown : selected.State;
                    if (selected.ReadFailed) result.Notice = "最近一次日志读取失败，显示最后记录。";
                }
                else if (String.IsNullOrEmpty(result.Notice)) result.Notice = "尚未找到可读取的桌面任务记录。";
                if (quota != null)
                {
                    result.Primary = CopyWindow(quota.Primary);
                    result.Secondary = CopyWindow(quota.Secondary);
                    result.LimitsObservedUtc = quota.LimitsUtc;
                    if (!result.LastUpdatedUtc.HasValue || quota.LimitsUtc > result.LastUpdatedUtc)
                        result.LastUpdatedUtc = quota.LimitsUtc;
                }
                ReadOverlay(result, now);
                return result;
            }
        }

        void ReadOverlay(CompanionSnapshot result, DateTime now)
        {
            string path = Path.Combine(CodexHome, ".codex-global-state.json");
            try
            {
                string raw;
                using (FileStream input = OpenLog(path))
                {
                    if (input.Length > MaxLineBytes) return;
                    using (StreamReader reader = new StreamReader(input, Encoding.UTF8, true)) raw = reader.ReadToEnd();
                }
                int end = 0;
                SkipWhite(raw, ref end);
                if (end >= raw.Length || raw[end] != '{' || !SkipValue(raw, ref end)) return;
                SkipWhite(raw, ref end);
                if (end != raw.Length) return;
                string[] sizeKeys = new string[] { "electron-avatar-overlay-size", "electron-avatar-size", "avatar-overlay-size", "pet-size" };
                string[] keys = new string[] { "electron-avatar-overlay-open", "electron-avatar-overlay-bounds", "electron-avatar-overlay-size", "electron-avatar-size", "avatar-overlay-size", "pet-size", "electron-persisted-atom-state" };
                Dictionary<string, string> selected = Project(raw, keys);
                string atoms=Get(selected,"electron-persisted-atom-state");
                if(!String.IsNullOrEmpty(atoms)){
                    var petSettings=Project(atoms,new[]{"avatar-overlay-mascot-width-px"});
                    double? nativeWidth=Number(Get(petSettings,"avatar-overlay-mascot-width-px"));
                    if(nativeWidth.HasValue&&nativeWidth.Value>=80&&nativeWidth.Value<=224){result.PetSize=nativeWidth;result.PetSizeKey="avatar-overlay-mascot-width-px";}
                }
                string visible = Get(selected, "electron-avatar-overlay-open");
                if (visible == "true") result.OverlayVisible = true;
                else if (visible == "false") result.OverlayVisible = false;
                string bounds = Get(selected, "electron-avatar-overlay-bounds");
                if (!String.IsNullOrEmpty(bounds) && bounds != "null")
                {
                    Dictionary<string, string> b = Project(bounds, new string[] { "x", "y", "width", "height", "displayBounds" });
                    CompanionOverlayBounds value = new CompanionOverlayBounds();
                    value.X = Number(Get(b, "x")); value.Y = Number(Get(b, "y"));
                    value.Width = Number(Get(b, "width")); value.Height = Number(Get(b, "height"));
                    string display = Get(b, "displayBounds");
                    if (!String.IsNullOrEmpty(display) && display != "null")
                    {
                        Dictionary<string, string> d = Project(display, new string[] { "x", "y", "width", "height" });
                        value.DisplayX = Number(Get(d, "x")); value.DisplayY = Number(Get(d, "y"));
                        value.DisplayWidth = Number(Get(d, "width")); value.DisplayHeight = Number(Get(d, "height"));
                    }
                    result.OverlayBounds = value;
                }
                foreach (string key in sizeKeys)
                {
                    double? value = Number(Get(selected, key));
                    if (!result.PetSize.HasValue && value.HasValue) { result.PetSize = value; result.PetSizeKey = key; break; }
                }
                result.OverlayReadSucceeded = true;
                result.OverlayReadUtc = now;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        static double? Number(string raw)
        {
            double number;
            return Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && !Double.IsNaN(number) && !Double.IsInfinity(number) ? number : (double?)null;
        }

        static UsageWindow CopyWindow(UsageWindow value)
        {
            return value == null ? null : new UsageWindow { UsedPercent = value.UsedPercent, WindowMinutes = value.WindowMinutes, ResetsAtUtc = value.ResetsAtUtc };
        }

        void Discover(DateTime now)
        {
            lastDiscoveryUtc = now;
            discoveryError = null;
            if (!Directory.Exists(sessionsRoot)) { discoveryError = "未找到本机 Codex 会话日志。"; return; }
            try
            {
                List<FileInfo> all = new DirectoryInfo(sessionsRoot).EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                    .Where(delegate(FileInfo f) { return f.Name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase); })
                    .OrderByDescending(delegate(FileInfo f) { return f.LastWriteTimeUtc; }).ToList();
                List<FileInfo> recent = all.Take(MaxTrackedFiles).ToList();
                if (!String.IsNullOrWhiteSpace(PreferredThreadId))
                {
                    FileInfo bound = all.FirstOrDefault(delegate(FileInfo f) { return f.Name.IndexOf(PreferredThreadId, StringComparison.OrdinalIgnoreCase) >= 0; });
                    if (bound != null && !recent.Any(delegate(FileInfo f) { return f.FullName == bound.FullName; })) recent.Add(bound);
                }
                foreach (FileInfo file in recent)
                {
                    if (files.ContainsKey(file.FullName)) continue;
                    LogCursor cursor = new LogCursor { Path = file.FullName };
                    ReadHeader(cursor);
                    files.Add(file.FullName, cursor);
                }
                // Preserve already observed active sessions, even if their
                // LastWriteTime is not updated while an OS handle is open.
                if (files.Count > MaxTrackedFiles * 2)
                {
                    List<LogCursor> keep = files.Values.OrderByDescending(delegate(LogCursor c) { return c.ActivityUtc; }).Take(MaxTrackedFiles).ToList();
                    foreach (string path in files.Keys.ToList())
                        if (!keep.Any(delegate(LogCursor c) { return c.Path == path; }) && !recent.Any(delegate(FileInfo f) { return f.FullName == path; })) files.Remove(path);
                }
            }
            catch (IOException) { discoveryError = "会话目录暂时不可读。"; }
            catch (UnauthorizedAccessException) { discoveryError = "无权读取本机 Codex 会话日志。"; }
        }

        FileStream OpenLog(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.SequentialScan);
        }

        // A long-running turn may place its turn_context several MB before
        // the tail. Once for the selected root only, look backwards in bounded
        // 512-KB blocks until both model and state are found (max 16 MB).
        // Subsequent polls read appended bytes only.
        void BootstrapRecentMetadata(LogCursor cursor)
        {
            if (cursor.ModelUtc.HasValue && cursor.StateUtc.HasValue) return;
            try
            {
                using (FileStream input = OpenLog(cursor.Path))
                {
                    long end = input.Length;
                    long earliest = Math.Max(0, end - 16 * 1024 * 1024);
                    byte[] carry = new byte[0];
                    while (end > earliest)
                    {
                        long start = Math.Max(earliest, end - InitialTailBytes);
                        int count = (int)(end - start);
                        byte[] data = new byte[count + carry.Length];
                        input.Position = start;
                        int done = 0;
                        int got;
                        while (done < count && (got = input.Read(data, done, count - done)) > 0) done += got;
                        if (done != count) return;
                        Buffer.BlockCopy(carry, 0, data, count, carry.Length);
                        int lineEnd = Array.LastIndexOf(data, (byte)10);
                        while (lineEnd >= 0)
                        {
                            int previous = lineEnd > 0 ? Array.LastIndexOf(data, (byte)10, lineEnd - 1) : -1;
                            if (previous < 0 && start > 0) break;
                            int lineStart = previous + 1;
                            int length = lineEnd - lineStart;
                            if (length > 0 && length <= MaxLineBytes)
                                ApplyLine(cursor, Encoding.UTF8.GetString(data, lineStart, length), false);
                            if (cursor.ModelUtc.HasValue && cursor.StateUtc.HasValue) return;
                            lineEnd = previous;
                        }
                        int carryLength = lineEnd + 1;
                        carry = carryLength <= MaxLineBytes ? new byte[carryLength] : new byte[0];
                        if (carry.Length > 0) Buffer.BlockCopy(data, 0, carry, 0, carry.Length);
                        end = start;
                    }
                }
            }
            catch (IOException) { cursor.ReadFailed = true; }
            catch (UnauthorizedAccessException) { cursor.ReadFailed = true; }
        }

        void ReadHeader(LogCursor cursor)
        {
            try
            {
                using (FileStream input = OpenLog(cursor.Path))
                {
                    List<byte> line = new List<byte>();
                    int b;
                    while (line.Count < MaxLineBytes && (b = input.ReadByte()) >= 0 && b != 10) line.Add((byte)b);
                    ApplyLine(cursor, Encoding.UTF8.GetString(line.ToArray()), true);
                }
            }
            catch (IOException) { cursor.ReadFailed = true; }
            catch (UnauthorizedAccessException) { cursor.ReadFailed = true; }
        }

        void ReadIncrement(LogCursor cursor)
        {
            try
            {
                using (FileStream input = OpenLog(cursor.Path))
                {
                    long length = input.Length;
                    if (cursor.Initialized && length < cursor.Offset)
                    {
                        cursor.Initialized = false;
                        cursor.Bootstrapped = false;
                        cursor.Model = null; cursor.Effort = null;
                        cursor.ModelUtc = null; cursor.StateUtc = null; cursor.LimitsUtc = null;
                        cursor.Primary = null; cursor.Secondary = null;
                        cursor.State = CompanionWorkState.Unknown;
                        cursor.ActivityUtc = DateTime.MinValue;
                        ReadHeader(cursor);
                    }
                    if (!cursor.Initialized || length < cursor.Offset)
                    {
                        cursor.Offset = Math.Max(0, length - InitialTailBytes);
                        cursor.SkipLine = cursor.Offset > 0;
                        cursor.Pending.Clear();
                        cursor.Initialized = true;
                    }
                    input.Position = cursor.Offset;
                    byte[] buffer = new byte[8192];
                    int left = IncrementalReadBytes;
                    int read;
                    while (left > 0 && (read = input.Read(buffer, 0, Math.Min(buffer.Length, left))) > 0)
                    {
                        cursor.Offset += read;
                        left -= read;
                        for (int i = 0; i < read; i++)
                        {
                            byte b = buffer[i];
                            if (b == 10)
                            {
                                if (!cursor.SkipLine && cursor.Pending.Count > 0)
                                    ApplyLine(cursor, Encoding.UTF8.GetString(cursor.Pending.ToArray()), false);
                                cursor.Pending.Clear();
                                cursor.SkipLine = false;
                            }
                            else if (!cursor.SkipLine)
                            {
                                if (cursor.Pending.Count < MaxLineBytes) cursor.Pending.Add(b);
                                else { cursor.Pending.Clear(); cursor.SkipLine = true; }
                            }
                        }
                    }
                }
                cursor.ReadFailed = false;
            }
            catch (IOException) { cursor.ReadFailed = true; }
            catch (UnauthorizedAccessException) { cursor.ReadFailed = true; }
        }

        void ApplyLine(LogCursor cursor, string line, bool headerOnly)
        {
            try
            {
                // Project before deserialization: message bodies, summaries,
                // instructions, last_agent_message, and all credentials are
                // skipped as raw JSON ranges and never decoded or retained.
                Dictionary<string, string> root = Project(line, new string[] { "type", "timestamp" });
                string type = DecodeString(Get(root, "type"));
                if (type != "session_meta" && type != "turn_context" && type != "event_msg") return;
                string payload = Get(Project(line, new string[] { "payload" }), "payload");
                if (String.IsNullOrEmpty(payload)) return;
                DateTime stamp;
                if (!DateTime.TryParse(DecodeString(Get(root, "timestamp")), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out stamp)) return;
                if (type == "session_meta")
                {
                    Dictionary<string, string> p = Project(payload, new string[] { "id", "originator", "source", "parent_thread_id" });
                    cursor.ThreadId = DecodeString(Get(p, "id"));
                    string originator = DecodeString(Get(p, "originator"));
                    string source = Get(p, "source");
                    bool child = !String.IsNullOrEmpty(DecodeString(Get(p, "parent_thread_id"))) || (!String.IsNullOrEmpty(source) && source.TrimStart().StartsWith("{", StringComparison.Ordinal));
                    cursor.IsDesktopRoot = !child && String.Equals(originator, "Codex Desktop", StringComparison.OrdinalIgnoreCase);
                    cursor.ActivityUtc = Later(cursor.ActivityUtc, stamp);
                    return;
                }
                if (headerOnly) return;
                if (type == "turn_context")
                {
                    Dictionary<string, string> p = Project(payload, new string[] { "model", "effort", "reasoning_effort" });
                    if (!cursor.ModelUtc.HasValue || stamp >= cursor.ModelUtc.Value)
                    {
                        cursor.Model = DecodeString(Get(p, "model"));
                        cursor.Effort = DecodeString(Get(p, "effort")) ?? DecodeString(Get(p, "reasoning_effort"));
                        cursor.ModelUtc = stamp;
                    }
                    cursor.ActivityUtc = Later(cursor.ActivityUtc, stamp);
                    return;
                }
                Dictionary<string, string> fields = Project(payload, new string[] { "type", "rate_limits" });
                string eventType = DecodeString(Get(fields, "type"));
                if (eventType == "token_count")
                {
                    string limits = Get(fields, "rate_limits");
                    if (!String.IsNullOrEmpty(limits) && limits != "null" && (!cursor.LimitsUtc.HasValue || stamp >= cursor.LimitsUtc.Value))
                    {
                        Dictionary<string, string> windows = Project(limits, new string[] { "limit_id", "primary", "secondary" });
                        string limitId = DecodeString(Get(windows, "limit_id"));
                        if (String.IsNullOrEmpty(limitId) || limitId == "codex")
                        {
                            cursor.Primary = DecodeWindow(Get(windows, "primary"));
                            cursor.Secondary = DecodeWindow(Get(windows, "secondary"));
                            if (cursor.Primary != null || cursor.Secondary != null) cursor.LimitsUtc = stamp;
                        }
                    }
                    cursor.ActivityUtc = Later(cursor.ActivityUtc, stamp);
                }
                else if (eventType == "task_started" || eventType == "task_complete" || eventType == "turn_aborted")
                {
                    if (!cursor.StateUtc.HasValue || stamp >= cursor.StateUtc.Value)
                    {
                        cursor.State = eventType == "task_started" ? CompanionWorkState.Working : eventType == "task_complete" ? CompanionWorkState.Idle : CompanionWorkState.Interrupted;
                        cursor.StateUtc = stamp;
                    }
                    cursor.ActivityUtc = Later(cursor.ActivityUtc, stamp);
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (FormatException) { }
            catch (OverflowException) { }
        }

        UsageWindow DecodeWindow(string raw)
        {
            if (String.IsNullOrEmpty(raw) || raw == "null") return null;
            Dictionary<string, string> f = Project(raw, new string[] { "used_percent", "window_minutes", "resets_at" });
            UsageWindow value = new UsageWindow();
            double percent;
            int minutes;
            long reset;
            if (Double.TryParse(Get(f, "used_percent"), NumberStyles.Float, CultureInfo.InvariantCulture, out percent) && !Double.IsNaN(percent) && percent >= 0 && percent <= 100) value.UsedPercent = percent;
            if (Int32.TryParse(Get(f, "window_minutes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes) && minutes > 0) value.WindowMinutes = minutes;
            if (Int64.TryParse(Get(f, "resets_at"), NumberStyles.Integer, CultureInfo.InvariantCulture, out reset) && reset > 0)
            {
                try { value.ResetsAtUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(reset); }
                catch (ArgumentOutOfRangeException) { }
            }
            return value.UsedPercent.HasValue || value.ResetsAtUtc.HasValue ? value : null;
        }

        string DecodeString(string raw)
        {
            if (String.IsNullOrEmpty(raw) || raw == "null") return null;
            if (raw.Length == 0 || raw[0] != '"') return null;
            return json.Deserialize<string>(raw);
        }

        static DateTime Later(DateTime a, DateTime b) { return a >= b ? a : b; }
        static string Get(Dictionary<string, string> values, string key) { string v; return values.TryGetValue(key, out v) ? v : null; }

        // A structural JSON scanner. Only allowlisted direct fields are copied;
        // other field values are skipped without deserializing their contents.
        Dictionary<string, string> Project(string text, string[] allowed)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (String.IsNullOrEmpty(text)) return result;
            int i = 0;
            SkipWhite(text, ref i);
            if (i >= text.Length || text[i++] != '{') return result;
            while (i < text.Length)
            {
                SkipWhite(text, ref i);
                if (i >= text.Length || text[i] == '}') return result;
                int keyStart = i;
                if (text[i] != '"' || !SkipString(text, ref i)) return result;
                string key = json.Deserialize<string>(text.Substring(keyStart, i - keyStart));
                SkipWhite(text, ref i);
                if (i >= text.Length || text[i++] != ':') return result;
                SkipWhite(text, ref i);
                int start = i;
                if (!SkipValue(text, ref i)) return result;
                if (Array.IndexOf(allowed, key) >= 0) result[key] = text.Substring(start, i - start);
                SkipWhite(text, ref i);
                if (i >= text.Length || text[i] == '}') return result;
                if (text[i++] != ',') return result;
            }
            return result;
        }

        static void SkipWhite(string s, ref int i) { while (i < s.Length && Char.IsWhiteSpace(s[i])) i++; }
        static bool SkipString(string s, ref int i)
        {
            if (i >= s.Length || s[i++] != '"') return false;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return true;
                if (c == '\\') { if (i >= s.Length) return false; i++; }
            }
            return false;
        }
        static bool SkipValue(string s, ref int i)
        {
            if (i >= s.Length) return false;
            if (s[i] == '"') return SkipString(s, ref i);
            if (s[i] == '{' || s[i] == '[')
            {
                int depth = 0;
                do
                {
                    char c = s[i];
                    if (c == '"') { if (!SkipString(s, ref i)) return false; continue; }
                    if (c == '{' || c == '[') depth++;
                    if (c == '}' || c == ']') depth--;
                    i++;
                    if (depth > 128) return false;
                } while (i < s.Length && depth > 0);
                return depth == 0;
            }
            int start = i;
            while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']' && !Char.IsWhiteSpace(s[i])) i++;
            return i > start;
        }

        sealed class LogCursor
        {
            public string Path, ThreadId, Model, Effort;
            public bool IsDesktopRoot, Initialized, SkipLine, ReadFailed, Bootstrapped;
            public long Offset;
            public List<byte> Pending = new List<byte>();
            public DateTime ActivityUtc = DateTime.MinValue;
            public DateTime? ModelUtc, LimitsUtc, StateUtc;
            public UsageWindow Primary, Secondary;
            public CompanionWorkState State = CompanionWorkState.Unknown;
        }
    }
}
