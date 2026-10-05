using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DeskPlanner
{
    internal static class UiTests
    {
        private static Application application;
        private static string testRoot;
        private static int passed;

        [STAThread]
        public static int Main()
        {
            testRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "ui-integration-data", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);
            application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                Run("completion and undo handlers persist", CompleteAndUndo);
                Run("reschedule context menu handlers persist", RescheduleHandlers);
                Run("failed saves restore original task objects and event closures", FailedSaveRollback);
                Run("date rollover follows relative tabs and retains fixed dates", DateRollover);
                Run("today and tomorrow tab handlers select relative dates", RelativeTabHandlers);
                Run("overdue counts exclude completed and future tasks", OverdueCount);
                Run("hidden main layout stays usable at default and minimum sizes", HiddenLayout);
                Run("custom appearance renders image with readable overlay and safe fallback", AppearanceRendering);
                Run("weekly creation, completion, rescheduling and conversion persist", WeeklyLifecycle);
                Run("individual weekly sliders persist and aggregate partial progress", WeeklyProgressSliders);
                Run("failed weekly progress saves restore the prior percentage", WeeklyProgressRollback);
                Run("daily journal saves, switches dates and shows only recorded history", JournalLifecycle);
                Run("failed journal saves retain text and the original records", JournalRollback);
                Run("large diary history browses, filters and opens full entries without losing drafts", JournalBrowsing);
                Run("stacked panels navigate and calculate progress independently", SimultaneousPanels);
                Run("each add editor defaults to its own selected period and type", AddEditorDefaults);
                Run("tall layout upgrades once and retains later user resizing", RememberedLayout);
                Run("taskbar space stays reserved even with full-screen work areas and resizing", TaskbarClearance);
                Run("week navigation and rollover respect Monday boundaries", WeeklyNavigation);
                Run("weekly overdue notices exclude daily and current-week plans", WeeklyOverdue);
                Run("stacked long lists scroll independently and keep both add buttons visible", WeeklyLayout);
                Console.WriteLine("Passed " + passed + " hidden WPF integration tests.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.ToString());
                return 1;
            }
            finally
            {
                application.Shutdown();
                string absolute = Path.GetFullPath(testRoot);
                string allowedParent = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "ui-integration-data")) + Path.DirectorySeparatorChar;
                if (absolute.StartsWith(allowedParent, StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(absolute, true);
            }
        }

        private static void Run(string name, Action test)
        {
            test();
            passed++;
            Console.WriteLine("PASS " + name);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception("Assertion failed: " + message);
        }

        private static string Day(DateTime date) { return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        private static TaskItem Task(string title, DateTime date, bool completed)
        {
            return new TaskItem { Id = Guid.NewGuid().ToString("N"), Title = title,
                Date = Day(date), Notes = "测试备注", Completed = completed,
                CreatedAt = DateTime.UtcNow.ToString("o"),
                CompletedAt = completed ? DateTime.UtcNow.ToString("o") : String.Empty };
        }

        private static object Invoke(PlannerController controller, string name, params object[] arguments)
        {
            MethodInfo method = typeof(PlannerController).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert(method != null, "controller method exists: " + name);
            try { return method.Invoke(controller, arguments); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }

        private static void SetField(PlannerController controller, string name, object value)
        {
            typeof(PlannerController).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(controller, value);
        }

        private static T GetField<T>(PlannerController controller, string name)
        {
            return (T)typeof(PlannerController).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(controller);
        }

        private static T Control<T>(PlannerController controller, string name) where T : FrameworkElement
        {
            T control = controller.Window.FindName(name) as T;
            Assert(control != null, "named control exists: " + name);
            return control;
        }

        private static void Click(ButtonBase button)
        {
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
        }

        private static CheckBox FirstTaskCheck(PlannerController controller, bool weekly = false)
        {
            StackPanel list = Control<StackPanel>(controller, weekly ? "WeekTaskList" : "TaskList");
            Assert(list.Children.Count > 0 && list.Children[0] is Border, "rendered task row exists");
            return (CheckBox)((Grid)((Border)list.Children[0]).Child).Children[0];
        }

        private static ContextMenu FirstTaskMenu(PlannerController controller, bool weekly = false)
        {
            StackPanel list = Control<StackPanel>(controller, weekly ? "WeekTaskList" : "TaskList");
            Grid row = (Grid)((Border)list.Children[0]).Child;
            StackPanel actions = (StackPanel)row.Children[2];
            return ((Button)actions.Children[1]).ContextMenu;
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly string Path;
            internal readonly PlannerStore Store;
            internal readonly PlannerController Controller;

            internal Fixture(string name, params TaskItem[] tasks)
            {
                Path = System.IO.Path.Combine(testRoot, name, "plans.json");
                Store = new PlannerStore(Path);
                Store.Data.Tasks.AddRange(tasks);
                Store.Save();
                Controller = new PlannerController(Store, true);
                Assert(!Controller.Window.IsVisible, "fixture window must stay hidden");
            }

            public void Dispose()
            {
                GetField<DispatcherTimer>(Controller, "clock").Stop();
                GetField<DispatcherTimer>(Controller, "geometryTimer").Stop();
                Assert(!Controller.Window.IsVisible, "tests must never show a window");
                Assert(GetField<object>(Controller, "tray") == null, "tests must not create a notification icon");
                Controller.Window.Close();
            }
        }

        private static void CompleteAndUndo()
        {
            TaskItem task = Task("阅读论文并记录关键问题", DateTime.Today, false);
            using (Fixture fixture = new Fixture("completion", task))
            {
                CheckBox complete = FirstTaskCheck(fixture.Controller);
                complete.IsChecked = true;
                Click(complete);
                TaskItem saved = new PlannerStore(fixture.Path).Data.Tasks.Single();
                Assert(task.Completed && saved.Completed && !String.IsNullOrEmpty(saved.CompletedAt), "completion and timestamp persisted");
                Assert(Control<ProgressBar>(fixture.Controller, "DayProgress").Value == 100, "progress updates to completed");
                Assert(Control<TextBlock>(fixture.Controller, "SummaryText").Text.Contains("1 / 1"), "completed count updates");
                CheckBox undo = FirstTaskCheck(fixture.Controller);
                undo.IsChecked = false;
                Click(undo);
                saved = new PlannerStore(fixture.Path).Data.Tasks.Single();
                Assert(!task.Completed && !saved.Completed && saved.CompletedAt == String.Empty, "undo and cleared timestamp persisted");
                Assert(Object.ReferenceEquals(task, fixture.Store.Data.Tasks.Single()), "successful changes keep task identity");
                Assert(Control<ProgressBar>(fixture.Controller, "DayProgress").Value == 0, "progress updates after undo");
            }
        }

        private static void RescheduleHandlers()
        {
            TaskItem task = Task("调整实验计划", DateTime.Today, false);
            using (Fixture fixture = new Fixture("reschedule", task))
            {
                MenuItem tomorrow = (MenuItem)FirstTaskMenu(fixture.Controller).Items[1];
                Assert(tomorrow.IsEnabled, "move to tomorrow is enabled for today's task");
                tomorrow.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, tomorrow));
                Assert(new PlannerStore(fixture.Path).Data.Tasks.Single().Date == Day(DateTime.Today.AddDays(1)), "tomorrow reschedule persists");
                Invoke(fixture.Controller, "SelectDate", DateTime.Today.AddDays(1), "tomorrow");
                MenuItem today = (MenuItem)FirstTaskMenu(fixture.Controller).Items[0];
                today.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, today));
                Assert(new PlannerStore(fixture.Path).Data.Tasks.Single().Date == Day(DateTime.Today), "move back to today persists");
                Assert(fixture.Store.Data.Tasks.Count == 1, "rescheduling retains one record");
            }
        }

        private static void FailedSaveRollback()
        {
            TaskItem task = Task("保存前的原计划", DateTime.Today, false);
            using (Fixture fixture = new Fixture("rollback", task))
            {
                CheckBox existingHandler = FirstTaskCheck(fixture.Controller);
                byte[] originalFile = File.ReadAllBytes(fixture.Path);
                PlannerStore external = new PlannerStore(fixture.Path);
                external.Data.Tasks.Add(Task("另一个窗口新增的计划", DateTime.Today.AddDays(1), false));
                external.Save();
                Action failingMutation = delegate
                {
                    task.Title = "不应该生效的修改";
                    task.Notes = "不应该生效的备注";
                    task.Date = Day(DateTime.Today.AddDays(9));
                    task.Completed = true;
                    task.Scope = TaskItem.WeeklyScope;
                    task.Progress = 68;
                    task.CompletedAt = "invalid-new-timestamp";
                    fixture.Store.Data.Tasks.Clear();
                    fixture.Store.Data.Tasks.Add(Task("不应该添加的记录", DateTime.Today, false));
                    fixture.Store.Data.Settings.Opacity = 0.8;
                };
                bool result = (bool)Invoke(fixture.Controller, "Change", failingMutation, false);
                Assert(!result, "failed writes return false without a modal dialog");
                Assert(fixture.Store.Data.Tasks.Count == 1 && Object.ReferenceEquals(fixture.Store.Data.Tasks[0], task), "rollback restores original task object");
                Assert(task.Title == "保存前的原计划" && task.Notes == "测试备注" && task.Date == Day(DateTime.Today), "all task text and date values restored");
                Assert(!task.Completed && task.CompletedAt == String.Empty && fixture.Store.Data.Settings.Opacity == 0.96, "completion and settings restored");
                Assert(task.Scope == TaskItem.DailyScope, "failed conversion restores the original plan type");
                Assert(task.Progress == 0, "failed changes restore the original progress");
                Assert(new PlannerStore(fixture.Path).Data.Tasks.Count == 2, "external data remains intact after rejected write");
                Assert(Control<Button>(fixture.Controller, "SettingsButton").ToolTip.ToString().Contains("保存失败"), "failed saves stay discoverable through the settings tooltip");

                // Restore the test fixture's exact initial revision, then use the pre-failure event closure.
                File.WriteAllBytes(fixture.Path, originalFile);
                existingHandler.IsChecked = true;
                Click(existingHandler);
                Assert(task.Completed && fixture.Store.Data.Tasks.Single().Completed, "pre-failure handler still targets the restored object");
                Assert(new PlannerStore(fixture.Path).Data.Tasks.Single().Completed, "restored event closure persists its change");
            }
        }

        private static void DateRollover()
        {
            using (Fixture fixture = new Fixture("rollover"))
            {
                DateTime today = DateTime.Today;
                SetField(fixture.Controller, "currentDay", today.AddDays(-1));
                SetField(fixture.Controller, "selectedDay", today.AddDays(-1));
                SetField(fixture.Controller, "selectionMode", "today");
                Invoke(fixture.Controller, "RefreshDay");
                Assert(GetField<DateTime>(fixture.Controller, "selectedDay") == today, "today view advances at midnight");

                SetField(fixture.Controller, "currentDay", today.AddDays(-1));
                SetField(fixture.Controller, "selectedDay", today);
                SetField(fixture.Controller, "selectionMode", "tomorrow");
                Invoke(fixture.Controller, "RefreshDay");
                Assert(GetField<DateTime>(fixture.Controller, "selectedDay") == today.AddDays(1), "tomorrow view advances at midnight");

                DateTime fixedDate = today.AddDays(-10);
                SetField(fixture.Controller, "currentDay", today.AddDays(-1));
                SetField(fixture.Controller, "selectedDay", fixedDate);
                SetField(fixture.Controller, "selectionMode", "date");
                Invoke(fixture.Controller, "RefreshDay");
                Assert(GetField<DateTime>(fixture.Controller, "selectedDay") == fixedDate, "fixed calendar selection remains fixed");
                Assert(GetField<DateTime>(fixture.Controller, "currentDay") == today, "rollover tracks the new current day");
            }
        }

        private static void RelativeTabHandlers()
        {
            using (Fixture fixture = new Fixture("tabs"))
            {
                Invoke(fixture.Controller, "SelectDate", DateTime.Today.AddDays(30), "date");
                Click(Control<Button>(fixture.Controller, "TodayButton"));
                Assert(GetField<DateTime>(fixture.Controller, "selectedDay") == DateTime.Today
                    && GetField<string>(fixture.Controller, "selectionMode") == "today", "today tab switches back to relative today");
                Click(Control<Button>(fixture.Controller, "TomorrowButton"));
                Assert(GetField<DateTime>(fixture.Controller, "selectedDay") == DateTime.Today.AddDays(1)
                    && GetField<string>(fixture.Controller, "selectionMode") == "tomorrow", "tomorrow tab selects relative tomorrow");
                Assert(Control<TextBlock>(fixture.Controller, "DayText").Text.Contains("明天"), "rendered date describes tomorrow");
            }
        }

        private static void OverdueCount()
        {
            TaskItem yesterday = Task("昨天未完成", DateTime.Today.AddDays(-1), false);
            TaskItem earlier = Task("更早未完成", DateTime.Today.AddDays(-10), false);
            using (Fixture fixture = new Fixture("overdue", yesterday, earlier,
                Task("昨天已完成", DateTime.Today.AddDays(-1), true),
                Task("今天待完成", DateTime.Today, false),
                Task("明天待完成", DateTime.Today.AddDays(1), false)))
            {
                Button overdue = Control<Button>(fixture.Controller, "OverdueButton");
                Assert(overdue.Visibility == Visibility.Visible && overdue.Content.ToString().Contains("还有 2 项"), "only unfinished past records count as overdue");
                bool result = (bool)Invoke(fixture.Controller, "Change", new Action(delegate
                {
                    yesterday.Completed = true; yesterday.CompletedAt = DateTime.UtcNow.ToString("o");
                    earlier.Completed = true; earlier.CompletedAt = DateTime.UtcNow.ToString("o");
                }), false);
                Assert(result, "completing overdue records saves");
                Invoke(fixture.Controller, "Render");
                Assert(overdue.Visibility == Visibility.Collapsed, "overdue notice hides when nothing remains");
                Assert(new PlannerStore(fixture.Path).Data.Tasks.Count == 5, "clearing overdue status retains task history");
            }
        }

        private static void HiddenLayout()
        {
            List<TaskItem> tasks = new List<TaskItem>();
            for (int index = 0; index < 25; index++)
                tasks.Add(Task(index + " · " + new String('字', 180), DateTime.Today, index % 3 == 0));
            tasks.Add(Task("过去的未完成计划", DateTime.Today.AddDays(-1), false));
            using (Fixture fixture = new Fixture("layout", tasks.ToArray()))
            {
                FrameworkElement content = (FrameworkElement)fixture.Controller.Window.Content;
                foreach (Size size in new[] { new Size(432, 920), new Size(380, 650) })
                {
                    content.Measure(size);
                    content.Arrange(new Rect(new Point(0, 0), size));
                    content.UpdateLayout();
                    Button add = Control<Button>(fixture.Controller, "AddButton");
                    Rect addBounds = add.TransformToAncestor(content).TransformBounds(new Rect(new Point(0, 0), add.RenderSize));
                    Assert(add.ActualHeight > 0 && addBounds.Bottom <= size.Height && addBounds.Right <= size.Width, "add button remains inside the window at " + size);
                    StackPanel list = Control<StackPanel>(fixture.Controller, "TaskList");
                    ScrollViewer scroller = Descendants<ScrollViewer>(content).First(viewer => Object.ReferenceEquals(viewer.Content, list));
                    Assert(scroller.ActualHeight > 0 && scroller.ViewportHeight > 0, "task viewport remains usable at " + size);
                    Assert(scroller.ExtentHeight > scroller.ViewportHeight, "long task lists can scroll at " + size);
                    Assert(list.Children.Count == 25, "visible day has all 25 task rows");
                }
            }
        }

        private static void AppearanceRendering()
        {
            using (Fixture fixture = new Fixture("appearance-render", Task("保留的明日计划", DateTime.Today.AddDays(1), false)))
            {
                Border surface = Control<Border>(fixture.Controller, "RootSurface");
                Brush defaultBackground = surface.Background;
                string imagePath = Path.Combine(Path.GetDirectoryName(fixture.Path), "synthetic-background.png");
                byte[] pixels = new byte[]
                {
                    20, 40, 230, 255, 100, 210, 40, 255,
                    220, 90, 30, 255, 255, 255, 255, 255
                };
                BitmapSource image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (FileStream stream = new FileStream(imagePath, FileMode.CreateNew, FileAccess.Write))
                    encoder.Save(stream);

                PlannerSettings settings = fixture.Store.Data.Settings;
                settings.AppTitle = "我的科研日程";
                settings.Subtitle = "向内求真，向前求变；自我批评，自我革命。";
                settings.TodayLabel = "今日安排";
                settings.TomorrowLabel = "明日安排";
                settings.CalendarLabel = "查日历";
                settings.BackgroundImage = imagePath;
                settings.BackgroundOpacity = 0.50;
                fixture.Store.Save();
                Invoke(fixture.Controller, "Render");

                Assert(Control<TextBlock>(fixture.Controller, "BrandTitle").Text == settings.AppTitle
                    && fixture.Controller.Window.Title == settings.AppTitle, "custom title appears in heading and native window title");
                Assert(Control<TextBlock>(fixture.Controller, "SubtitleText").Visibility == Visibility.Collapsed
                    && Control<TextBlock>(fixture.Controller, "WeekWatermarkText").Text == "向内求真向前求变\n自我批评自我革命",
                    "the weekly backdrop displays two lines without punctuation");
                StackPanel handwriting = Control<StackPanel>(fixture.Controller, "WeekWatermarkArtwork");
                Image top = Control<Image>(fixture.Controller, "WeekWatermarkTop");
                Assert(handwriting.Visibility == Visibility.Visible && top.Source is BitmapSource
                    && ((BitmapSource)top.Source).PixelWidth > 100,
                    "the standard motto displays its embedded cursive artwork");
                Assert(!Control<Viewbox>(fixture.Controller, "WeekWatermark").IsHitTestVisible
                    && Control<Viewbox>(fixture.Controller, "WeekWatermark").Opacity <= .30,
                    "background text stays light and allows list interaction");
                Assert(Control<Button>(fixture.Controller, "TodayButton").Content.ToString() == settings.TodayLabel
                    && Control<Button>(fixture.Controller, "TomorrowButton").Content.ToString() == settings.TomorrowLabel
                    && Control<Button>(fixture.Controller, "CalendarButton").Content.ToString() == settings.CalendarLabel,
                    "all custom tab labels appear");

                DrawingBrush background = Control<Border>(fixture.Controller, "WallpaperSurface").Background as DrawingBrush;
                Assert(background != null && background.Stretch == Stretch.Uniform && background.AlignmentY == AlignmentY.Bottom,
                    "local PNG retains its whole image and sits below the weekly section");
                DrawingGroup drawing = background.Drawing as DrawingGroup;
                Assert(drawing != null && drawing.Children.Count == 2 && drawing.Children[0] is ImageDrawing,
                    "background contains the loaded image and an overlay");
                ImageDrawing picture = (ImageDrawing)drawing.Children[0];
                BitmapSource loadedImage = picture.ImageSource as BitmapSource;
                Assert(loadedImage != null && loadedImage.PixelWidth > 0 && loadedImage.PixelHeight > 0,
                    "synthetic PNG is decoded into actual image pixels");
                GeometryDrawing overlay = drawing.Children[1] as GeometryDrawing;
                SolidColorBrush overlayBrush = overlay == null ? null : overlay.Brush as SolidColorBrush;
                Assert(overlayBrush != null && overlayBrush.Color.A >= 178
                    && overlayBrush.Color.R >= 240 && overlayBrush.Color.G >= 240 && overlayBrush.Color.B >= 240,
                    "bright overlay keeps background intensity capped for readable text");

                settings.Subtitle = "每天进步；持续复盘。"; Invoke(fixture.Controller, "Render");
                Assert(handwriting.Visibility == Visibility.Collapsed && Control<TextBlock>(fixture.Controller, "WeekWatermarkText").Visibility == Visibility.Visible
                    && Control<TextBlock>(fixture.Controller, "WeekWatermarkText").Text == "每天进步\n持续复盘",
                    "changing the motto shows the new wording rather than a stale artwork");
                Assert(new PlannerStore(fixture.Path).Data.Settings.Subtitle == "向内求真，向前求变；自我批评，自我革命。",
                    "punctuation removal affects presentation without rewriting saved wording");

                File.Delete(imagePath);
                settings.BackgroundImage = Path.Combine(Path.GetDirectoryName(fixture.Path), "missing-background.png");
                Invoke(fixture.Controller, "Render");
                Assert(Object.ReferenceEquals(surface.Background, defaultBackground) && Control<Border>(fixture.Controller, "WallpaperSurface").Background == null,
                    "missing image falls back to the original light background");
                Assert(fixture.Store.Data.Tasks.Single().Title == "保留的明日计划",
                    "appearance rendering does not modify plans");
            }
        }

        private static void WeeklyLifecycle()
        {
            TaskItem daily = Task("日计划不计入周进度", DateTime.Today, true);
            using (Fixture fixture = new Fixture("weekly-lifecycle", daily))
            {
                Assert(Control<Button>(fixture.Controller, "WeekTodayButton").Content.ToString() == "本周", "week controls are always present");
                bool saved = (bool)Invoke(fixture.Controller, "CommitTask", null, "每周学习目标", "完成章节并复盘", DateTime.Today, true, false);
                Assert(saved, "new weekly plan saves");
                TaskItem week = fixture.Store.Data.Tasks.Single(t => t.IsWeekly);
                Assert(week.Date == Day(PlannerDates.StartOfWeek(DateTime.Today)) && fixture.Store.Data.Tasks.Count == 2, "new week record uses Monday and preserves daily data");
                Assert(Control<ProgressBar>(fixture.Controller, "WeekProgress").Value == 0 && Control<ProgressBar>(fixture.Controller, "DayProgress").Value == 100, "daily completion does not affect weekly progress");
                CheckBox complete = FirstTaskCheck(fixture.Controller, true); complete.IsChecked = true; Click(complete);
                Assert(new PlannerStore(fixture.Path).Data.Tasks.Single(t => t.IsWeekly).Completed && Control<ProgressBar>(fixture.Controller, "WeekProgress").Value == 100, "week completion persists and updates its progress");
                string timestamp = week.CompletedAt;
                Invoke(fixture.Controller, "CommitTask", week, "修改后的周目标", "新备注", DateTime.Today, true, true);
                Assert(week.CompletedAt == timestamp, "editing a completed week goal keeps its completion time");
                MenuItem move = (MenuItem)FirstTaskMenu(fixture.Controller, true).Items[1];
                Assert(move.Header.ToString() == "移到下周", "weekly rescheduling uses weeks");
                move.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, move));
                Assert(week.Date == Day(PlannerDates.StartOfWeek(DateTime.Today).AddDays(7)) && week.IsWeekly, "move-to-next-week preserves the plan type");
                Click(Control<Button>(fixture.Controller, "WeekTomorrowButton"));
                CheckBox undo = FirstTaskCheck(fixture.Controller, true); undo.IsChecked = false; Click(undo);
                Assert(!week.Completed && week.CompletedAt == String.Empty, "weekly completion can be undone");
                Invoke(fixture.Controller, "CommitTask", week, "安排到明天", "日计划备注", DateTime.Today.AddDays(1), false, false);
                TaskItem converted = new PlannerStore(fixture.Path).Data.Tasks.Single(t => t.Id == week.Id);
                Assert(!converted.IsWeekly && converted.Date == Day(DateTime.Today.AddDays(1)) && fixture.Store.Data.Tasks.Count == 2, "week-to-day conversion preserves identity and uses the exact day");
                Assert(GetField<DateTime>(fixture.Controller, "selectedDay") == DateTime.Today.AddDays(1) && GetField<DateTime>(fixture.Controller, "selectedWeek") == PlannerDates.StartOfWeek(DateTime.Today).AddDays(7), "conversion selects the resulting day and retains the chosen week");
            }
        }

        private static void WeeklyProgressSliders()
        {
            DateTime monday = PlannerDates.StartOfWeek(DateTime.Today);
            TaskItem first = Task("第一项周目标", monday, false), second = Task("第二项周目标", monday, false);
            first.Scope = second.Scope = TaskItem.WeeklyScope; second.Progress = 20;
            TaskItem daily = Task("独立的日计划", DateTime.Today, true);
            using (Fixture fixture = new Fixture("weekly-sliders", first, second, daily))
            {
                StackPanel list = Control<StackPanel>(fixture.Controller, "WeekTaskList");
                Assert(Descendants<Slider>(list).Count() == 2 && !Descendants<Slider>(Control<StackPanel>(fixture.Controller, "TaskList")).Any(), "every weekly task has its own slider");
                Slider slider = Descendants<Slider>(list).First();
                slider.Value = 40;
                Assert(new PlannerStore(fixture.Path).Data.Tasks.Single(t => t.Id == first.Id).Progress == 0, "drag preview does not write every intermediate value");
                slider.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                Assert(first.Progress == 40 && new PlannerStore(fixture.Path).Data.Tasks.Single(t => t.Id == first.Id).Progress == 40, "releasing the slider persists its percentage");
                Assert(Control<ProgressBar>(fixture.Controller, "WeekProgress").Value == 30 && Control<TextBlock>(fixture.Controller, "WeekPercentText").Text == "30%", "overall weekly progress averages the individual percentages");
                Assert(Control<ProgressBar>(fixture.Controller, "DayProgress").Value == 100, "weekly adjustments retain daily progress");
                Invoke(fixture.Controller, "CommitTask", first, "修改后的标题", "新备注", monday.AddDays(7), true, false);
                Assert(first.Progress == 40, "editing and moving to another week preserve partial progress");
                Invoke(fixture.Controller, "SelectWeek", monday.AddDays(7), "next-week");
                slider = Descendants<Slider>(list).Single(); slider.Value = 100;
                slider.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                Assert(first.Completed && !String.IsNullOrEmpty(first.CompletedAt) && first.Progress == 100, "100 percent completes the task and records its time");
                string completedAt = first.CompletedAt;
                Invoke(fixture.Controller, "CommitTask", first, first.Title, "再次编辑", monday.AddDays(7), true, true);
                Assert(first.CompletedAt == completedAt, "editing completed goals keeps the completion history");
                slider = Descendants<Slider>(list).Single(); slider.Value = 75;
                slider.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                Assert(!first.Completed && first.CompletedAt == "" && first.Progress == 75, "reducing progress reopens the task and clears the completion time");
                CheckBox check = FirstTaskCheck(fixture.Controller, true); check.IsChecked = true; Click(check);
                Assert(first.Progress == 100, "checking a weekly task sets full progress");
                check = FirstTaskCheck(fixture.Controller, true); check.IsChecked = false; Click(check);
                Assert(first.Progress == 0 && !first.Completed, "unchecking resets weekly progress");
                Invoke(fixture.Controller, "SetWeeklyProgress", first, 55);
                Invoke(fixture.Controller, "CommitTask", first, first.Title, first.Notes, DateTime.Today, false, false);
                Assert(first.Progress == 0 && !first.IsWeekly, "conversion to daily clears the weekly percentage");
            }
        }

        private static void WeeklyProgressRollback()
        {
            TaskItem task = Task("保留进度", PlannerDates.StartOfWeek(DateTime.Today), false);
            task.Scope = TaskItem.WeeklyScope; task.Progress = 35;
            using (Fixture fixture = new Fixture("weekly-progress-rollback", task))
            {
                PlannerStore external = new PlannerStore(fixture.Path);
                external.Data.Tasks.Add(Task("外部新增记录", DateTime.Today, false)); external.Save();
                bool saved = (bool)Invoke(fixture.Controller, "SetWeeklyProgress", task, 100);
                Assert(!saved && task.Progress == 35 && !task.Completed && task.CompletedAt == "", "failed progress saves restore percentage and completion state");
                Assert(Descendants<Slider>(Control<StackPanel>(fixture.Controller, "WeekTaskList")).Single().Value == 35, "the slider returns to its saved percentage");
                Assert(new PlannerStore(fixture.Path).Data.Tasks.Count == 2 && new PlannerStore(fixture.Path).Data.Tasks.Single(t => t.Id == task.Id).Progress == 35, "external records and prior progress remain untouched");
            }
        }

        private static void JournalLifecycle()
        {
            using (Fixture fixture = new Fixture("journal-lifecycle", Task("保留的计划", DateTime.Today, true)))
            {
                Assert(Control<Button>(fixture.Controller, "JournalButton").Content.ToString() == "日记", "the diary entry is in the daily date bar");
                Invoke(fixture.Controller, "CommitJournal", DateTime.Today.AddDays(-3), "早些时候的学习记录");
                Window dialog = (Window)Invoke(fixture.Controller, "CreateJournalDialog", DateTime.Today);
                FrameworkElement content = (FrameworkElement)dialog.Content; content.Measure(new Size(dialog.Width, dialog.Height)); content.Arrange(new Rect(0, 0, dialog.Width, dialog.Height)); content.UpdateLayout();
                TextBox editor = Descendants<TextBox>(content).Single(c => c.Name == "JournalEditor");
                DatePicker date = Descendants<DatePicker>(content).Single();
                StackPanel history = Descendants<StackPanel>(content).Single(c => c.Name == "JournalHistory");
                Assert(history.Children.Count == 1 && Descendants<TextBlock>(history).Any(t => t.Text == "早些时候的学习记录"), "only saved dates appear with their full contents");
                editor.Text = "今天学了值迭代\n还看了论文";
                date.SelectedDate = DateTime.Today.AddDays(-3);
                Assert(new PlannerStore(fixture.Path).Data.Journal.Single(j => j.Date == Day(DateTime.Today)).Content.Contains("还看了论文"), "changing dates saves the outgoing day's draft");
                Assert(editor.Text == "早些时候的学习记录" && history.Children.Count == 2, "the chosen date loads its existing diary");
                TextBlock firstDate = Descendants<TextBlock>((Border)history.Children[0]).First();
                Assert(firstDate.Text.StartsWith(DateTime.Today.ToString("yyyy/M/d")), "history sorts newest recorded dates first");
                editor.Text = "补充了过去的记录"; dialog.Close();
                Assert(new PlannerStore(fixture.Path).Data.Journal.Single(j => j.Date == Day(DateTime.Today.AddDays(-3))).Content == "补充了过去的记录", "closing the diary saves the current draft");
                Invoke(fixture.Controller, "CommitJournal", DateTime.Today.AddDays(-1), "  ");
                Assert(fixture.Store.Data.Journal.Count == 2, "blank unwritten dates do not create entries");
                Invoke(fixture.Controller, "CommitJournal", DateTime.Today, "");
                Assert(new PlannerStore(fixture.Path).Data.Journal.Count == 1, "clearing a record removes its date from diary history");
                Assert(fixture.Store.Data.Tasks.Single().Completed && new PlannerStore(fixture.Path).Data.Tasks.Count == 1, "diary operations retain the original tasks");
            }
        }

        private static void JournalRollback()
        {
            using (Fixture fixture = new Fixture("journal-rollback"))
            {
                Invoke(fixture.Controller, "CommitJournal", DateTime.Today, "原日记");
                JournalEntry original = fixture.Store.Data.Journal.Single();
                Window dialog = (Window)Invoke(fixture.Controller, "CreateJournalDialog", DateTime.Today);
                FrameworkElement content = (FrameworkElement)dialog.Content; content.Measure(new Size(dialog.Width, dialog.Height)); content.Arrange(new Rect(0, 0, dialog.Width, dialog.Height)); content.UpdateLayout();
                TextBox editor = Descendants<TextBox>(content).Single(c => c.Name == "JournalEditor"); DatePicker date = Descendants<DatePicker>(content).Single();
                PlannerStore external = new PlannerStore(fixture.Path); external.Data.Journal.Add(new JournalEntry { Date = Day(DateTime.Today.AddDays(-1)), Content = "外部新增记录" }); external.Save();
                editor.Text = "尚未保存的文字"; date.SelectedDate = DateTime.Today.AddDays(-2);
                Assert(date.SelectedDate == DateTime.Today && editor.Text == "尚未保存的文字", "failed saves keep the selected date and the editor draft");
                Assert(Object.ReferenceEquals(fixture.Store.Data.Journal.Single(), original) && original.Content == "原日记", "rollback preserves the existing diary object and contents");
                bool closed = false; dialog.Closed += delegate { closed = true; }; dialog.Close();
                Assert(!closed && editor.Text == "尚未保存的文字", "a save failure prevents closing and losing the draft");
                Assert(new PlannerStore(fixture.Path).Data.Journal.Count == 2, "failed edits do not overwrite external diary records");
                editor.Text = "原日记"; dialog.Close(); Assert(closed, "an unchanged editor can close after a failed save");
            }
        }

        private static void JournalBrowsing()
        {
            using (Fixture fixture = new Fixture("journal-browsing"))
            {
                for (int i = 0; i < 2000; i++) fixture.Store.Data.Journal.Add(new JournalEntry { Date = Day(DateTime.Today.AddDays(-i - 1)), Content = new String('文', 300) + (i == 137 ? "Rare_Search_Marker" : "学习心得"), UpdatedAt = "" });
                fixture.Store.Save();
                Window dialog = (Window)Invoke(fixture.Controller, "CreateJournalDialog", DateTime.Today);
                FrameworkElement content = (FrameworkElement)dialog.Content;
                foreach (Size size in new[] { new Size(700, 520), new Size(780, 660) })
                {
                    content.Measure(size); content.Arrange(new Rect(new Point(0, 0), size)); content.UpdateLayout();
                    Assert(Descendants<TextBox>(content).Single(c => c.Name == "JournalEditor").ActualHeight > 120, "the writing area remains usable at both supported window sizes");
                    Assert(Descendants<ScrollViewer>(content).Single(c => c.Name == "JournalHistoryScroll").ViewportHeight > 100, "history keeps an independent bounded scrolling area");
                }
                StackPanel history = Descendants<StackPanel>(content).Single(c => c.Name == "JournalHistory");
                TextBox search = Descendants<TextBox>(content).Single(c => c.Name == "JournalSearch"), editor = Descendants<TextBox>(content).Single(c => c.Name == "JournalEditor");
                ComboBox month = Descendants<ComboBox>(content).Single(c => c.Name == "JournalMonth");
                Button next = Descendants<Button>(content).Single(c => c.Name == "JournalNextPage");
                Assert(history.Children.OfType<Border>().Count() == 8, "thousands of entries produce only one page of cards");
                string firstDate = Descendants<TextBlock>((Border)history.Children[0]).First().Text; Click(next);
                Assert(history.Children.OfType<Border>().Count() == 8 && Descendants<TextBlock>((Border)history.Children[0]).First().Text != firstDate, "pagination reaches a distinct next page");
                month.SelectedIndex = 1;
                string selectedMonth = (string)((ComboBoxItem)month.SelectedItem).Tag;
                Assert(history.Children.OfType<Border>().Count() == Math.Min(8, fixture.Store.Data.Journal.Count(j => j.Date.StartsWith(selectedMonth + "-"))), "month selection filters and resets pagination");
                month.SelectedIndex = 0; search.Text = "Rare_Search_Marker";
                Assert(history.Children.OfType<Border>().Count() == 1, "full-text search finds a marker outside the visible preview");
                editor.Text = "今天还没保存的草稿";
                Click(Descendants<Button>(history).Single(c => c.Content.ToString() == "打开"));
                Assert(editor.Text.Length > 300 && editor.Text.EndsWith("Rare_Search_Marker"), "opening an excerpt loads the full record for reading and editing");
                Assert(new PlannerStore(fixture.Path).Data.Journal.Single(j => j.Date == Day(DateTime.Today)).Content == "今天还没保存的草稿", "opening history saves the outgoing draft to its original date");
                search.Text = "no-such-record";
                Assert(history.Children.OfType<Border>().Count() == 0 && !next.IsEnabled, "no-match state has no phantom records or next page");
                ScrollViewer scroll = Descendants<ScrollViewer>(content).Single(c => c.Name == "JournalHistoryScroll");
                Assert(!scroll.Focusable && scroll.FocusVisualStyle == null, "the history region has no large dotted focus outline");
                dialog.Close();
            }
        }

        private static void SimultaneousPanels()
        {
            DateTime monday = PlannerDates.StartOfWeek(DateTime.Today), future = DateTime.Today.AddDays(9);
            TaskItem goal = Task("周目标", monday, false); goal.Scope = TaskItem.WeeklyScope;
            TaskItem next = Task("下周目标", monday.AddDays(7), true); next.Scope = TaskItem.WeeklyScope;
            using (Fixture fixture = new Fixture("stacked-panels", goal, next, Task("今天的日计划", DateTime.Today, true), Task("未来的日计划", future, false)))
            {
                Assert(Control<StackPanel>(fixture.Controller, "TaskList").Children.OfType<Border>().Count() == 1 && Control<StackPanel>(fixture.Controller, "WeekTaskList").Children.OfType<Border>().Count() == 1, "both scopes are displayed together without mixing records");
                Assert(Control<ProgressBar>(fixture.Controller, "WeekProgress").Value == 0 && Control<ProgressBar>(fixture.Controller, "DayProgress").Value == 100, "both initial progress values are independent");
                Border dailyRow = (Border)Control<StackPanel>(fixture.Controller, "TaskList").Children[0];
                Click(Control<Button>(fixture.Controller, "WeekTomorrowButton"));
                Assert(GetField<DateTime>(fixture.Controller, "selectedDay") == DateTime.Today && Object.ReferenceEquals(dailyRow, Control<StackPanel>(fixture.Controller, "TaskList").Children[0]), "week navigation leaves the daily date and list intact");
                Assert(Control<ProgressBar>(fixture.Controller, "WeekProgress").Value == 100 && Control<ProgressBar>(fixture.Controller, "DayProgress").Value == 100, "week navigation updates only the week progress");
                Border weeklyRow = (Border)Control<StackPanel>(fixture.Controller, "WeekTaskList").Children[0];
                Invoke(fixture.Controller, "SelectDate", future, "date");
                Assert(GetField<DateTime>(fixture.Controller, "selectedWeek") == monday.AddDays(7) && Object.ReferenceEquals(weeklyRow, Control<StackPanel>(fixture.Controller, "WeekTaskList").Children[0]), "daily calendar selection retains the week and its list");
                Assert(Control<ProgressBar>(fixture.Controller, "WeekProgress").Value == 100 && Control<ProgressBar>(fixture.Controller, "DayProgress").Value == 0, "daily navigation updates only the daily progress");
                Invoke(fixture.Controller, "CommitTask", next, "修改后的下周目标", "", monday.AddDays(7), true, true);
                Assert(GetField<DateTime>(fixture.Controller, "selectedDay") == future && Control<StackPanel>(fixture.Controller, "TaskList").Children.OfType<Border>().Count() == 1, "editing a week goal does not change the daily selection");
            }
        }

        private static void AddEditorDefaults()
        {
            using (Fixture fixture = new Fixture("add-editors"))
            {
                DateTime day = DateTime.Today.AddDays(12), week = PlannerDates.StartOfWeek(DateTime.Today).AddDays(14);
                Invoke(fixture.Controller, "SelectDate", day, "date"); Invoke(fixture.Controller, "SelectWeek", week, "week");
                foreach (bool weekly in new[] { false, true })
                {
                    Window dialog = (Window)Invoke(fixture.Controller, "CreateTaskDialog", null, weekly);
                    FrameworkElement content = (FrameworkElement)dialog.Content;
                    content.Measure(new Size(420, 560)); content.Arrange(new Rect(0, 0, 420, 560)); content.UpdateLayout();
                    Assert(Descendants<ComboBox>(content).Single().SelectedIndex == (weekly ? 1 : 0), "add editor opens with its section's plan type");
                    Assert(Descendants<DatePicker>(content).Single().SelectedDate == (weekly ? week : day), "add editor uses its section's selected period");
                    Assert(!dialog.IsVisible && fixture.Store.Data.Tasks.Count == 0, "opening an unsaved editor leaves data intact");
                    dialog.Close();
                }
            }
        }

        private static void RememberedLayout()
        {
            string path = Path.Combine(testRoot, "remembered-layout", "plans.json");
            PlannerStore store = new PlannerStore(path); store.Data.Settings.Height = 547.5; store.Save();
            PlannerController first = new PlannerController(new PlannerStore(path), true);
            Assert(first.Window.Height >= 920 && new PlannerStore(path).Data.Settings.Height == 547.5, "old small layout extends without changing user data before loading"); first.Window.Close();
            store.Data.Settings.StackedLayout = true; store.Data.Settings.Height = 760; store.Save();
            PlannerStore reopened = new PlannerStore(path); PlannerController resized = new PlannerController(reopened, true);
            Assert(reopened.Data.Settings.StackedLayout && resized.Window.Height == 760, "a saved stacked height survives reopening"); resized.Window.Close();
        }

        private static void TaskbarClearance()
        {
            MethodInfo reserve = typeof(PlannerController).GetMethod("ReserveTaskbar", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo fit = typeof(PlannerController).GetMethod("FitWindowBounds", BindingFlags.NonPublic | BindingFlags.Static);
            foreach (Rect reported in new[] { new Rect(0, 0, 1600, 1000), new Rect(0, 0, 1600, 952) })
            {
                Rect safe = (Rect)reserve.Invoke(null, new object[] { reported, 1000.0 });
                Rect saved = (Rect)fit.Invoke(null, new object[] { safe, new Rect(1145.5, 6.5, 453.5, 976) });
                Assert(saved.Bottom < 952 && saved.Width == 453.5 && saved.X == 1145.5, "the previous tall widget now ends above a 48-pixel taskbar without moving sideways");
                Rect resized = (Rect)fit.Invoke(null, new object[] { safe, new Rect(1500, 400, 900, 2000) });
                Assert(safe.Contains(resized), "oversized windows return inside the reserved desktop area");
            }
            Rect smallerWork = (Rect)reserve.Invoke(null, new object[] { new Rect(0, 0, 1600, 900), 1000.0 });
            Assert(smallerWork.Bottom == 900, "larger taskbars already excluded by Windows remain respected");
            Rect secondary = (Rect)reserve.Invoke(null, new object[] { new Rect(-1600, -1000, 1600, 1000), 0.0 });
            Rect onSecondary = (Rect)fit.Invoke(null, new object[] { secondary, new Rect(-1700, -900, 453.5, 976) });
            Assert(secondary.Contains(onSecondary) && onSecondary.Bottom <= -64, "negative-coordinate monitors retain their own taskbar gap");
        }

        private static void WeeklyNavigation()
        {
            using (Fixture fixture = new Fixture("weekly-navigation"))
            {
                Invoke(fixture.Controller, "SelectWeek", new DateTime(2026, 12, 31), "week");
                Assert(GetField<DateTime>(fixture.Controller, "selectedWeek") == new DateTime(2026, 12, 28), "calendar-selected Thursday belongs to its Monday week");
                Assert(Control<TextBlock>(fixture.Controller, "WeekDateText").Text.Contains("26.12.28") && Control<TextBlock>(fixture.Controller, "WeekDateText").Text.Contains("27.1.3"), "year-spanning range includes both years");
                Click(Control<Button>(fixture.Controller, "WeekNextButton"));
                Assert(GetField<DateTime>(fixture.Controller, "selectedWeek") == new DateTime(2027, 1, 4), "next arrow advances seven days across the year");
                Click(Control<Button>(fixture.Controller, "WeekPreviousButton"));
                Assert(GetField<DateTime>(fixture.Controller, "selectedWeek") == new DateTime(2026, 12, 28), "previous arrow returns one week");
                Click(Control<Button>(fixture.Controller, "WeekTodayButton"));
                SetField(fixture.Controller, "currentDay", DateTime.Today.AddDays(-7));
                SetField(fixture.Controller, "selectedWeek", PlannerDates.StartOfWeek(DateTime.Today).AddDays(-7));
                Invoke(fixture.Controller, "RefreshDay");
                Assert(GetField<DateTime>(fixture.Controller, "selectedWeek") == PlannerDates.StartOfWeek(DateTime.Today), "relative this-week view follows week rollover");
                Click(Control<Button>(fixture.Controller, "WeekTomorrowButton"));
                SetField(fixture.Controller, "currentDay", DateTime.Today.AddDays(-7)); Invoke(fixture.Controller, "RefreshDay");
                Assert(GetField<DateTime>(fixture.Controller, "selectedWeek") == PlannerDates.StartOfWeek(DateTime.Today).AddDays(7), "relative next-week view follows rollover");
                Invoke(fixture.Controller, "SelectWeek", new DateTime(2026, 12, 31), "week");
                SetField(fixture.Controller, "currentDay", DateTime.Today.AddDays(-1)); Invoke(fixture.Controller, "RefreshDay");
                Assert(GetField<DateTime>(fixture.Controller, "selectedWeek") == new DateTime(2026, 12, 28), "fixed week selection survives day rollover");
                Invoke(fixture.Controller, "SelectWeek", DateTime.MaxValue, "week"); Click(Control<Button>(fixture.Controller, "WeekNextButton"));
                Assert(GetField<DateTime>(fixture.Controller, "selectedWeek") == PlannerDates.StartOfWeek(DateTime.MaxValue), "final week navigation stays within the calendar");
            }
        }

        private static void WeeklyOverdue()
        {
            DateTime monday = PlannerDates.StartOfWeek(DateTime.Today);
            TaskItem current = Task("本周尚未过期", monday, false); current.Scope = TaskItem.WeeklyScope;
            TaskItem past = Task("上周目标", monday.AddDays(-7), false); past.Scope = TaskItem.WeeklyScope;
            TaskItem future = Task("下周目标", monday.AddDays(7), false); future.Scope = TaskItem.WeeklyScope;
            using (Fixture fixture = new Fixture("weekly-overdue", current, past, future, Task("昨天日计划", DateTime.Today.AddDays(-1), false)))
            {
                Assert(Control<Button>(fixture.Controller, "WeekOverdueButton").Content.ToString().Contains("还有 1 项"), "only the past weekly target is overdue");
                Invoke(fixture.Controller, "SelectWeek", monday.AddDays(-7), "week");
                MenuItem move = (MenuItem)FirstTaskMenu(fixture.Controller, true).Items[0]; move.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, move));
                Assert(new PlannerStore(fixture.Path).Data.Tasks.Single(t => t.Id == past.Id).Date == Day(monday), "past weekly target moves to the current Monday");
                Assert(Control<Button>(fixture.Controller, "WeekOverdueButton").Visibility == Visibility.Collapsed, "current week does not count as overdue");
                Assert(Control<Button>(fixture.Controller, "OverdueButton").Content.ToString().Contains("还有 1 项"), "daily overdue count is independent");
            }
        }

        private static void WeeklyLayout()
        {
            List<TaskItem> tasks = new List<TaskItem>();
            for (int index = 0; index < 25; index++)
            {
                TaskItem task = Task(index + " · " + new String('字', 120), PlannerDates.StartOfWeek(DateTime.Today), index % 3 == 0);
                task.Scope = TaskItem.WeeklyScope; tasks.Add(task);
                tasks.Add(Task(index + " · " + new String('日', 120), DateTime.Today, index % 3 == 0));
            }
            using (Fixture fixture = new Fixture("weekly-layout", tasks.ToArray()))
            {
                FrameworkElement content = (FrameworkElement)fixture.Controller.Window.Content;
                foreach (Size size in new[] { new Size(380, 650), new Size(453.5, 920) })
                {
                    content.Measure(size); content.Arrange(new Rect(new Point(0, 0), size)); content.UpdateLayout();
                    foreach (string prefix in new[] { "Week", "" })
                    {
                        Button add = Control<Button>(fixture.Controller, prefix + "AddButton");
                        Rect bounds = add.TransformToAncestor(content).TransformBounds(new Rect(new Point(0, 0), add.RenderSize));
                        Assert(add.ActualHeight > 0 && bounds.Bottom <= size.Height && bounds.Right <= size.Width, "both add buttons remain inside the window");
                        ScrollViewer scroller = Control<ScrollViewer>(fixture.Controller, prefix == "Week" ? "WeekScroller" : "DayScroller");
                        Assert(scroller.ViewportHeight > 70 && scroller.ExtentHeight > scroller.ViewportHeight, "each long list has its own usable scroll viewport");
                    }
                    Grid week = Control<Grid>(fixture.Controller, "WeekSection"), day = Control<Grid>(fixture.Controller, "DaySection");
                    Rect weekBounds = week.TransformToAncestor(content).TransformBounds(new Rect(new Point(0, 0), week.RenderSize));
                    Rect dayBounds = day.TransformToAncestor(content).TransformBounds(new Rect(new Point(0, 0), day.RenderSize));
                    Assert(weekBounds.Bottom < dayBounds.Top, "weekly plans stay above daily plans without overlap");
                }
                ScrollViewer weekScroll = Control<ScrollViewer>(fixture.Controller, "WeekScroller"), dayScroll = Control<ScrollViewer>(fixture.Controller, "DayScroller");
                weekScroll.ScrollToVerticalOffset(80); content.UpdateLayout();
                Assert(weekScroll.VerticalOffset > 0 && dayScroll.VerticalOffset == 0, "scrolling the week leaves daily plans at the top");
                double weekOffset = weekScroll.VerticalOffset; dayScroll.ScrollToVerticalOffset(110); content.UpdateLayout();
                Assert(dayScroll.VerticalOffset > 0 && weekScroll.VerticalOffset == weekOffset, "scrolling daily plans preserves the week position");
            }
        }

        private static IEnumerable<T> Descendants<T>(DependencyObject element) where T : DependencyObject
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(element, index);
                T match = child as T;
                if (match != null) yield return match;
                foreach (T descendant in Descendants<T>(child)) yield return descendant;
            }
        }
    }
}
