using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

namespace DeskPlanner
{
    [DataContract]
    public sealed class TaskItem
    {
        public const string DailyScope = "daily", WeeklyScope = "weekly";
        [DataMember(Order = 0)] public string Id { get; set; }
        [DataMember(Order = 1)] public string Title { get; set; }
        [DataMember(Order = 2)] public string Notes { get; set; }
        [DataMember(Order = 3)] public string Date { get; set; }
        [DataMember(Order = 4)] public bool Completed { get; set; }
        [DataMember(Order = 5)] public string CreatedAt { get; set; }
        [DataMember(Order = 6)] public string CompletedAt { get; set; }
        [DataMember(Order = 7)] public string Scope { get; set; }
        [DataMember(Order = 8, EmitDefaultValue = false)] public int Progress { get; set; }
        public bool IsWeekly { get { return Scope == WeeklyScope; } }
        public int ProgressPercent { get { return Completed ? 100 : Progress; } }

        public TaskItem()
        {
            Notes = String.Empty;
            CreatedAt = String.Empty;
            CompletedAt = String.Empty;
            Scope = DailyScope;
        }
    }

    [DataContract]
    public sealed class JournalEntry
    {
        [DataMember(Order = 0)] public string Date { get; set; }
        [DataMember(Order = 1)] public string Content { get; set; }
        [DataMember(Order = 2)] public string UpdatedAt { get; set; }
    }

    public sealed class JournalPage
    {
        public List<JournalEntry> Entries { get; internal set; }
        public int TotalCount { get; internal set; }
        public int PageIndex { get; internal set; }
        public int PageCount { get; internal set; }
    }

    public static class JournalBrowse
    {
        public const int PageSize = 8;

        public static JournalPage FindPage(IEnumerable<JournalEntry> source, string query, string month, int page)
        {
            string keyword = (query ?? "").Trim(), period = month ?? "";
            var matches = new List<JournalEntry>();
            foreach (JournalEntry entry in source)
            {
                if (period.Length > 0 && !entry.Date.StartsWith(period + "-", StringComparison.Ordinal)) continue;
                string displayDate = entry.Date.Replace("-0", "/").Replace("-", "/");
                if (keyword.Length > 0 && entry.Content.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0
                    && entry.Date.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0
                    && displayDate.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0) continue;
                matches.Add(entry);
            }
            matches.Sort(delegate(JournalEntry first, JournalEntry second) { return String.CompareOrdinal(second.Date, first.Date); });
            int pages = (matches.Count + PageSize - 1) / PageSize;
            int index = Math.Max(0, Math.Min(page, Math.Max(0, pages - 1)));
            return new JournalPage { TotalCount = matches.Count, PageIndex = index, PageCount = pages,
                Entries = matches.GetRange(index * PageSize, Math.Min(PageSize, matches.Count - index * PageSize)) };
        }

        public static string Excerpt(string content)
        {
            var text = new System.Text.StringBuilder();
            bool space = false;
            foreach (char letter in content)
            {
                if (Char.IsWhiteSpace(letter)) { space = text.Length > 0; continue; }
                if (space) { text.Append(' '); space = false; }
                text.Append(letter);
                if (text.Length >= 160 && !Char.IsHighSurrogate(letter)) return text.ToString() + "…";
            }
            return text.ToString();
        }

        public static string Export(IEnumerable<JournalEntry> source)
        {
            var entries = new List<JournalEntry>(source);
            entries.Sort(delegate(JournalEntry first, JournalEntry second) { return String.CompareOrdinal(first.Date, second.Date); });
            var text = new System.Text.StringBuilder("# 每日小记\r\n\r\n");
            foreach (JournalEntry entry in entries) text.Append("## ").Append(entry.Date).Append("\r\n\r\n").Append(entry.Content).Append("\r\n\r\n");
            return text.ToString();
        }
    }

    public static class PlannerDates
    {
        public static DateTime StartOfWeek(DateTime date)
        {
            return date.Date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        }

        public static DateTime ShiftDays(DateTime date, int days)
        {
            if (days > 0 && date.Date > DateTime.MaxValue.Date.AddDays(-days)) return DateTime.MaxValue.Date;
            if (days < 0 && date.Date < DateTime.MinValue.Date.AddDays(-days)) return DateTime.MinValue.Date;
            return date.Date.AddDays(days);
        }

        public static DateTime EndOfWeek(DateTime date) { return ShiftDays(StartOfWeek(date), 6); }
    }

    [DataContract]
    public sealed class PlannerSettings
    {
        [DataMember(Order = 0)] public bool AlwaysOnTop { get; set; }
        [DataMember(Order = 1)] public double Opacity { get; set; }
        [DataMember(Order = 2)] public double Left { get; set; }
        [DataMember(Order = 3)] public double Top { get; set; }
        [DataMember(Order = 4)] public bool AutoStart { get; set; }
        [DataMember(Order = 5)] public bool CarryOver { get; set; }
        [DataMember(Order = 6)] public double Width { get; set; }
        [DataMember(Order = 7)] public double Height { get; set; }
        [DataMember(Order = 8)] public string AppTitle { get; set; }
        [DataMember(Order = 9)] public string Subtitle { get; set; }
        [DataMember(Order = 10)] public string TodayLabel { get; set; }
        [DataMember(Order = 11)] public string TomorrowLabel { get; set; }
        [DataMember(Order = 12)] public string CalendarLabel { get; set; }
        [DataMember(Order = 13)] public string BackgroundImage { get; set; }
        [DataMember(Order = 14)] public double BackgroundOpacity { get; set; }
        [DataMember(Order = 15)] public string ViewMode { get; set; }
        [DataMember(Order = 16)] public bool StackedLayout { get; set; }

        public PlannerSettings() { SetDefaults(); }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context) { SetDefaults(); }

        private void SetDefaults()
        {
            AlwaysOnTop = false;
            Opacity = 0.96;
            Left = -1;
            Top = -1;
            AutoStart = false;
            CarryOver = false;
            Width = 432;
            Height = 920;
            AppTitle = "每日计划";
            Subtitle = "把一天，安排得刚刚好。";
            TodayLabel = "今天";
            TomorrowLabel = "明天";
            CalendarLabel = "选日期";
            BackgroundImage = String.Empty;
            BackgroundOpacity = 0.22;
            ViewMode = TaskItem.DailyScope;
        }
    }

    [DataContract]
    public sealed class PlannerData
    {
        [DataMember(Order = 0)] public int Version { get; set; }
        [DataMember(Order = 1)] public List<TaskItem> Tasks { get; set; }
        [DataMember(Order = 2)] public PlannerSettings Settings { get; set; }
        [DataMember(Order = 3)] public List<JournalEntry> Journal { get; set; }

        public PlannerData() { SetDefaults(); }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context) { SetDefaults(); }

        private void SetDefaults()
        {
            Version = 2;
            Tasks = new List<TaskItem>();
            Settings = new PlannerSettings();
            Journal = new List<JournalEntry>();
        }
    }

    /// <summary>
    /// Keeps every dated task, including completed history, in one local JSON file.
    /// Saves replace that file atomically and keep the previous valid file as .bak.
    /// </summary>
    public sealed class PlannerStore
    {
        public const int MaxTitleLength = 240;
        public const int MaxNotesLength = 8000;
        public const int MaxJournalLength = 8000;
        private const int MaxTaskCount = 100000;
        private const long MaxFileBytes = 128L * 1024L * 1024L;
        private readonly string dataPath;
        private readonly object gate = new object();
        private bool primaryExisted;
        private string primaryFingerprint;
        private bool primaryDamaged;
        private string pendingRecoveryCopy;

        public PlannerData Data { get; private set; }
        public string LoadWarning { get; private set; }
        public string RecoveryCopyPath { get; private set; }

        public PlannerStore(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentException("数据文件路径不能为空。", "path");
            dataPath = Path.GetFullPath(path);
            LoadWarning = String.Empty;
            Data = new PlannerData();
            primaryExisted = File.Exists(dataPath);

            if (primaryExisted)
            {
                // Read failures are surfaced; only malformed data triggers recovery.
                primaryFingerprint = Fingerprint(dataPath);
                try
                {
                    Data = ReadData(dataPath);
                    return;
                }
                catch (SerializationException) { primaryDamaged = true; }
                catch (InvalidDataException) { primaryDamaged = true; }
                catch (System.Xml.XmlException) { primaryDamaged = true; }
            }

            string backupPath = dataPath + ".bak";
            if (File.Exists(backupPath))
            {
                try
                {
                    Data = ReadData(backupPath);
                    LoadWarning = primaryDamaged
                        ? "计划数据文件损坏，已读取上次备份。损坏文件会在保存前另存保留。"
                        : "计划数据文件缺失，已读取上次备份。";
                    return;
                }
                catch (SerializationException) { }
                catch (InvalidDataException) { }
                catch (System.Xml.XmlException) { }
            }

            if (primaryDamaged)
                LoadWarning = "计划数据文件损坏，且没有可用备份。当前显示空计划；损坏文件会在保存前另存保留。";
            else if (File.Exists(backupPath))
                LoadWarning = "备份文件无法读取，当前显示空计划。原备份仍保留在磁盘上。";
        }

        public void Save()
        {
            lock (gate)
            {
                Validate(Data);
                CheckPrimaryUnchanged();
                string directory = Path.GetDirectoryName(dataPath);
                Directory.CreateDirectory(directory);
                string temporaryPath = dataPath + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None))
                    {
                        new DataContractJsonSerializer(typeof(PlannerData)).WriteObject(stream, Data);
                        if (stream.Length > MaxFileBytes) throw new InvalidDataException("记录文件已达到容量上限，请先导出并整理旧记录。原文件仍保留。");
                        stream.Flush(true);
                    }
                    string savedFingerprint = Fingerprint(temporaryPath);
                    // Check again after writing the temporary file, before replacing the original.
                    CheckPrimaryUnchanged();

                    if (primaryDamaged && primaryExisted)
                    {
                        if (String.IsNullOrEmpty(pendingRecoveryCopy) || !File.Exists(pendingRecoveryCopy))
                        {
                            string recoveryPath = dataPath + ".corrupt-"
                                + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture)
                                + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                            File.Copy(dataPath, recoveryPath, false);
                            pendingRecoveryCopy = recoveryPath;
                        }
                        RecoveryCopyPath = pendingRecoveryCopy;
                    }

                    if (primaryExisted)
                    {
                        // A recovered backup must not be replaced with the damaged primary.
                        File.Replace(temporaryPath, dataPath,
                            primaryDamaged ? null : dataPath + ".bak", true);
                    }
                    else
                    {
                        File.Move(temporaryPath, dataPath);
                    }

                    primaryExisted = true;
                    primaryFingerprint = savedFingerprint;
                    primaryDamaged = false;
                    pendingRecoveryCopy = null;
                }
                finally
                {
                    // Cleanup must not conceal the actual validation/write failure.
                    try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private void CheckPrimaryUnchanged()
        {
            bool exists = File.Exists(dataPath);
            if (exists != primaryExisted
                || (exists && !String.Equals(primaryFingerprint, Fingerprint(dataPath), StringComparison.Ordinal)))
                throw new IOException("计划数据已被另一个窗口或程序修改。为避免覆盖，请重新打开桌面计划后再保存。");
        }

        private static PlannerData ReadData(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > MaxFileBytes)
                    throw new InvalidDataException("计划数据文件超过允许的大小。原文件将保留。");
                // UTF-8 files edited by Windows tools often contain a byte-order mark.
                if (stream.Length >= 3)
                {
                    bool hasBom = stream.ReadByte() == 0xEF && stream.ReadByte() == 0xBB && stream.ReadByte() == 0xBF;
                    if (!hasBom) stream.Position = 0;
                }
                PlannerData data = new DataContractJsonSerializer(typeof(PlannerData)).ReadObject(stream) as PlannerData;
                Validate(data);
                return data;
            }
        }

        private static string Fingerprint(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 algorithm = SHA256.Create())
                return Convert.ToBase64String(algorithm.ComputeHash(stream));
        }

        private static void Validate(PlannerData data)
        {
            if (data == null) throw new InvalidDataException("计划数据不能为空。");
            if (data.Version != 1 && data.Version != 2) throw new InvalidDataException("不支持此计划数据版本，原文件将保留。");
            if (data.Tasks == null) data.Tasks = new List<TaskItem>();
            if (data.Tasks.Count > MaxTaskCount) throw new InvalidDataException("计划条目数量超过允许的上限。");
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (TaskItem task in data.Tasks)
            {
                if (task == null) throw new InvalidDataException("计划条目不能为空。");
                if (String.IsNullOrWhiteSpace(task.Id) || task.Id.Length > 128 || HasControl(task.Id))
                    throw new InvalidDataException("计划条目的标识无效。");
                if (!ids.Add(task.Id)) throw new InvalidDataException("计划条目的标识重复。");
                if (String.IsNullOrWhiteSpace(task.Title) || task.Title.Length > MaxTitleLength || task.Title.IndexOf('\0') >= 0)
                    throw new InvalidDataException("计划标题不能为空，且不能超过 240 个字符。");
                if (task.Notes != null && (task.Notes.Length > MaxNotesLength || task.Notes.IndexOf('\0') >= 0))
                    throw new InvalidDataException("计划备注不能超过 8000 个字符。");
                DateTime date;
                if (!DateTime.TryParseExact(task.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out date))
                    throw new InvalidDataException("计划日期必须是有效的 yyyy-MM-dd 日期。");
                if (String.IsNullOrEmpty(task.Scope)) task.Scope = TaskItem.DailyScope;
                if (task.Scope != TaskItem.DailyScope && task.Scope != TaskItem.WeeklyScope)
                    throw new InvalidDataException("计划类型必须是每日或每周。");
                if (task.Progress < 0 || task.Progress > 100)
                    throw new InvalidDataException("计划进度应在 0% 到 100% 之间。");
                if (task.IsWeekly && !task.Completed && task.Progress == 100)
                    throw new InvalidDataException("进度为 100% 的周计划应标记为完成。");
                if (task.IsWeekly && date.DayOfWeek != DayOfWeek.Monday)
                    throw new InvalidDataException("周计划应保存在对应周的周一。");
                // Optional text is normalized without changing dates or completed history.
                if (task.Notes == null) task.Notes = String.Empty;
                if (task.CreatedAt == null) task.CreatedAt = String.Empty;
                if (task.CompletedAt == null) task.CompletedAt = String.Empty;
                if (task.CreatedAt.Length > 128 || task.CompletedAt.Length > 128)
                    throw new InvalidDataException("计划时间记录过长。");
            }
            if (data.Journal == null) data.Journal = new List<JournalEntry>();
            if (data.Journal.Count > MaxTaskCount) throw new InvalidDataException("日记条目数量超过允许的上限。");
            HashSet<string> journalDates = new HashSet<string>(StringComparer.Ordinal);
            foreach (JournalEntry entry in data.Journal)
            {
                DateTime date;
                if (entry == null || !DateTime.TryParseExact(entry.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                    throw new InvalidDataException("日记日期必须是有效的 yyyy-MM-dd 日期。");
                if (!journalDates.Add(entry.Date)) throw new InvalidDataException("同一天的日记不能重复。");
                if (String.IsNullOrWhiteSpace(entry.Content) || entry.Content.Length > MaxJournalLength || entry.Content.IndexOf('\0') >= 0)
                    throw new InvalidDataException("日记内容不能为空，且不能超过 8000 个字符。");
                entry.UpdatedAt = entry.UpdatedAt ?? String.Empty;
                if (entry.UpdatedAt.Length > 128) throw new InvalidDataException("日记时间记录过长。");
            }
            if (data.Settings == null) data.Settings = new PlannerSettings();
            PlannerSettings settings = data.Settings;
            settings.Opacity = ClampFinite(settings.Opacity, 0.50, 1.0, 0.96);
            settings.Width = ClampFinite(settings.Width, 380, 900, 432);
            settings.Height = ClampFinite(settings.Height, 410, 2000, 920);
            settings.Left = ClampFinite(settings.Left, -100000, 100000, -1);
            settings.Top = ClampFinite(settings.Top, -100000, 100000, -1);
            settings.AppTitle = NormalizeLabel(settings.AppTitle, "每日计划", 12, "窗口标题");
            settings.Subtitle = NormalizeLabel(settings.Subtitle, "把一天，安排得刚刚好。", 30, "窗口副标题");
            settings.TodayLabel = NormalizeLabel(settings.TodayLabel, "今天", 6, "今天按钮文字");
            settings.TomorrowLabel = NormalizeLabel(settings.TomorrowLabel, "明天", 6, "明天按钮文字");
            settings.CalendarLabel = NormalizeLabel(settings.CalendarLabel, "选日期", 6, "日期按钮文字");
            settings.BackgroundImage = (settings.BackgroundImage ?? String.Empty).Trim();
            if (settings.BackgroundImage.Length > 1024 || HasControl(settings.BackgroundImage))
                throw new InvalidDataException("背景图片路径无效，且不能超过 1024 个字符。");
            settings.BackgroundOpacity = ClampFinite(settings.BackgroundOpacity, 0, 0.50, 0.22);
            if (String.IsNullOrEmpty(settings.ViewMode)) settings.ViewMode = TaskItem.DailyScope;
            if (settings.ViewMode != TaskItem.DailyScope && settings.ViewMode != TaskItem.WeeklyScope)
                throw new InvalidDataException("计划视图必须是每日或每周。");
            data.Version = 2;
        }

        private static string NormalizeLabel(string value, string fallback, int maximum, string name)
        {
            if (String.IsNullOrWhiteSpace(value)) return fallback;
            value = value.Trim();
            if (value.Length > maximum || HasControl(value))
                throw new InvalidDataException(name + "不能超过 " + maximum + " 个字符，且不能包含换行或控制字符。");
            return value;
        }

        private static bool HasControl(string value)
        {
            foreach (char character in value) if (Char.IsControl(character)) return true;
            return false;
        }

        private static double ClampFinite(double value, double minimum, double maximum, double fallback)
        {
            if (Double.IsNaN(value) || Double.IsInfinity(value)) return fallback;
            return Math.Min(maximum, Math.Max(minimum, value));
        }
    }
}
