using System;
using System.IO;
using System.Linq;
using System.Text;
using DeskPlanner;

internal static class StoreTests
{
    private static string testRoot;
    private static int passed;

    private static int Main()
    {
        testRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "store-test-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            Run("empty defaults", EmptyDefaults);
            Run("Chinese roundtrip and completed history", ChineseRoundtrip);
            Run("previous revision backup", PreviousRevisionBackup);
            Run("corrupt primary recovery and preservation", CorruptPrimaryRecovery);
            Run("corrupt primary without backup", CorruptWithoutBackup);
            Run("missing primary recovery", MissingPrimaryRecovery);
            Run("invalid date and duplicate ID reject without data loss", ValidationRejects);
            Run("settings bounds and old settings defaults", SettingsBounds);
            Run("appearance settings roundtrip preserves tasks", AppearanceRoundtrip);
            Run("old and blank appearance fields use defaults", AppearanceDefaults);
            Run("appearance limits reject safely and intensity clamps", AppearanceValidation);
            Run("external changes are not overwritten", ConcurrentChanges);
            Run("legacy daily plans migrate without changing records", LegacyDailyMigration);
            Run("daily and weekly records coexist and validate safely", WeeklyRoundtrip);
            Run("weekly partial progress and legacy completion persist safely", WeeklyProgress);
            Run("journals roundtrip with old plans and completed history", JournalRoundtrip);
            Run("invalid journal dates, duplicates and content cannot damage saved data", JournalValidation);
            Run("years of journals persist, search, paginate and export without truncation", JournalArchive);
            Run("Monday weeks handle Sundays, year changes and date limits", WeekBoundaries);
            Console.WriteLine("Passed " + passed + " storage tests.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.ToString());
            return 1;
        }
        finally
        {
            // The fixture directory is always generated under the test executable directory.
            string absolute = Path.GetFullPath(testRoot);
            string parent = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
            if (absolute.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(absolute, true);
        }
    }

    private static void Run(string name, Action test)
    {
        test();
        passed++;
        Console.WriteLine("PASS " + name);
    }

    private static string FileFor(string name) { return Path.Combine(testRoot, name + ".json"); }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("Assertion failed: " + message);
    }

    private static TaskItem Item(string id, string title, string date, bool completed)
    {
        return new TaskItem { Id = id, Title = title, Date = date, Completed = completed,
            Notes = "备注：试验数据与明日安排", CreatedAt = "2026-10-02T09:30:00+08:00",
            CompletedAt = completed ? "2026-10-02T10:30:00+08:00" : String.Empty };
    }

    private static void EmptyDefaults()
    {
        PlannerStore store = new PlannerStore(FileFor("empty"));
        Assert(store.Data.Tasks.Count == 0, "new planner must contain no example tasks");
        Assert(!store.Data.Settings.AlwaysOnTop && !store.Data.Settings.AutoStart && !store.Data.Settings.CarryOver, "boolean defaults");
        Assert(store.Data.Settings.Opacity == 0.96 && store.Data.Settings.Width == 432 && store.Data.Settings.Height == 920, "window defaults");
        AssertAppearanceDefaults(store.Data.Settings);
        store.Save();
        Assert(File.Exists(FileFor("empty")), "initial save creates data file");
    }

    private static void ChineseRoundtrip()
    {
        string path = FileFor("Chinese");
        PlannerStore store = new PlannerStore(path);
        store.Data.Tasks.Add(Item("yesterday", "复盘飞控仿真 ✓", "2026-10-01", true));
        store.Data.Tasks.Add(Item("today", "今天整理论文📚", "2026-10-02", false));
        store.Data.Tasks.Add(Item("tomorrow", "明天准备实验", "2026-10-03", false));
        store.Save();
        PlannerStore reopened = new PlannerStore(path);
        Assert(reopened.Data.Tasks.Count == 3, "all dated records remain");
        Assert(reopened.Data.Tasks[1].Title == "今天整理论文📚" && reopened.Data.Tasks[1].Notes == store.Data.Tasks[1].Notes, "Unicode roundtrip");
        Assert(reopened.Data.Tasks[0].Completed && reopened.Data.Tasks[0].CompletedAt == "2026-10-02T10:30:00+08:00", "completed history retained");
        Assert(reopened.Data.Tasks[2].Date == "2026-10-03", "future dates retained");
    }

    private static void PreviousRevisionBackup()
    {
        string path = FileFor("backup");
        PlannerStore store = new PlannerStore(path);
        store.Data.Tasks.Add(Item("a", "初始计划", "2026-10-02", false));
        store.Save();
        byte[] original = File.ReadAllBytes(path);
        store.Data.Tasks[0].Title = "修订计划";
        store.Save();
        Assert(original.SequenceEqual(File.ReadAllBytes(path + ".bak")), "backup contains the previous complete revision");
        Assert(new PlannerStore(path).Data.Tasks[0].Title == "修订计划", "primary contains current revision");
    }

    private static void CorruptPrimaryRecovery()
    {
        string path = FileFor("recovery");
        PlannerStore seed = new PlannerStore(path);
        seed.Data.Tasks.Add(Item("a", "可恢复计划", "2026-10-02", true));
        seed.Save();
        seed.Data.Tasks.Add(Item("b", "新计划", "2026-10-03", false));
        seed.Save();
        byte[] goodBackup = File.ReadAllBytes(path + ".bak");
        byte[] corrupt = Encoding.UTF8.GetBytes("{ broken json \r\n 中文原始内容");
        File.WriteAllBytes(path, corrupt);
        PlannerStore recovered = new PlannerStore(path);
        Assert(recovered.LoadWarning.Length > 0 && recovered.Data.Tasks.Count == 1, "backup used with warning");
        Assert(corrupt.SequenceEqual(File.ReadAllBytes(path)), "loading must not replace damaged data");
        recovered.Save();
        Assert(!String.IsNullOrEmpty(recovered.RecoveryCopyPath) && corrupt.SequenceEqual(File.ReadAllBytes(recovered.RecoveryCopyPath)), "damaged original preserved exactly");
        Assert(goodBackup.SequenceEqual(File.ReadAllBytes(path + ".bak")), "good backup is not replaced by corrupt original");
        Assert(new PlannerStore(path).Data.Tasks[0].Completed, "recovered complete history remains after save");
    }

    private static void CorruptWithoutBackup()
    {
        string path = FileFor("no-backup");
        byte[] corrupt = Encoding.UTF8.GetBytes("this is not JSON");
        File.WriteAllBytes(path, corrupt);
        PlannerStore recovered = new PlannerStore(path);
        Assert(recovered.LoadWarning.Length > 0 && recovered.Data.Tasks.Count == 0, "empty recovery is explicit");
        recovered.Data.Tasks.Add(Item("a", "重新录入", "2026-10-02", false));
        recovered.Save();
        Assert(corrupt.SequenceEqual(File.ReadAllBytes(recovered.RecoveryCopyPath)), "unrecoverable original is retained");
        Assert(new PlannerStore(path).Data.Tasks.Count == 1, "new plan persists");
    }

    private static void MissingPrimaryRecovery()
    {
        string path = FileFor("missing");
        PlannerStore seed = new PlannerStore(path);
        seed.Data.Tasks.Add(Item("a", "备份中的历史", "2026-09-29", true));
        seed.Save();
        seed.Save();
        File.Delete(path);
        PlannerStore recovered = new PlannerStore(path);
        Assert(recovered.Data.Tasks.Count == 1 && recovered.LoadWarning.Length > 0, "missing file recovery is explicit");
        recovered.Save();
        Assert(File.Exists(path) && File.Exists(path + ".bak"), "recovery creates primary while retaining backup");
    }

    private static void ValidationRejects()
    {
        string path = FileFor("validation");
        PlannerStore store = new PlannerStore(path);
        store.Data.Tasks.Add(Item("a", "有效历史", "2024-02-29", true));
        store.Save();
        byte[] valid = File.ReadAllBytes(path);
        store.Data.Tasks[0].Date = "2026-02-29";
        AssertSaveFails(store, typeof(InvalidDataException));
        Assert(valid.SequenceEqual(File.ReadAllBytes(path)), "invalid calendar date cannot replace valid saved data");
        store.Data.Tasks[0].Date = "2024-02-29";
        store.Data.Tasks.Add(Item("a", "重复标识", "2026-10-03", false));
        AssertSaveFails(store, typeof(InvalidDataException));
        Assert(valid.SequenceEqual(File.ReadAllBytes(path)), "duplicate IDs cannot destroy history");
        store.Data.Tasks.RemoveAt(1);
        store.Data.Tasks[0].Title = new String('字', 241);
        AssertSaveFails(store, typeof(InvalidDataException));
        Assert(valid.SequenceEqual(File.ReadAllBytes(path)), "oversized title rejected");
    }

    private static void SettingsBounds()
    {
        string path = FileFor("settings");
        PlannerStore store = new PlannerStore(path);
        store.Data.Settings.Width = 1;
        store.Data.Settings.Height = 3000;
        store.Data.Settings.Opacity = Double.NaN;
        store.Data.Settings.Left = Double.PositiveInfinity;
        store.Save();
        PlannerSettings settings = new PlannerStore(path).Data.Settings;
        Assert(settings.Width == 380 && settings.Height == 2000 && settings.Opacity == 0.96 && settings.Left == -1, "finite window bounds enforced");
        string oldPath = FileFor("old-settings");
        File.WriteAllText(oldPath, "{\"Version\":1,\"Tasks\":[],\"Settings\":{\"AlwaysOnTop\":true}}", Encoding.UTF8);
        settings = new PlannerStore(oldPath).Data.Settings;
        Assert(settings.AlwaysOnTop && settings.Width == 432 && settings.Height == 920 && settings.Opacity == 0.96, "missing setting members keep defaults: " + settings.AlwaysOnTop + ", " + settings.Width + ", " + settings.Height + ", " + settings.Opacity);
    }

    private static void ConcurrentChanges()
    {
        string path = FileFor("concurrent");
        PlannerStore first = new PlannerStore(path);
        first.Data.Tasks.Add(Item("a", "原计划", "2026-10-02", false));
        first.Save();
        PlannerStore second = new PlannerStore(path);
        first.Data.Tasks[0].Title = "另一个窗口的新计划";
        first.Save();
        second.Data.Tasks[0].Title = "过期修改";
        AssertSaveFails(second, typeof(IOException));
        Assert(new PlannerStore(path).Data.Tasks[0].Title == "另一个窗口的新计划", "external update must not be overwritten");
    }

    private static void AppearanceRoundtrip()
    {
        string path = FileFor("appearance-roundtrip");
        PlannerStore store = new PlannerStore(path);
        store.Data.Tasks.Add(Item("tomorrow-a", "明天整理论文", "2026-10-03", false));
        store.Data.Tasks.Add(Item("tomorrow-b", "明天检查实验", "2026-10-03", true));
        PlannerSettings settings = store.Data.Settings;
        settings.AppTitle = "科研小日程";
        settings.Subtitle = "每天进步一点点。";
        settings.TodayLabel = "今日安排";
        settings.TomorrowLabel = "明日计划";
        settings.CalendarLabel = "看日历";
        settings.BackgroundImage = "D:\\照片\\云海风景.png";
        settings.BackgroundOpacity = 0.35;
        store.Save();
        PlannerStore reopened = new PlannerStore(path);
        PlannerSettings actual = reopened.Data.Settings;
        Assert(actual.AppTitle == settings.AppTitle && actual.Subtitle == settings.Subtitle,
            "custom Chinese title and subtitle persist");
        Assert(actual.TodayLabel == settings.TodayLabel && actual.TomorrowLabel == settings.TomorrowLabel
            && actual.CalendarLabel == settings.CalendarLabel, "custom button labels persist");
        Assert(actual.BackgroundImage == settings.BackgroundImage && actual.BackgroundOpacity == 0.35,
            "background path and intensity persist even if image is currently unavailable");
        Assert(reopened.LoadWarning == String.Empty && reopened.Data.Tasks.Count == 2,
            "appearance changes retain existing tasks");
        Assert(reopened.Data.Tasks[0].Date == "2026-10-03" && reopened.Data.Tasks[1].Completed,
            "appearance changes retain future dates and completion history");
    }

    private static void AppearanceDefaults()
    {
        string oldPath = FileFor("appearance-old-version");
        File.WriteAllText(oldPath, "{\"Version\":1,\"Tasks\":[{\"Id\":\"old\",\"Title\":\"已有计划\",\"Date\":\"2026-10-03\",\"Completed\":false}],\"Settings\":{\"Width\":432,\"Height\":530}}", Encoding.UTF8);
        PlannerStore old = new PlannerStore(oldPath);
        Assert(old.LoadWarning == String.Empty && old.Data.Tasks.Count == 1, "old file loads without treating missing appearance fields as corruption");
        AssertAppearanceDefaults(old.Data.Settings);
        old.Save();
        PlannerStore reopened = new PlannerStore(oldPath);
        AssertAppearanceDefaults(reopened.Data.Settings);
        Assert(reopened.Data.Tasks.Single().Title == "已有计划", "upgrading missing fields preserves existing records");

        string blankPath = FileFor("appearance-blank-labels");
        File.WriteAllText(blankPath, "{\"Version\":1,\"Tasks\":[],\"Settings\":{\"AppTitle\":null,\"Subtitle\":\"  \",\"TodayLabel\":\"\",\"TomorrowLabel\":null,\"CalendarLabel\":\" \",\"BackgroundImage\":null}}", Encoding.UTF8);
        PlannerStore blank = new PlannerStore(blankPath);
        Assert(blank.LoadWarning == String.Empty, "blank optional appearance fields load normally");
        AssertAppearanceDefaults(blank.Data.Settings);
    }

    private static void AppearanceValidation()
    {
        string path = FileFor("appearance-validation");
        PlannerStore store = new PlannerStore(path);
        store.Data.Tasks.Add(Item("kept", "已有计划保持原样", "2026-10-03", false));
        store.Save();
        byte[] valid = File.ReadAllBytes(path);
        Action<Action> rejects = delegate(Action invalidChange)
        {
            invalidChange();
            AssertSaveFails(store, typeof(InvalidDataException));
            Assert(valid.SequenceEqual(File.ReadAllBytes(path)), "invalid appearance must not replace saved tasks");
            store.Data.Settings = new PlannerSettings();
        };
        rejects(delegate { store.Data.Settings.AppTitle = new String('字', 13); });
        rejects(delegate { store.Data.Settings.Subtitle = new String('字', 31); });
        rejects(delegate { store.Data.Settings.TodayLabel = new String('字', 7); });
        rejects(delegate { store.Data.Settings.TomorrowLabel = new String('字', 7); });
        rejects(delegate { store.Data.Settings.CalendarLabel = new String('字', 7); });
        rejects(delegate { store.Data.Settings.BackgroundImage = new String('a', 1025); });

        store.Data.Settings.BackgroundOpacity = 1;
        store.Save();
        Assert(new PlannerStore(path).Data.Settings.BackgroundOpacity == 0.50, "excess background intensity is bounded");
        store.Data.Settings.BackgroundOpacity = -1;
        store.Save();
        Assert(new PlannerStore(path).Data.Settings.BackgroundOpacity == 0, "negative intensity is bounded");
        store.Data.Settings.BackgroundOpacity = Double.NaN;
        store.Save();
        Assert(new PlannerStore(path).Data.Settings.BackgroundOpacity == 0.22, "nonfinite intensity falls back safely");
    }

    private static void LegacyDailyMigration()
    {
        string path = FileFor("legacy-daily");
        File.WriteAllText(path, "{\"Version\":1,\"Tasks\":[{\"Id\":\"legacy\",\"Title\":\"旧版每日计划\",\"Notes\":\"原备注\",\"Date\":\"2026-10-04\",\"Completed\":true,\"CreatedAt\":\"2026-10-02T09:30:00+08:00\",\"CompletedAt\":\"2026-10-03T10:30:00+08:00\"}],\"Settings\":{\"AppTitle\":\"原窗口标题\"}}", new UTF8Encoding(false));
        byte[] original = File.ReadAllBytes(path);
        PlannerStore store = new PlannerStore(path);
        TaskItem task = store.Data.Tasks.Single();
        Assert(store.Data.Version == 2 && task.Scope == TaskItem.DailyScope, "old records become daily plans in the current schema");
        Assert(store.Data.Journal.Count == 0, "missing diary fields load as an empty history");
        Assert(task.Date == "2026-10-04" && task.Completed && task.Notes == "原备注" && task.CompletedAt == "2026-10-03T10:30:00+08:00", "migration preserves date, notes and completion history");
        Assert(store.Data.Settings.AppTitle == "原窗口标题" && store.Data.Settings.ViewMode == TaskItem.DailyScope, "existing appearance and default view survive");
        Assert(original.SequenceEqual(File.ReadAllBytes(path)), "loading never rewrites live data");
        store.Save();
        Assert(original.SequenceEqual(File.ReadAllBytes(path + ".bak")), "migration retains the original version as a backup");
        Assert(new PlannerStore(path).Data.Tasks.Single().Id == "legacy", "migrated record reopens with the same identity");
    }

    private static void WeeklyRoundtrip()
    {
        string path = FileFor("weekly");
        PlannerStore store = new PlannerStore(path);
        store.Data.Tasks.Add(Item("daily", "周一的日计划", "2026-09-28", false));
        TaskItem week = Item("week", "本周的学习目标", "2026-09-28", true); week.Scope = TaskItem.WeeklyScope;
        store.Data.Tasks.Add(week); store.Data.Settings.ViewMode = TaskItem.WeeklyScope; store.Save();
        PlannerStore reopened = new PlannerStore(path);
        Assert(reopened.Data.Tasks.Count == 2 && reopened.Data.Tasks.Count(t => t.IsWeekly) == 1, "same date retains both independent plan types");
        Assert(reopened.Data.Tasks.Single(t => t.IsWeekly).CompletedAt == week.CompletedAt && reopened.Data.Settings.ViewMode == TaskItem.WeeklyScope, "week completion and selected view persist");
        byte[] valid = File.ReadAllBytes(path);
        week.Date = "2026-10-04"; AssertSaveFails(store, typeof(InvalidDataException));
        Assert(valid.SequenceEqual(File.ReadAllBytes(path)), "a non-Monday weekly date cannot damage saved records");
        week.Date = "2026-09-28"; week.Scope = "monthly"; AssertSaveFails(store, typeof(InvalidDataException));
        week.Scope = TaskItem.WeeklyScope; store.Data.Settings.ViewMode = "monthly"; AssertSaveFails(store, typeof(InvalidDataException));
    }

    private static void WeeklyProgress()
    {
        string path = FileFor("weekly-progress");
        File.WriteAllText(path, "{\"Version\":2,\"Tasks\":[{\"Id\":\"old\",\"Title\":\"旧周计划\",\"Date\":\"2026-09-28\",\"Scope\":\"weekly\",\"Completed\":true,\"CompletedAt\":\"original-time\"}]}", new UTF8Encoding(false));
        PlannerStore store = new PlannerStore(path);
        TaskItem legacy = store.Data.Tasks.Single();
        Assert(legacy.ProgressPercent == 100 && legacy.CompletedAt == "original-time", "legacy completed plans remain at 100 without rewriting history");
        TaskItem partial = Item("partial", "学习目标", "2026-09-28", false);
        partial.Scope = TaskItem.WeeklyScope; partial.Progress = 37;
        store.Data.Tasks.Add(partial); store.Save();
        PlannerStore reopened = new PlannerStore(path);
        Assert(reopened.Data.Tasks.Single(t => t.Id == "partial").ProgressPercent == 37 && !reopened.Data.Tasks.Single(t => t.Id == "partial").Completed, "partial progress reopens without being completed");
        byte[] valid = File.ReadAllBytes(path);
        foreach (int value in new[] { -1, 101, 100 })
        {
            partial.Progress = value; AssertSaveFails(store, typeof(InvalidDataException));
            Assert(valid.SequenceEqual(File.ReadAllBytes(path)), "invalid progress cannot overwrite saved records");
        }
        partial.Progress = 100; partial.Completed = true; partial.CompletedAt = "finished-time"; store.Save();
        Assert(new PlannerStore(path).Data.Tasks.Single(t => t.Id == "partial").CompletedAt == "finished-time", "full progress persists with its completion time");
    }

    private static void JournalRoundtrip()
    {
        string path = FileFor("journals");
        PlannerStore store = new PlannerStore(path);
        store.Data.Tasks.Add(Item("kept", "原有计划", "2026-10-04", true)); store.Save();
        Assert(new PlannerStore(path).Data.Journal.Count == 0, "old data without diary records opens with an empty journal");
        store.Data.Journal.Add(new JournalEntry { Date = "2026-10-04", Content = "学习了值迭代。\n理解了贝尔曼方程。📚", UpdatedAt = "2026-10-04T14:00:00Z" });
        store.Data.Journal.Add(new JournalEntry { Date = "2026-10-02", Content = "搭建了仿真环境。", UpdatedAt = "2026-10-02T14:00:00Z" }); store.Save();
        PlannerStore reopened = new PlannerStore(path);
        Assert(reopened.Data.Journal.Count == 2 && reopened.Data.Journal[0].Content.Contains("\n") && reopened.Data.Journal[0].Content.EndsWith("📚"), "Chinese text, newlines and separate dates survive reopening");
        Assert(reopened.Data.Tasks.Single().Completed && reopened.Data.Tasks.Single().CompletedAt == store.Data.Tasks.Single().CompletedAt, "diary saves retain existing completion history");
        reopened.Data.Journal[0].Content = "修订后的学习记录"; reopened.Save();
        Assert(new PlannerStore(path).Data.Journal.Single(j => j.Date == "2026-10-04").Content == "修订后的学习记录", "editing retains one entry per date");
        Assert(new PlannerStore(path + ".bak").Data.Journal[0].Content == store.Data.Journal[0].Content, "the previous diary revision is retained in the backup");
    }

    private static void JournalValidation()
    {
        string path = FileFor("journal-validation");
        PlannerStore store = new PlannerStore(path);
        JournalEntry entry = new JournalEntry { Date = "2026-10-04", Content = "原记录", UpdatedAt = "" };
        store.Data.Journal.Add(entry); store.Save(); byte[] valid = File.ReadAllBytes(path);
        entry.Date = "2026-02-30"; AssertSaveFails(store, typeof(InvalidDataException)); entry.Date = "2026-10-04";
        foreach (string content in new[] { "  ", new String('字', PlannerStore.MaxJournalLength + 1), "坏\0内容" })
        {
            entry.Content = content; AssertSaveFails(store, typeof(InvalidDataException));
            Assert(valid.SequenceEqual(File.ReadAllBytes(path)), "invalid diary content preserves the saved file");
        }
        entry.Content = "原记录"; store.Data.Journal.Add(new JournalEntry { Date = entry.Date, Content = "重复日期" }); AssertSaveFails(store, typeof(InvalidDataException));
        Assert(valid.SequenceEqual(File.ReadAllBytes(path)), "duplicate diary dates preserve the saved file");
    }

    private static void JournalArchive()
    {
        PlannerStore store = new PlannerStore(FileFor("journal-archive"));
        DateTime newest = new DateTime(2026, 10, 4);
        for (int i = 0; i < 3000; i++) store.Data.Journal.Add(new JournalEntry { Date = newest.AddDays(-i).ToString("yyyy-MM-dd"), Content = "第 " + i + " 次学习\n" + new String('文', 240) + (i == 137 ? "Deep_Keyword" : "继续积累"), UpdatedAt = "" });
        store.Save(); PlannerStore reopened = new PlannerStore(FileFor("journal-archive"));
        Assert(reopened.Data.Journal.Count == 3000, "several years of full diary records reopen intact");
        JournalPage first = JournalBrowse.FindPage(reopened.Data.Journal, "", "", 0);
        JournalPage last = JournalBrowse.FindPage(reopened.Data.Journal, "", "", Int32.MaxValue);
        Assert(first.Entries.Count == 8 && first.TotalCount == 3000 && first.PageCount == 375, "page rendering stays bounded with thousands of records");
        Assert(last.PageIndex == 374 && last.Entries.Last().Date == newest.AddDays(-2999).ToString("yyyy-MM-dd"), "the final page reaches the oldest records without losing entries");
        JournalPage filtered = JournalBrowse.FindPage(reopened.Data.Journal, "deep_keyword", "", 0);
        Assert(filtered.TotalCount == 1 && filtered.Entries.Single().Content.Contains("Deep_Keyword") && !JournalBrowse.Excerpt(filtered.Entries.Single().Content).Contains("Deep_Keyword"), "search includes complete bodies, not just visible excerpts");
        Assert(JournalBrowse.FindPage(reopened.Data.Journal, "", "2026-09", 0).TotalCount == 30, "month filtering excludes other dates");
        Assert(JournalBrowse.FindPage(reopened.Data.Journal, "2026/10/4", "", 0).TotalCount == 1, "display dates can be searched");
        Assert(JournalBrowse.FindPage(reopened.Data.Journal, "nothing-found", "", 10).Entries.Count == 0, "empty queries return a safe empty page");
        Assert(JournalBrowse.Excerpt(new String('字', 159) + "📚后文").Contains("📚"), "excerpt boundaries retain complete Unicode characters");
        string exported = JournalBrowse.Export(reopened.Data.Journal);
        Assert(exported.Contains("Deep_Keyword") && exported.Contains("## " + newest.AddDays(-2999).ToString("yyyy-MM-dd")) && exported.LastIndexOf("## 2026-10-04", StringComparison.Ordinal) > exported.IndexOf("## " + newest.AddDays(-2999).ToString("yyyy-MM-dd"), StringComparison.Ordinal), "export retains all full contents in chronological order");
    }

    private static void WeekBoundaries()
    {
        Assert(PlannerDates.StartOfWeek(new DateTime(2026, 10, 4)) == new DateTime(2026, 9, 28), "Sunday belongs to the preceding Monday");
        Assert(PlannerDates.StartOfWeek(new DateTime(2026, 10, 5)) == new DateTime(2026, 10, 5), "Monday starts a new week");
        Assert(PlannerDates.EndOfWeek(new DateTime(2026, 12, 31)) == new DateTime(2027, 1, 3), "week end can cross a calendar year");
        Assert(PlannerDates.StartOfWeek(DateTime.MinValue) == DateTime.MinValue && PlannerDates.EndOfWeek(DateTime.MaxValue) == DateTime.MaxValue.Date, "calendar limits stay in range");
        Assert(PlannerDates.ShiftDays(DateTime.MinValue, -7) == DateTime.MinValue && PlannerDates.ShiftDays(DateTime.MaxValue, 7) == DateTime.MaxValue.Date, "navigation clamps at calendar limits");
    }

    private static void AssertAppearanceDefaults(PlannerSettings settings)
    {
        Assert(settings.AppTitle == "每日计划" && settings.Subtitle == "把一天，安排得刚刚好。", "default title and subtitle");
        Assert(settings.TodayLabel == "今天" && settings.TomorrowLabel == "明天" && settings.CalendarLabel == "选日期", "default tab labels");
        Assert(settings.BackgroundImage == String.Empty && settings.BackgroundOpacity == 0.22, "default background appearance");
    }

    private static void AssertSaveFails(PlannerStore store, Type expected)
    {
        try { store.Save(); }
        catch (Exception error)
        {
            if (expected.IsAssignableFrom(error.GetType())) return;
            throw;
        }
        throw new Exception("Expected Save() to fail with " + expected.Name);
    }
}
