using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DeskPlanner
{
    internal static class Program
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        internal static uint ShowMessage;

        [STAThread]
        public static void Main(string[] args)
        {
            try
            {
                if (args.Contains("--enable-startup")) { StartupManager.SetEnabled(true); return; }
                if (args.Contains("--disable-startup")) { StartupManager.SetEnabled(false); return; }
                string root = AppDomain.CurrentDomain.BaseDirectory;
                int dataIndex = Array.IndexOf(args, "--data-dir");
                string dataDir = dataIndex >= 0 && dataIndex + 1 < args.Length ? Path.GetFullPath(args[dataIndex + 1]) : Path.Combine(root, "data");
                string identity;
                using (var hash = System.Security.Cryptography.SHA256.Create()) identity = BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(dataDir.ToLowerInvariant()))).Replace("-", "");
                ShowMessage = RegisterWindowMessage("DeskPlanner.Show.v1." + identity);
                bool created;
                using (Mutex mutex = new Mutex(true, "Local\\DeskPlanner." + identity, out created))
                {
                    if (!created) { PostMessage(new IntPtr(0xffff), ShowMessage, IntPtr.Zero, IntPtr.Zero); return; }
                    var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                    app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                    {
                        MessageBox.Show("操作没有完成：" + e.Exception.Message + "\n请重试；若显示保存失败，请先保留窗口。", "每日计划", MessageBoxButton.OK, MessageBoxImage.Warning);
                        e.Handled = true;
                    };
                    var planner = new PlannerController(new PlannerStore(Path.Combine(dataDir, "plans.json")), dataIndex >= 0);
                    app.Run(planner.Window);
                    mutex.ReleaseMutex();
                }
            }
            catch (Exception e)
            {
                MessageBox.Show("每日计划无法启动：\n" + e.Message, "每日计划", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    internal static class StartupManager
    {
        internal static string ShortcutPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "每日计划.lnk"); } }
        internal static bool IsEnabled { get { return File.Exists(ShortcutPath); } }
        internal static void SetEnabled(bool enabled)
        {
            if (!enabled) { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); return; }
            object shell = null, shortcut = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                shortcut = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { ShortcutPath });
                Type type = shortcut.GetType();
                string exe = Assembly.GetExecutingAssembly().Location;
                type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { exe });
                type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(exe) });
                type.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "登录后在桌面右上角显示每日计划" });
                type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { exe + ",0" });
                type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    internal static class WidgetWindowStyle
    {
        private const int ExtendedStyle = -20;
        private const long ToolWindow = 0x80, AppWindow = 0x40000;
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetStyle32(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetStyle64(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetStyle32(IntPtr window, int index, int value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetStyle64(IntPtr window, int index, IntPtr value);

        internal static void Apply(IntPtr window)
        {
            long style = IntPtr.Size == 8 ? GetStyle64(window, ExtendedStyle).ToInt64() : GetStyle32(window, ExtendedStyle);
            style = (style | ToolWindow) & ~AppWindow;
            if (IntPtr.Size == 8) SetStyle64(window, ExtendedStyle, new IntPtr(style));
            else SetStyle32(window, ExtendedStyle, (int)style);
        }
    }

    internal sealed class PlannerController
    {
        internal Window Window { get; private set; }
        private readonly PlannerStore store;
        private readonly bool testMode;
        private DateTime currentDay = DateTime.Today;
        private DateTime selectedDay = DateTime.Today;
        private string selectionMode = "today";
        private DateTime selectedWeek = PlannerDates.StartOfWeek(DateTime.Today);
        private string weekSelectionMode = "this-week";
        private Forms.NotifyIcon tray;
        private System.Drawing.Icon trayIcon;
        private DispatcherTimer clock, geometryTimer;
        private Brush defaultBackground;
        private string loadedBackgroundPath = null;
        private BitmapSource backgroundBitmap;
        private BitmapSource weeklyQuoteBitmap;
        private bool loaded;
        private string lastErrorNotification;
        private readonly Brush ink = ColorBrush("#243653"), muted = ColorBrush("#68768D"), green = ColorBrush("#4A8A74");
        private static Brush ColorBrush(string color) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); }
        private T Find<T>(string name) where T : class { return Window.FindName(name) as T; }
        private static string DayKey(DateTime day) { return day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }

        internal PlannerController(PlannerStore storage, bool testing)
        {
            store = storage;
            testMode = testing;
            if (!testMode) store.Data.Settings.AutoStart = StartupManager.IsEnabled;
            using (Stream xaml = Assembly.GetExecutingAssembly().GetManifestResourceStream("DeskPlanner.MainWindow.xaml")) Window = (Window)XamlReader.Load(xaml);
            defaultBackground = Find<Border>("RootSurface").Background;
            using (Stream quote = Assembly.GetExecutingAssembly().GetManifestResourceStream("DeskPlanner.WeeklyQuote.png"))
            {
                if (quote != null)
                {
                    var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = quote; bitmap.EndInit(); bitmap.Freeze(); weeklyQuoteBitmap = bitmap;
                }
            }
            Find<Image>("WeekWatermarkTop").Source = weeklyQuoteBitmap;
            Find<Image>("WeekWatermarkBottom").Source = weeklyQuoteBitmap;
            Window.Topmost = store.Data.Settings.AlwaysOnTop;
            Window.Opacity = Math.Max(.80, Math.Min(1, store.Data.Settings.Opacity));
            Window.Width = store.Data.Settings.Width;
            Window.Height = store.Data.Settings.StackedLayout ? store.Data.Settings.Height : Math.Max(920, store.Data.Settings.Height);
            WireEvents();
            Render();
            Window.Loaded += delegate
            {
                PlaceWindow();
                loaded = true;
                SaveGeometry();
                SetupTray();
                if (!String.IsNullOrEmpty(store.LoadWarning))
                {
                    SetStatus("数据恢复提示 · 点击设置查看", true);
                    MessageBox.Show(Window, store.LoadWarning, "每日计划 · 数据恢复", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            };
            Window.SourceInitialized += delegate
            {
                HwndSource source = HwndSource.FromHwnd(new WindowInteropHelper(Window).Handle);
                WidgetWindowStyle.Apply(source.Handle);
                source.AddHook(delegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
                {
                    if (msg == Program.ShowMessage) { ShowWindow(); handled = true; }
                    if (msg == 0x0218 || msg == 0x007e) Window.Dispatcher.BeginInvoke(new Action(delegate { RefreshDay(); ClampWindow(); }));
                    return IntPtr.Zero;
                });
            };
        }

        private void WireEvents()
        {
            Find<Button>("WeekTodayButton").Click += delegate { SelectWeek(DateTime.Today, "this-week"); };
            Find<Button>("WeekTomorrowButton").Click += delegate { SelectWeek(PlannerDates.ShiftDays(PlannerDates.StartOfWeek(DateTime.Today), 7), "next-week"); };
            Find<Button>("WeekCalendarButton").Click += delegate { PickDate(true); };
            Find<Button>("WeekPreviousButton").Click += delegate { SelectWeek(PlannerDates.ShiftDays(selectedWeek, -7), "week"); };
            Find<Button>("WeekNextButton").Click += delegate { SelectWeek(PlannerDates.ShiftDays(selectedWeek, 7), "week"); };
            Find<Button>("WeekAddButton").Click += delegate { EditTask(null, true); };
            Find<Button>("WeekOverdueButton").Click += delegate { ShowOverdue(true); };
            Find<Button>("TodayButton").Click += delegate { SelectDate(DateTime.Today, "today"); };
            Find<Button>("TomorrowButton").Click += delegate { SelectDate(PlannerDates.ShiftDays(DateTime.Today, 1), "tomorrow"); };
            Find<Button>("CalendarButton").Click += delegate { PickDate(false); };
            Find<Button>("PreviousButton").Click += delegate { SelectDate(PlannerDates.ShiftDays(selectedDay, -1), "date"); };
            Find<Button>("NextButton").Click += delegate { SelectDate(PlannerDates.ShiftDays(selectedDay, 1), "date"); };
            Find<Button>("AddButton").Click += delegate { EditTask(null); };
            Find<Button>("JournalButton").Click += delegate { CreateJournalDialog(selectedDay).ShowDialog(); };
            Find<Button>("PinButton").Click += delegate
            {
                if (Change(delegate { store.Data.Settings.AlwaysOnTop = !store.Data.Settings.AlwaysOnTop; })) Window.Topmost = store.Data.Settings.AlwaysOnTop;
                RenderPin();
            };
            Find<Button>("SettingsButton").Click += delegate { ShowSettings(); };
            Find<Button>("HideButton").Click += delegate { Window.Hide(); };
            Find<Button>("ExitButton").Click += delegate { Window.Close(); };
            Find<Button>("OverdueButton").Click += delegate { ShowOverdue(false); };
            Find<Grid>("DragArea").MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                DependencyObject element = e.OriginalSource as DependencyObject;
                while (element != null)
                {
                    if (element is Button) return;
                    element = VisualTreeHelper.GetParent(element);
                }
                if (e.ButtonState == MouseButtonState.Pressed) Window.DragMove();
            };
            Window.Activated += delegate { RefreshDay(); };
            Find<Canvas>("DailyCalligraphySurface").SizeChanged += delegate { LayoutDailyCalligraphy(); };
            Window.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.N && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { EditTask(null, (Keyboard.Modifiers & ModifierKeys.Shift) != 0); e.Handled = true; }
                if (e.Key == Key.Escape) { Window.Hide(); e.Handled = true; }
            };
            geometryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            geometryTimer.Tick += delegate { geometryTimer.Stop(); ClampWindow(); SaveGeometry(); };
            Window.LocationChanged += delegate { QueueGeometry(); };
            Window.SizeChanged += delegate { QueueGeometry(); };
            Window.Closing += delegate { geometryTimer.Stop(); SaveGeometry(); };
            Window.Closed += delegate { clock.Stop(); geometryTimer.Stop(); if (tray != null) tray.Dispose(); if (trayIcon != null) trayIcon.Dispose(); };
            clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            clock.Tick += delegate { RefreshDay(); };
            clock.Start();
        }

        private void PlaceWindow()
        {
            Rect work = GetWorkArea();
            Window.MinHeight = Math.Min(650, Math.Max(410, work.Height - 16));
            if (!store.Data.Settings.StackedLayout) Window.Height = Math.Min(Window.MaxHeight, work.Height - 24);
            Window.Width = Math.Min(Window.Width, work.Width - 16);
            Window.Height = Math.Min(Window.Height, work.Height - 16);
            Window.Left = store.Data.Settings.Left >= 0 ? store.Data.Settings.Left : work.Right - Window.Width - 14;
            Window.Top = store.Data.Settings.Top >= 0 ? store.Data.Settings.Top : work.Top + 12;
            ClampWindow();
        }

        private Rect GetWorkArea()
        {
            Forms.Screen screen = Forms.Screen.PrimaryScreen;
            if (loaded) screen = Forms.Screen.FromHandle(new WindowInteropHelper(Window).Handle);
            var area = screen.WorkingArea;
            PresentationSource source = PresentationSource.FromVisual(Window);
            Matrix transform = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformFromDevice : Matrix.Identity;
            Point topLeft = transform.Transform(new Point(area.Left, area.Top));
            Point bottomRight = transform.Transform(new Point(area.Right, area.Bottom));
            double monitorBottom = transform.Transform(new Point(screen.Bounds.Right, screen.Bounds.Bottom)).Y;
            return ReserveTaskbar(new Rect(topLeft, bottomRight), monitorBottom);
        }

        private static Rect ReserveTaskbar(Rect work, double monitorBottom)
        {
            // Auto-hidden taskbars may report the full monitor as the work area.
            double bottom = Math.Min(work.Bottom, monitorBottom - 64);
            return new Rect(work.Left, work.Top, work.Width, Math.Max(0, bottom - work.Top));
        }

        private static Rect FitWindowBounds(Rect work, Rect window)
        {
            double width = Math.Min(window.Width, Math.Max(1, work.Width - 16));
            double height = Math.Min(window.Height, Math.Max(1, work.Height - 16));
            double left = Math.Max(work.Left, Math.Min(window.Left, work.Right - width));
            double top = Math.Max(work.Top, Math.Min(window.Top, work.Bottom - height));
            return new Rect(left, top, width, height);
        }

        private void ClampWindow()
        {
            Rect work = GetWorkArea();
            Window.MinHeight = Math.Min(650, Math.Max(410, work.Height - 16));
            Rect bounds = FitWindowBounds(work, new Rect(Window.Left, Window.Top, Window.Width, Window.Height));
            Window.Width = bounds.Width; Window.Height = bounds.Height;
            Window.Left = bounds.Left; Window.Top = bounds.Top;
        }

        private void QueueGeometry() { if (loaded) { geometryTimer.Stop(); geometryTimer.Start(); } }
        private void SaveGeometry()
        {
            if (!loaded) return;
            Change(delegate
            {
                store.Data.Settings.Left = Window.Left;
                store.Data.Settings.Top = Window.Top;
                store.Data.Settings.Width = Window.Width;
                store.Data.Settings.Height = Window.Height;
                store.Data.Settings.StackedLayout = true;
            }, false);
        }

        private void SetupTray()
        {
            trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
            tray = new Forms.NotifyIcon { Text = store.Data.Settings.AppTitle + " · 双击打开", Icon = trayIcon, Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开计划", null, delegate { Window.Dispatcher.Invoke(new Action(ShowWindow)); });
            menu.Items.Add("添加今天的计划", null, delegate { Window.Dispatcher.Invoke(new Action(delegate { ShowWindow(); SelectDate(DateTime.Today, "today"); EditTask(null); })); });
            menu.Items.Add("添加本周的计划", null, delegate { Window.Dispatcher.Invoke(new Action(delegate { ShowWindow(); SelectWeek(DateTime.Today, "this-week"); EditTask(null, true); })); });
            menu.Items.Add("回到右上角", null, delegate { Window.Dispatcher.Invoke(new Action(delegate { ShowWindow(); ResetPosition(); })); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { Window.Dispatcher.Invoke(new Action(delegate { Window.Close(); })); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { Window.Dispatcher.Invoke(new Action(ShowWindow)); };
        }
        private void ShowWindow() { Window.Show(); if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal; RefreshDay(); Window.Activate(); }
        private void ResetPosition() { Rect area = GetWorkArea(); Window.Left = area.Right - Window.Width - 14; Window.Top = area.Top + 12; ClampWindow(); }

        private void RefreshDay()
        {
            DateTime today = DateTime.Today;
            if (today == currentDay) return;
            currentDay = today;
            if (selectionMode == "today") selectedDay = today;
            if (selectionMode == "tomorrow") selectedDay = today.AddDays(1);
            if (weekSelectionMode == "this-week") selectedWeek = PlannerDates.StartOfWeek(today);
            if (weekSelectionMode == "next-week") selectedWeek = PlannerDates.StartOfWeek(PlannerDates.ShiftDays(today, 7));
            Render();
        }
        private void SelectDate(DateTime day, string mode)
        {
            selectedDay = day.Date; selectionMode = mode; RenderSection(false);
        }
        private void SelectWeek(DateTime day, string mode)
        {
            selectedWeek = PlannerDates.StartOfWeek(day); weekSelectionMode = mode; RenderSection(true);
        }
        private IEnumerable<TaskItem> PastUnfinished(bool weekly)
        {
            string cutoff = DayKey(weekly ? PlannerDates.StartOfWeek(DateTime.Today) : DateTime.Today);
            return store.Data.Tasks.Where(t => t.IsWeekly == weekly && !t.Completed && String.CompareOrdinal(t.Date, cutoff) < 0);
        }

        private void RenderPin()
        {
            Button pin = Find<Button>("PinButton");
            pin.Content = Window.Topmost ? "◆" : "◇";
            pin.Foreground = Window.Topmost ? ColorBrush("#355B8F") : muted;
            pin.ToolTip = Window.Topmost ? "已置顶 · 点击取消" : "点击置顶到其他窗口上方";
            AutomationProperties.SetName(pin, Window.Topmost ? "取消置顶" : "置顶窗口");
        }

        private void Render()
        {
            RenderAppearance();
            RenderPin();
            RenderSection(true);
            RenderSection(false);
        }

        private void RenderSection(bool weekly)
        {
            string prefix = weekly ? "Week" : "";
            DateTime thisWeek = PlannerDates.StartOfWeek(DateTime.Today), nextWeek = PlannerDates.StartOfWeek(PlannerDates.ShiftDays(thisWeek, 7));
            string weekday = selectedDay.ToString("dddd", CultureInfo.GetCultureInfo("zh-CN"));
            string relative = weekly ? (selectedWeek == thisWeek ? "本周" : selectedWeek == nextWeek ? "下周" : "这一周") : (selectedDay == DateTime.Today ? "今天" : selectedDay == PlannerDates.ShiftDays(DateTime.Today, 1) ? "明天" : "这一天");
            TextBlock dateText = Find<TextBlock>(prefix + "DateText");
            DateTime weekEnd = PlannerDates.EndOfWeek(selectedWeek);
            string weekFormat = selectedWeek.Year == weekEnd.Year ? "M.d" : "yy.M.d";
            dateText.Text = weekly ? selectedWeek.ToString(weekFormat) + " – " + weekEnd.ToString(weekFormat) + " · " + relative : selectedDay.ToString("M 月 d 日", CultureInfo.GetCultureInfo("zh-CN"));
            if (weekly) dateText.ToolTip = selectedWeek.ToString("yyyy/M/d") + " – " + weekEnd.ToString("yyyy/M/d") + " · 周一至周日";
            else Find<TextBlock>("DayText").Text = selectedDay.Year + " 年 · " + weekday + " · " + relative;
            SetTab(prefix + "TodayButton", weekly ? selectedWeek == thisWeek : selectedDay == DateTime.Today);
            SetTab(prefix + "TomorrowButton", weekly ? selectedWeek == nextWeek : selectedDay == PlannerDates.ShiftDays(DateTime.Today, 1));
            SetTab(prefix + "CalendarButton", weekly ? selectedWeek != thisWeek && selectedWeek != nextWeek : selectedDay != DateTime.Today && selectedDay != PlannerDates.ShiftDays(DateTime.Today, 1));
            List<TaskItem> tasks = store.Data.Tasks.Where(t => t.IsWeekly == weekly && t.Date == DayKey(weekly ? selectedWeek : selectedDay)).OrderBy(t => t.Completed).ThenBy(t => t.CreatedAt).ToList();
            int completed = tasks.Count(t => t.Completed);
            Find<TextBlock>(prefix + "SummaryText").Text = tasks.Count == 0 ? "给" + relative + "留一点明确的方向" : "已完成 " + completed + " / " + tasks.Count + " 项";
            double percent = tasks.Count == 0 ? 0 : weekly ? tasks.Average(t => t.ProgressPercent) : completed * 100.0 / tasks.Count;
            Find<TextBlock>(prefix + "PercentText").Text = tasks.Count == 0 ? "" : Math.Round(percent) + "%";
            Find<ProgressBar>(weekly ? "WeekProgress" : "DayProgress").Value = percent;
            StackPanel list = Find<StackPanel>(prefix + "TaskList");
            list.Children.Clear();
            if (tasks.Count == 0 && backgroundBitmap != null)
            {
                list.Children.Add(new TextBlock { Text = weekly ? "这周还没有安排 · 点击下方添加周计划" : "还没有安排 · 点击下方添加日计划", FontSize = 11, Foreground = ink, Margin = new Thickness(0, 4, 0, 0) });
            }
            else if (tasks.Count == 0)
            {
                var empty = new StackPanel { Margin = new Thickness(10, 23, 10, 14), HorizontalAlignment = HorizontalAlignment.Center };
                empty.Children.Add(new TextBlock { Text = "○", FontSize = 34, Foreground = ColorBrush("#AABCD3"), HorizontalAlignment = HorizontalAlignment.Center });
                empty.Children.Add(new TextBlock { Text = "还没有安排", FontSize = 14, Foreground = ink, Margin = new Thickness(0, 8, 0, 6), HorizontalAlignment = HorizontalAlignment.Center });
                empty.Children.Add(new TextBlock { Text = "点击下方，写下第一项计划", FontSize = 11, Foreground = muted, HorizontalAlignment = HorizontalAlignment.Center });
                list.Children.Add(empty);
            }
            foreach (TaskItem task in tasks) list.Children.Add(TaskRow(task));
            int overdue = PastUnfinished(weekly).Count();
            Button overdueButton = Find<Button>(prefix + "OverdueButton");
            overdueButton.Visibility = overdue > 0 ? Visibility.Visible : Visibility.Collapsed;
            overdueButton.Content = "↗  还有 " + overdue + (weekly ? " 项往周计划未完成 · 查看 / 调整" : " 项过去的计划未完成 · 查看 / 调整");
        }

        private void RenderAppearance()
        {
            PlannerSettings settings = store.Data.Settings;
            Find<TextBlock>("BrandTitle").Text = settings.AppTitle;
            Find<TextBlock>("BrandTitle").MaxWidth = Math.Max(75, Window.Width - 247);
            Find<TextBlock>("SubtitleText").Text = settings.Subtitle;
            string backdropText = WatermarkText(settings.Subtitle);
            Find<TextBlock>("WeekWatermarkText").Text = backdropText;
            string compact = new String(backdropText.Where(c => !Char.IsWhiteSpace(c)).ToArray());
            bool useHandwriting = weeklyQuoteBitmap != null && compact == "向内求真向前求变自我批评自我革命";
            Find<StackPanel>("WeekWatermarkArtwork").Visibility = useHandwriting ? Visibility.Visible : Visibility.Collapsed;
            Find<TextBlock>("WeekWatermarkText").Visibility = useHandwriting ? Visibility.Collapsed : Visibility.Visible;
            Find<Viewbox>("WeekWatermark").Visibility = String.IsNullOrWhiteSpace(settings.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
            Find<Viewbox>("WeekWatermark").Opacity = Math.Min(.30, settings.BackgroundOpacity);
            Find<Button>("TodayButton").Content = settings.TodayLabel;
            Find<Button>("TomorrowButton").Content = settings.TomorrowLabel;
            Find<Button>("CalendarButton").Content = settings.CalendarLabel;
            Window.Title = settings.AppTitle;
            if (tray != null) tray.Text = settings.AppTitle + " · 双击打开";
            string path = settings.BackgroundImage ?? "";
            if (path != loadedBackgroundPath)
            {
                loadedBackgroundPath = path;
                backgroundBitmap = null;
                if (!String.IsNullOrEmpty(path) && File.Exists(path))
                {
                    try
                    {
                        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.DecodePixelWidth = 1200; bitmap.UriSource = new Uri(Path.GetFullPath(path)); bitmap.EndInit(); bitmap.Freeze(); backgroundBitmap = bitmap;
                    }
                    catch { SetStatus("背景图片未能读取 · 可在外观设置中重选", true); }
                }
            }
            bool hasImage = backgroundBitmap != null;
            bool separateCalligraphy = hasImage && String.Equals(Path.GetFileName(path), "calligraphy-ivory-v2.png", StringComparison.OrdinalIgnoreCase);
            Find<Canvas>("DailyCalligraphySurface").Visibility = separateCalligraphy ? Visibility.Visible : Visibility.Collapsed;
            Find<Canvas>("DailyCalligraphySurface").Opacity = Math.Min(.30, settings.BackgroundOpacity);
            if (separateCalligraphy)
            {
                foreach (string imageName in new[] { "DailyQuoteTopImage", "DailyQuoteBottomImage", "DailySignatureImage", "DailyStarImage" })
                    Find<Image>(imageName).Source = backgroundBitmap;
                LayoutDailyCalligraphy();
            }
            Find<TextBlock>("SubtitleText").Foreground = hasImage ? ink : muted;
            Find<TextBlock>("DayText").Foreground = hasImage ? ink : muted;
            Find<TextBlock>("SummaryText").Foreground = hasImage ? ink : muted;
            Find<TextBlock>("PercentText").Foreground = hasImage ? ink : green;
            Find<TextBlock>("WeekDateText").Foreground = hasImage ? ink : muted;
            Find<TextBlock>("WeekSummaryText").Foreground = hasImage ? ink : muted;
            Find<TextBlock>("WeekPercentText").Foreground = hasImage ? ink : green;
            if (!hasImage) { Find<Border>("WallpaperSurface").Background = null; Find<Border>("RootSurface").Background = defaultBackground; return; }
            Find<Border>("RootSurface").Background = ColorBrush("#F9FAFC");
            if (separateCalligraphy) { Find<Border>("WallpaperSurface").Background = null; return; }
            var rectangle = new Rect(0, 0, backgroundBitmap.PixelWidth, backgroundBitmap.PixelHeight);
            var drawing = new DrawingGroup();
            drawing.Children.Add(new ImageDrawing(backgroundBitmap, rectangle));
            byte alpha = (byte)Math.Round(255 * (1 - Math.Min(.30, settings.BackgroundOpacity)));
            drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(alpha, 247, 249, 253)), null, new RectangleGeometry(rectangle)));
            drawing.Freeze();
            Find<Border>("WallpaperSurface").Background = new DrawingBrush(drawing) { Stretch = Stretch.Uniform, AlignmentY = AlignmentY.Bottom };
        }

        private void LayoutDailyCalligraphy()
        {
            Canvas surface = Find<Canvas>("DailyCalligraphySurface");
            double width = surface.ActualWidth, height = surface.ActualHeight;
            if (width <= 0 || height <= 0) return;
            double scale = Math.Min(1, Math.Min(width / 335, height / 235));
            PositionDailyCalligraphy("DailyQuoteTop", 305, 70, .18, scale, width, height);
            PositionDailyCalligraphy("DailyQuoteBottom", 305, 70, .515, scale, width, height);
            PositionDailyCalligraphy("DailySignature", 110, 32, .76, scale, width, height);
            PositionDailyCalligraphy("DailyStar", 40, 36, .94, scale, width, height);
        }

        private void PositionDailyCalligraphy(string name, double baseWidth, double baseHeight, double center,
            double scale, double surfaceWidth, double surfaceHeight)
        {
            Viewbox part = Find<Viewbox>(name);
            part.Width = baseWidth * scale;
            part.Height = baseHeight * scale;
            Canvas.SetLeft(part, (surfaceWidth - part.Width) / 2);
            Canvas.SetTop(part, Math.Max(0, Math.Min(surfaceHeight - part.Height, surfaceHeight * center - part.Height / 2)));
        }

        private static string WatermarkText(string text)
        {
            string lines = (text ?? "").Replace("；", "\n").Replace(";", "\n");
            return new String(lines.Where(c => !Char.IsPunctuation(c)).ToArray()).Trim();
        }

        private void SetTab(string name, bool active)
        {
            Button button = Find<Button>(name);
            button.Background = ColorBrush(active ? "#E0E9F6" : "#EEF2F8");
            button.Foreground = ColorBrush(active ? "#2E578B" : "#68768D");
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }

        private Border TaskRow(TaskItem task)
        {
            bool hasImage = backgroundBitmap != null || (task.IsWeekly && !String.IsNullOrWhiteSpace(store.Data.Settings.Subtitle));
            Brush detailInk = hasImage ? ColorBrush("#35445E") : muted;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var check = new CheckBox { IsChecked = task.Completed, Style = (Style)Window.FindResource("CircleCheck"), ToolTip = task.Completed ? "撤销完成" : "标记完成" };
            AutomationProperties.SetName(check, task.Title + (task.Completed ? "，已完成" : "，未完成"));
            check.Click += delegate
            {
                Change(delegate
                {
                    task.Completed = check.IsChecked == true;
                    if (task.IsWeekly) task.Progress = task.Completed ? 100 : 0;
                    task.CompletedAt = task.Completed ? DateTime.UtcNow.ToString("o") : "";
                });
                Render();
            };
            row.Children.Add(check);
            var text = new StackPanel { Margin = new Thickness(8, 0, 6, 0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock { Text = task.Title, Foreground = task.Completed ? ColorBrush("#2E674F") : ink, FontSize = 13, FontWeight = hasImage || task.Completed ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, MaxHeight = 60 };
            title.MouseLeftButtonUp += delegate { EditTask(task); };
            text.Children.Add(title);
            if (!String.IsNullOrWhiteSpace(task.Notes))
            {
                var notes = new TextBlock { Text = task.Notes.Replace('\r', ' ').Replace('\n', ' '), Foreground = task.Completed ? ColorBrush("#355C49") : detailInk, FontSize = hasImage ? 11 : 10, Margin = new Thickness(0, 5, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                notes.MouseLeftButtonUp += delegate { EditTask(task); };
                text.Children.Add(notes);
            }
            if (task.IsWeekly) text.Children.Add(TaskProgressControl(task));
            Grid.SetColumn(text, 1); row.Children.Add(text);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Button edit = Button("编辑", delegate { EditTask(task); }); edit.Padding = new Thickness(5, 6, 5, 6); edit.FontSize = 10; edit.Background = Brushes.Transparent; edit.Foreground = detailInk;
            AutomationProperties.SetName(edit, "编辑 " + task.Title);
            actions.Children.Add(edit);
            Button more = Button("⋯", null); more.Padding = new Thickness(4, 3, 4, 3); more.FontSize = 17; more.Background = Brushes.Transparent; more.Foreground = detailInk;
            AutomationProperties.SetName(more, task.Title + "的更多操作");
            var context = new ContextMenu();
            DateTime firstTarget = task.IsWeekly ? PlannerDates.StartOfWeek(DateTime.Today) : DateTime.Today;
            DateTime secondTarget = task.IsWeekly ? PlannerDates.StartOfWeek(PlannerDates.ShiftDays(firstTarget, 7)) : PlannerDates.ShiftDays(firstTarget, 1);
            MenuItem moveToday = new MenuItem { Header = task.IsWeekly ? "移到本周" : "移到今天", IsEnabled = task.Date != DayKey(firstTarget) };
            moveToday.Click += delegate { Change(delegate { task.Date = DayKey(firstTarget); }); Render(); };
            MenuItem moveTomorrow = new MenuItem { Header = task.IsWeekly ? "移到下周" : "移到明天", IsEnabled = task.Date != DayKey(secondTarget) };
            moveTomorrow.Click += delegate { Change(delegate { task.Date = DayKey(secondTarget); }); Render(); };
            MenuItem changeDate = new MenuItem { Header = task.IsWeekly ? "编辑 / 修改周" : "编辑 / 修改日期" }; changeDate.Click += delegate { EditTask(task); };
            MenuItem remove = new MenuItem { Header = "删除计划" };
            remove.Click += delegate
            {
                if (MessageBox.Show(Window, "删除这项计划？\n\n" + task.Title, "删除计划", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) { Change(delegate { store.Data.Tasks.Remove(task); }); Render(); }
            };
            context.Items.Add(moveToday); context.Items.Add(moveTomorrow); context.Items.Add(changeDate); context.Items.Add(new Separator()); context.Items.Add(remove);
            more.ContextMenu = context;
            more.Click += delegate { context.PlacementTarget = more; context.IsOpen = true; };
            actions.Children.Add(more); Grid.SetColumn(actions, 2); row.Children.Add(actions);
            Brush rowBackground = hasImage ? (task.Completed ? ColorBrush("#183F8F68") : Brushes.Transparent) : (task.Completed ? ColorBrush("#EDF3F1") : Brushes.White);
            var card = new Border { Child = row, Background = rowBackground, CornerRadius = new CornerRadius(hasImage ? (task.Completed ? 7 : 0) : 12), Padding = new Thickness(11, hasImage ? 10 : 12, 8, hasImage ? 10 : 12), Margin = new Thickness(0, 0, 0, 8), BorderBrush = ColorBrush(task.Completed ? "#458F6F" : (hasImage ? "#4093A1B5" : "#E4EAF2")), BorderThickness = hasImage ? (task.Completed ? new Thickness(3, 0, 0, 0) : new Thickness(0, 0, 0, 1)) : new Thickness(1) };
            if (hasImage)
            {
                card.MouseEnter += delegate { card.Background = task.Completed ? ColorBrush("#243F8F68") : ColorBrush("#14FFFFFF"); };
                card.MouseLeave += delegate { card.Background = rowBackground; };
            }
            return card;
        }

        private Grid TaskProgressControl(TaskItem task)
        {
            var panel = new Grid { Margin = new Thickness(0, 5, 0, 0) };
            panel.ColumnDefinitions.Add(new ColumnDefinition());
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var slider = new Slider
            {
                Minimum = 0, Maximum = 100, Value = task.ProgressPercent, TickFrequency = 1,
                IsSnapToTickEnabled = true, IsMoveToPointEnabled = true, SmallChange = 1, LargeChange = 10,
                Style = (Style)Window.FindResource("WeeklyProgressSlider"),
                ToolTip = "点击或拖动调整进度，松开自动保存；方向键微调，Enter 保存"
            };
            AutomationProperties.SetName(slider, task.Title + "的进度");
            var percent = new TextBlock { Text = task.ProgressPercent + "%", FontSize = 10, Foreground = green, MinWidth = 32, TextAlignment = TextAlignment.Right, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            slider.ValueChanged += delegate { percent.Text = Math.Round(slider.Value) + "%"; };
            bool committing = false;
            Action commit = delegate
            {
                if (committing) return;
                int value = (int)Math.Round(slider.Value);
                if (store.Data.Tasks.Contains(task) && task.IsWeekly && value != task.ProgressPercent)
                {
                    committing = true;
                    try { if (!SetWeeklyProgress(task, value)) slider.Value = task.ProgressPercent; }
                    finally { committing = false; }
                }
            };
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(delegate { commit(); }));
            slider.PreviewMouseLeftButtonUp += delegate { Window.Dispatcher.BeginInvoke(commit, DispatcherPriority.Background); };
            slider.LostKeyboardFocus += delegate { commit(); };
            slider.KeyUp += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { commit(); e.Handled = true; } };
            panel.Children.Add(slider); Grid.SetColumn(percent, 1); panel.Children.Add(percent);
            return panel;
        }

        private bool SetWeeklyProgress(TaskItem task, int value)
        {
            if (task == null || !task.IsWeekly || !store.Data.Tasks.Contains(task) || value < 0 || value > 100) return false;
            bool saved = Change(delegate
            {
                bool wasCompleted = task.Completed;
                task.Progress = value; task.Completed = value == 100;
                task.CompletedAt = task.Completed ? (wasCompleted ? task.CompletedAt : DateTime.UtcNow.ToString("o")) : "";
            }, !testMode);
            RenderSection(true);
            return saved;
        }

        private Button Button(string content, Action click)
        {
            var button = new Button { Content = content, Style = (Style)Window.FindResource(typeof(Button)) };
            if (click != null) button.Click += delegate { click(); };
            return button;
        }
        private TextBlock Label(string text) { return new TextBlock { Text = text, Foreground = muted, FontSize = 12, Margin = new Thickness(0, 12, 0, 6) }; }
        private Window Dialog(string title, double width, double height)
        {
            var dialog = new Window { Title = title, Width = width, Height = height, MaxHeight = Math.Max(350, GetWorkArea().Height - 30), Owner = testMode ? null : Window, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Background = ColorBrush("#F5F7FB"), Foreground = ink, FontFamily = Window.FontFamily, FontSize = 13, ShowInTaskbar = false, Topmost = Window.Topmost };
            dialog.Resources.MergedDictionaries.Add(Window.Resources);
            return dialog;
        }

        private bool CommitJournal(DateTime date, string content)
        {
            string key = DayKey(date), text = (content ?? "").Trim();
            JournalEntry entry = store.Data.Journal.FirstOrDefault(j => j.Date == key);
            if (entry == null && text.Length == 0) return true;
            if (entry != null && entry.Content == text) return true;
            return Change(delegate
            {
                if (text.Length == 0) { store.Data.Journal.Remove(entry); return; }
                if (entry == null) { entry = new JournalEntry { Date = key }; store.Data.Journal.Add(entry); }
                entry.Content = text; entry.UpdatedAt = DateTime.UtcNow.ToString("o");
            }, !testMode);
        }

        private Window CreateJournalDialog(DateTime initialDate)
        {
            var dialog = Dialog("每日小记", 780, 660);
            dialog.ResizeMode = ResizeMode.CanResizeWithGrip;
            dialog.MinWidth = 700; dialog.MinHeight = Math.Min(520, dialog.MaxHeight);
            var layout = new Grid { Margin = new Thickness(20, 16, 20, 20) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition());
            var heading = new StackPanel { Margin = new Thickness(2, 0, 0, 16) };
            heading.Children.Add(new TextBlock { Text = "每日小记", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = ink });
            heading.Children.Add(new TextBlock { Text = "写下学习收获，方便以后回看", FontSize = 11, Foreground = muted, Margin = new Thickness(0, 4, 0, 0) }); layout.Children.Add(heading);
            var workspace = new Grid(); workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) }); workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); workspace.ColumnDefinitions.Add(new ColumnDefinition()); Grid.SetRow(workspace, 1); layout.Children.Add(workspace);
            var browser = new Grid();
            browser.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); browser.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); browser.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); browser.RowDefinitions.Add(new RowDefinition()); browser.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); browser.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var historyHeading = new TextBlock { Text = "过往记录", Foreground = ink, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) }; browser.Children.Add(historyHeading);
            var search = new TextBox { Name = "JournalSearch", Padding = new Thickness(9, 7, 9, 7), FontSize = 11, MaxLength = 120, ToolTip = "搜索正文或日期" }; AutomationProperties.SetName(search, "搜索日记正文或日期");
            var searchArea = new Grid { Margin = new Thickness(0, 0, 0, 8) }; searchArea.Children.Add(search);
            var searchPrompt = new TextBlock { Text = "搜索正文或日期", FontSize = 11, Foreground = muted, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false }; searchArea.Children.Add(searchPrompt); Grid.SetRow(searchArea, 1); browser.Children.Add(searchArea);
            var month = new ComboBox { Name = "JournalMonth", Padding = new Thickness(8, 6, 8, 6), FontSize = 11, Margin = new Thickness(0, 0, 0, 12) }; AutomationProperties.SetName(month, "按月份筛选日记"); Grid.SetRow(month, 2); browser.Children.Add(month);
            var history = new StackPanel { Name = "JournalHistory" };
            var historyScroll = new ScrollViewer { Name = "JournalHistoryScroll", Content = history, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 4, 0), Focusable = false, FocusVisualStyle = null };
            Grid.SetRow(historyScroll, 3); browser.Children.Add(historyScroll);
            var pager = new Grid { Margin = new Thickness(0, 10, 0, 0) }; pager.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); pager.ColumnDefinitions.Add(new ColumnDefinition()); pager.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Button previous = Button("‹", null); previous.Name = "JournalPreviousPage"; previous.Padding = new Thickness(9, 4, 9, 4); previous.ToolTip = "上一页"; pager.Children.Add(previous);
            var pageText = new TextBlock { Name = "JournalPageText", FontSize = 10, Foreground = muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(pageText, 1); pager.Children.Add(pageText);
            Button nextPage = Button("›", null); nextPage.Name = "JournalNextPage"; nextPage.Padding = new Thickness(9, 4, 9, 4); nextPage.ToolTip = "下一页"; Grid.SetColumn(nextPage, 2); pager.Children.Add(nextPage); Grid.SetRow(pager, 4); browser.Children.Add(pager);
            Button export = Button("导出全部", null); export.Name = "JournalExport"; export.FontSize = 11; export.Padding = new Thickness(9, 6, 9, 6); export.Margin = new Thickness(0, 10, 0, 0); export.Background = ColorBrush("#E3EAF3"); Grid.SetRow(export, 5); browser.Children.Add(export);
            workspace.Children.Add(new Border { Child = browser, Background = ColorBrush("#EEF2F7"), CornerRadius = new CornerRadius(14), Padding = new Thickness(13) });
            var writing = new Grid(); writing.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); writing.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); writing.RowDefinitions.Add(new RowDefinition()); writing.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var dayHeading = new TextBlock { Name = "JournalDayHeading", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = ink, VerticalAlignment = VerticalAlignment.Center }; header.Children.Add(dayHeading);
            var date = new DatePicker { Name = "JournalDate", SelectedDate = initialDate.Date, SelectedDateFormat = DatePickerFormat.Short, Width = 126, Language = XmlLanguage.GetLanguage("zh-CN"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(date, 1); header.Children.Add(date); AutomationProperties.SetName(date, "日记日期");
            Button today = Button("今天", delegate { date.SelectedDate = DateTime.Today; }); today.Padding = new Thickness(9, 6, 9, 6); today.Margin = new Thickness(7, 0, 0, 0); Grid.SetColumn(today, 2); header.Children.Add(today);
            writing.Children.Add(header);
            var line = new Border { Height = 1, Background = ColorBrush("#E3EAF2"), Margin = new Thickness(0, 16, 0, 14) }; Grid.SetRow(line, 1); writing.Children.Add(line);
            var editor = new TextBox { Name = "JournalEditor", MaxLength = PlannerStore.MaxJournalLength, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 14, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
            AutomationProperties.SetName(editor, "日记内容");
            var editorArea = new Grid { Margin = new Thickness(0, 0, 0, 14) }; editorArea.Children.Add(editor);
            var prompt = new TextBlock { Text = "今天学到了什么？\n有什么新的理解，或值得记住的事？", FontSize = 13, Foreground = ColorBrush("#93A0B1"), IsHitTestVisible = false, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 16, 0) }; editorArea.Children.Add(prompt); Grid.SetRow(editorArea, 2); writing.Children.Add(editorArea);
            var toolbar = new Grid(); toolbar.ColumnDefinitions.Add(new ColumnDefinition()); toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var status = new TextBlock { Name = "JournalStatus", Text = "", FontSize = 10, Foreground = muted, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }; toolbar.Children.Add(status);
            var wordCount = new TextBlock { FontSize = 10, Foreground = muted, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(wordCount, 1); toolbar.Children.Add(wordCount); Grid.SetRow(toolbar, 3); writing.Children.Add(toolbar);
            var writingSurface = new Border { Child = writing, Background = Brushes.White, CornerRadius = new CornerRadius(14), Padding = new Thickness(18), BorderThickness = new Thickness(1), BorderBrush = ColorBrush("#DFE6EF") }; Grid.SetColumn(writingSurface, 2); workspace.Children.Add(writingSurface);
            DateTime editingDay = initialDate.Date;
            string loadedContent = "";
            bool loading = false, saving = false, validDate = true, updatingFilters = false;
            int pageIndex = 0;
            Button remove = null;
            Action renderHistory = null;
            Action load = delegate
            {
                JournalEntry entry = store.Data.Journal.FirstOrDefault(j => j.Date == DayKey(editingDay));
                loading = true; loadedContent = entry == null ? "" : entry.Content; editor.Text = loadedContent; loading = false;
                prompt.Visibility = editor.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                wordCount.Text = editor.Text.Length + " / 8000";
                dayHeading.Text = editingDay.ToString("M 月 d 日");
                status.Text = entry == null ? "写几句就好" : "已保存 · 可继续修改"; status.Foreground = muted;
                if (remove != null) remove.Visibility = entry == null ? Visibility.Collapsed : Visibility.Visible;
            };
            Func<bool> save = delegate
            {
                if (saving) return true;
                if (!validDate || !date.SelectedDate.HasValue) { status.Text = "请选择有效的日期"; status.Foreground = ColorBrush("#B6564D"); return false; }
                string content = editor.Text.Trim();
                if (content == loadedContent) return true;
                saving = true;
                try
                {
                    if (!CommitJournal(editingDay, content)) { status.Text = "保存失败，文字仍保留在这里"; status.Foreground = ColorBrush("#B6564D"); return false; }
                    loadedContent = content; status.Text = content.Length == 0 ? "空白内容不留记录" : "已保存"; status.Foreground = green;
                    if (remove != null) remove.Visibility = content.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                    renderHistory(); return true;
                }
                finally { saving = false; }
            };
            renderHistory = delegate
            {
                history.Children.Clear();
                string selectedMonth = month.SelectedItem is ComboBoxItem ? ((ComboBoxItem)month.SelectedItem).Tag as string : "";
                updatingFilters = true;
                month.Items.Clear(); month.Items.Add(new ComboBoxItem { Content = "全部月份", Tag = "" });
                foreach (string period in store.Data.Journal.Select(j => j.Date.Substring(0, 7)).Distinct().OrderByDescending(p => p, StringComparer.Ordinal))
                    month.Items.Add(new ComboBoxItem { Content = period.Substring(0, 4) + " 年 " + Int32.Parse(period.Substring(5, 2)) + " 月", Tag = period });
                month.SelectedItem = month.Items.OfType<ComboBoxItem>().FirstOrDefault(c => (string)c.Tag == selectedMonth) ?? month.Items[0];
                updatingFilters = false;
                JournalPage page = JournalBrowse.FindPage(store.Data.Journal, search.Text, ((ComboBoxItem)month.SelectedItem).Tag as string, pageIndex);
                pageIndex = page.PageIndex;
                historyHeading.Text = "过往记录 · " + store.Data.Journal.Count + " 篇";
                pageText.Text = page.TotalCount == 0 ? "0 篇" : (pageIndex + 1) + " / " + page.PageCount + " 页 · " + page.TotalCount + " 篇";
                previous.IsEnabled = pageIndex > 0; nextPage.IsEnabled = pageIndex + 1 < page.PageCount; export.IsEnabled = store.Data.Journal.Count > 0;
                if (page.TotalCount == 0)
                {
                    var empty = new StackPanel { Margin = new Thickness(8, 32, 8, 0) };
                    empty.Children.Add(new Border { Width = 38, Height = 38, Background = ColorBrush("#E0E9F6"), CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Center, Child = new TextBlock { Text = "记", FontSize = 17, Foreground = ColorBrush("#52749E"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
                    empty.Children.Add(new TextBlock { Text = store.Data.Journal.Count == 0 ? "还没有小记" : "没有找到记录", Foreground = ink, FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 8) });
                    empty.Children.Add(new TextBlock { Text = store.Data.Journal.Count == 0 ? "在右侧写下今天的收获\n有记录的日期会出现在这里" : "换个关键词，或选择全部月份", Foreground = muted, FontSize = 11, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap }); history.Children.Add(empty);
                }
                foreach (JournalEntry item in page.Entries)
                {
                    JournalEntry entry = item;
                    var cardContent = new StackPanel();
                    var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    DateTime day = DateTime.ParseExact(entry.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    row.Children.Add(new TextBlock { Text = day.ToString("yyyy/M/d", CultureInfo.InvariantCulture), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ink, VerticalAlignment = VerticalAlignment.Center });
                    Action open = delegate { date.SelectedDate = day; if (editingDay == day) editor.Focus(); };
                    Button edit = Button("打开", open); edit.Padding = new Thickness(4, 3, 4, 3); edit.FontSize = 10; edit.Background = Brushes.Transparent; AutomationProperties.SetName(edit, "打开 " + entry.Date + " 的日记"); Grid.SetColumn(edit, 1); row.Children.Add(edit);
                    cardContent.Children.Add(row);
                    cardContent.Children.Add(new TextBlock { Text = JournalBrowse.Excerpt(entry.Content), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 36, FontSize = 11, Foreground = muted, Margin = new Thickness(0, 5, 0, 0), LineHeight = 17 });
                    var card = new Border { Child = cardContent, Padding = new Thickness(10, 8, 10, 10), Margin = new Thickness(0, 0, 0, 8), Background = ColorBrush(editingDay == day ? "#E4EDF8" : "#FAFCFE"), BorderBrush = ColorBrush(editingDay == day ? "#AFC5DF" : "#DFE6EF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand };
                    card.MouseLeftButtonUp += delegate { open(); }; history.Children.Add(card);
                }
                historyScroll.ScrollToTop();
            };
            Func<bool> saveCurrent = delegate
            {
                DateTime parsed;
                validDate = DateTime.TryParse(date.Text, CultureInfo.GetCultureInfo("zh-CN"), DateTimeStyles.None, out parsed) && date.SelectedDate.HasValue;
                return save();
            };
            Button saveButton = Button("保存小记", delegate { saveCurrent(); }); saveButton.Background = ColorBrush("#355B8F"); saveButton.Foreground = Brushes.White; saveButton.Padding = new Thickness(14, 8, 14, 8); saveButton.Margin = new Thickness(0, 11, 0, 0); saveButton.HorizontalAlignment = HorizontalAlignment.Left; Grid.SetRow(saveButton, 1); toolbar.Children.Add(saveButton);
            remove = Button("删除这篇", delegate
            {
                if (MessageBox.Show(dialog, "删除 " + DayKey(editingDay) + " 的日记？", "删除日记", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                if (CommitJournal(editingDay, "")) { load(); renderHistory(); }
            }); remove.FontSize = 11; remove.Padding = new Thickness(4, 8, 4, 8); remove.Background = Brushes.Transparent; remove.Foreground = muted; remove.Margin = new Thickness(0, 11, 0, 0); remove.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(remove, 1); Grid.SetRow(remove, 1); toolbar.Children.Add(remove);
            editor.TextChanged += delegate
            {
                prompt.Visibility = editor.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; wordCount.Text = editor.Text.Length + " / 8000";
                if (!loading) { status.Text = editor.Text.Trim() == loadedContent ? "已保存" : "未保存 · 切换日期或关闭时也会保存"; status.Foreground = muted; }
            };
            search.TextChanged += delegate { searchPrompt.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; pageIndex = 0; renderHistory(); };
            month.SelectionChanged += delegate { if (!updatingFilters) { pageIndex = 0; renderHistory(); } };
            previous.Click += delegate { pageIndex--; renderHistory(); }; nextPage.Click += delegate { pageIndex++; renderHistory(); };
            export.Click += delegate
            {
                if (!saveCurrent()) return;
                var picker = new Microsoft.Win32.SaveFileDialog { Title = "导出全部日记", Filter = "Markdown 记录 (*.md)|*.md|文本记录 (*.txt)|*.txt", FileName = "每日小记-" + DayKey(DateTime.Today) + ".md", AddExtension = true };
                if (picker.ShowDialog(dialog) != true) return;
                try { File.WriteAllText(picker.FileName, JournalBrowse.Export(store.Data.Journal), new System.Text.UTF8Encoding(false)); status.Text = "全部记录已导出"; status.Foreground = green; }
                catch (Exception error) { MessageBox.Show(dialog, "导出未完成，日记仍保留在本机。\n\n" + error.Message, "导出日记", MessageBoxButton.OK, MessageBoxImage.Warning); }
            };
            date.DateValidationError += delegate { validDate = false; status.Text = "请选择有效的日期"; status.Foreground = ColorBrush("#B6564D"); };
            date.SelectedDateChanged += delegate
            {
                if (loading) return;
                validDate = date.SelectedDate.HasValue;
                DateTime next = date.SelectedDate.HasValue ? date.SelectedDate.Value.Date : editingDay;
                if (!validDate || next == editingDay) return;
                if (!save()) { loading = true; date.SelectedDate = editingDay; loading = false; return; }
                editingDay = next; load(); renderHistory();
            };
            dialog.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!saveCurrent()) e.Cancel = true; };
            dialog.PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { saveCurrent(); e.Handled = true; } };
            dialog.Content = layout; load(); renderHistory();
            dialog.Loaded += delegate
            {
                Rect area = GetWorkArea();
                dialog.Left = Math.Max(area.Left + 12, Math.Min(dialog.Left, area.Right - dialog.ActualWidth - 12));
                dialog.Top = Math.Max(area.Top + 12, Math.Min(dialog.Top, area.Bottom - dialog.ActualHeight - 12));
                editor.Focus(); editor.CaretIndex = editor.Text.Length;
            };
            return dialog;
        }

        private void EditTask(TaskItem task, bool weekly = false)
        {
            CreateTaskDialog(task, weekly).ShowDialog();
        }

        private Window CreateTaskDialog(TaskItem task, bool weekly)
        {
            if (task != null) weekly = task.IsWeekly;
            var dialog = Dialog(task == null ? (weekly ? "添加周计划" : "添加日计划") : "编辑计划", 420, 560);
            var panel = new StackPanel { Margin = new Thickness(24, 12, 24, 20) };
            panel.Children.Add(Label("计划类型"));
            var scope = new ComboBox { ItemsSource = new[] { "每日计划", "每周计划" }, SelectedIndex = weekly ? 1 : 0, Padding = new Thickness(8, 6, 8, 6) };
            AutomationProperties.SetName(scope, "计划类型"); panel.Children.Add(scope);
            panel.Children.Add(Label("计划内容"));
            var title = new TextBox { Text = task == null ? "" : task.Title, MaxLength = 240, FontSize = 14 };
            AutomationProperties.SetName(title, "计划内容"); panel.Children.Add(title);
            TextBlock dateLabel = Label("安排在哪一天"); panel.Children.Add(dateLabel);
            var dateRow = new Grid(); dateRow.ColumnDefinitions.Add(new ColumnDefinition()); dateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); dateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var date = new DatePicker { SelectedDate = task == null ? (weekly ? selectedWeek : selectedDay) : DateTime.ParseExact(task.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture), SelectedDateFormat = DatePickerFormat.Short, VerticalAlignment = VerticalAlignment.Center, Language = System.Windows.Markup.XmlLanguage.GetLanguage("zh-CN") };
            AutomationProperties.SetName(date, "计划日期"); dateRow.Children.Add(date);
            Button today = Button("今天", delegate { date.SelectedDate = scope.SelectedIndex == 1 ? PlannerDates.StartOfWeek(DateTime.Today) : DateTime.Today; }); today.Margin = new Thickness(8, 0, 0, 0); today.Padding = new Thickness(8, 6, 8, 6); Grid.SetColumn(today, 1); dateRow.Children.Add(today);
            Button tomorrow = Button("明天", delegate { date.SelectedDate = scope.SelectedIndex == 1 ? PlannerDates.StartOfWeek(PlannerDates.ShiftDays(DateTime.Today, 7)) : PlannerDates.ShiftDays(DateTime.Today, 1); }); tomorrow.Margin = new Thickness(5, 0, 0, 0); tomorrow.Padding = new Thickness(8, 6, 8, 6); Grid.SetColumn(tomorrow, 2); dateRow.Children.Add(tomorrow); panel.Children.Add(dateRow);
            var periodHint = new TextBlock { FontSize = 11, Foreground = muted, Margin = new Thickness(0, 6, 0, 0) }; panel.Children.Add(periodHint);
            Action updatePeriod = delegate
            {
                bool isWeek = scope.SelectedIndex == 1;
                dateLabel.Text = isWeek ? "安排在哪一周（任选该周一天）" : "安排在哪一天";
                today.Content = isWeek ? "本周" : "今天"; tomorrow.Content = isWeek ? "下周" : "明天";
                periodHint.Visibility = isWeek ? Visibility.Visible : Visibility.Collapsed;
                if (isWeek && date.SelectedDate.HasValue)
                {
                    DateTime start = PlannerDates.StartOfWeek(date.SelectedDate.Value);
                    periodHint.Text = start.ToString("yyyy/M/d") + " – " + PlannerDates.EndOfWeek(start).ToString("yyyy/M/d") + " · 周一至周日";
                }
            };
            scope.SelectionChanged += delegate { updatePeriod(); }; date.SelectedDateChanged += delegate { updatePeriod(); }; updatePeriod();
            panel.Children.Add(Label("备注（可选）"));
            var notes = new TextBox { Text = task == null ? "" : task.Notes, MaxLength = 8000, Height = 94, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            AutomationProperties.SetName(notes, "计划备注"); panel.Children.Add(notes);
            var done = new CheckBox { Content = "已完成", IsChecked = task != null && task.Completed, Margin = new Thickness(0, 15, 0, 0), Foreground = muted }; panel.Children.Add(done);
            var validation = new TextBlock { Foreground = ColorBrush("#B6564D"), FontSize = 11, Margin = new Thickness(0, 8, 0, 0), Height = 18 }; panel.Children.Add(validation);
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Button cancel = Button("取消", delegate { dialog.Close(); }); cancel.Margin = new Thickness(0, 0, 8, 0); cancel.IsCancel = true; footer.Children.Add(cancel);
            Button save = Button("保存计划", delegate
            {
                if (String.IsNullOrWhiteSpace(title.Text)) { validation.Text = "请写下计划内容。"; title.Focus(); return; }
                DateTime parsedDate;
                if (!DateTime.TryParse(date.Text, CultureInfo.GetCultureInfo("zh-CN"), DateTimeStyles.None, out parsedDate)) { validation.Text = "请选择有效的日期。"; date.Focus(); return; }
                bool saved = CommitTask(task, title.Text, notes.Text, parsedDate, scope.SelectedIndex == 1, done.IsChecked == true);
                if (saved) { dialog.Close(); Render(); }
            });
            save.Background = ColorBrush("#355B8F"); save.Foreground = Brushes.White; footer.Children.Add(save); panel.Children.Add(footer);
            dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            dialog.Loaded += delegate { title.Focus(); title.SelectAll(); };
            dialog.PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); e.Handled = true; } };
            return dialog;
        }

        private bool CommitTask(TaskItem task, string title, string notes, DateTime date, bool weekly, bool completed)
        {
            DateTime period = weekly ? PlannerDates.StartOfWeek(date) : date.Date;
            bool saved = Change(delegate
            {
                TaskItem item = task ?? new TaskItem { Id = Guid.NewGuid().ToString("N"), CreatedAt = DateTime.UtcNow.ToString("o") };
                bool wasDone = item.Completed;
                item.Title = title.Trim(); item.Notes = notes.Trim(); item.Date = DayKey(period);
                item.Scope = weekly ? TaskItem.WeeklyScope : TaskItem.DailyScope;
                item.Progress = !weekly ? 0 : completed ? 100 : item.Progress == 100 ? 0 : item.Progress;
                item.Completed = completed;
                item.CompletedAt = completed ? (wasDone ? item.CompletedAt : DateTime.UtcNow.ToString("o")) : "";
                if (task == null) store.Data.Tasks.Add(item);
            });
            if (saved)
            {
                if (weekly) { selectedWeek = period; weekSelectionMode = period == PlannerDates.StartOfWeek(DateTime.Today) ? "this-week" : "week"; }
                else { selectedDay = period; selectionMode = period == DateTime.Today ? "today" : period == PlannerDates.ShiftDays(DateTime.Today, 1) ? "tomorrow" : "date"; }
                Render();
            }
            return saved;
        }

        private void PickDate(bool weekly)
        {
            var dialog = Dialog(weekly ? "查看某一周的计划" : "查看某一天的计划", 330, 370);
            var panel = new StackPanel { Margin = new Thickness(20) };
            var calendar = new System.Windows.Controls.Calendar { SelectedDate = weekly ? selectedWeek : selectedDay, DisplayDate = weekly ? selectedWeek : selectedDay, FirstDayOfWeek = DayOfWeek.Monday, Language = XmlLanguage.GetLanguage("zh-CN"), HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(calendar);
            if (weekly) panel.Children.Add(new TextBlock { Text = "任选一天，查看所在周的计划（周一至周日）", FontSize = 10, Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
            Button view = Button(weekly ? "查看这一周" : "查看这一天", delegate { if (calendar.SelectedDate.HasValue) { if (weekly) SelectWeek(calendar.SelectedDate.Value, "week"); else SelectDate(calendar.SelectedDate.Value, "date"); dialog.Close(); } });
            view.Margin = new Thickness(0, 18, 0, 0); view.Background = ColorBrush("#355B8F"); view.Foreground = Brushes.White; panel.Children.Add(view);
            dialog.Content = panel; dialog.ShowDialog();
        }

        private void ShowOverdue(bool weekly)
        {
            var dialog = Dialog(weekly ? "过去未完成的周计划" : "过去未完成的计划", 460, 460);
            var layout = new DockPanel { Margin = new Thickness(20) };
            var description = new TextBlock { Text = "记录会一直保留。你可以补做，或重新安排日期。", TextWrapping = TextWrapping.Wrap, Foreground = muted, Margin = new Thickness(0, 0, 0, 14) };
            DockPanel.SetDock(description, Dock.Top); layout.Children.Add(description);
            var list = new StackPanel();
            Action refresh = null;
            refresh = delegate
            {
                list.Children.Clear();
                foreach (TaskItem item in PastUnfinished(weekly).OrderBy(t => t.Date).ToList())
                {
                    var row = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
                    row.Children.Add(new TextBlock { Text = item.Date + " · " + item.Title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
                    var actions = new StackPanel { Orientation = Orientation.Horizontal };
                    Button edit = Button("编辑 / 改日期", delegate { EditTask(item); refresh(); }); edit.Margin = new Thickness(0, 0, 6, 0); actions.Children.Add(edit);
                    Button move = Button(item.IsWeekly ? "移到本周" : "移到今天", delegate { if (Change(delegate { item.Date = DayKey(item.IsWeekly ? PlannerDates.StartOfWeek(DateTime.Today) : DateTime.Today); })) { Render(); dialog.Close(); } }); actions.Children.Add(move);
                    row.Children.Add(actions); list.Children.Add(row);
                }
                if (list.Children.Count == 0) list.Children.Add(new TextBlock { Text = "没有待补做的计划。", Foreground = muted });
            };
            refresh(); layout.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); dialog.Content = layout; dialog.ShowDialog();
        }

        private void ShowSettings()
        {
            var dialog = Dialog("每日计划 · 设置", 430, 540);
            var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            var startup = new CheckBox { Content = "登录 Windows 后自动显示", IsChecked = StartupManager.IsEnabled, Margin = new Thickness(0, 0, 0, 16), IsEnabled = !testMode }; panel.Children.Add(startup);
            var pin = new CheckBox { Content = "始终显示在其他窗口上方", IsChecked = Window.Topmost, Margin = new Thickness(0, 0, 0, 12) }; panel.Children.Add(pin);
            panel.Children.Add(Label("窗口不透明度（越高越清晰）"));
            var opacity = new Slider { Minimum = .80, Maximum = 1, Value = Window.Opacity, TickFrequency = .05, IsSnapToTickEnabled = true, Margin = new Thickness(0, 3, 0, 12) }; panel.Children.Add(opacity);
            panel.Children.Add(new TextBlock { Text = "勾选可完成 / 撤销完成；点击标题或“编辑”修改。\n右下角可调整大小。收起后可双击托盘图标打开。\nCtrl + N 添加计划；Ctrl + Enter 保存编辑。", FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 0, 0, 12) });
            var utilities = new StackPanel { Orientation = Orientation.Horizontal };
            Button appearance = Button("外观设置", delegate { ShowAppearance(); }); appearance.Margin = new Thickness(0, 0, 8, 0); utilities.Children.Add(appearance);
            Button reset = Button("回到右上角", delegate { ResetPosition(); }); reset.Margin = new Thickness(0, 0, 8, 0); utilities.Children.Add(reset);
            Button folder = Button("打开数据文件夹", delegate { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = Path.GetDirectoryName(DataPath), UseShellExecute = true }); }); utilities.Children.Add(folder); panel.Children.Add(utilities);
            panel.Children.Add(new TextBlock { Text = String.IsNullOrEmpty(store.LoadWarning) ? "计划保存在本机 data\\plans.json，保存时自动保留备份。" : store.LoadWarning, FontSize = 10, Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12), MaxHeight = 40 });
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Button cancel = Button("取消", delegate { dialog.Close(); }); cancel.IsCancel = true; cancel.Margin = new Thickness(0, 0, 8, 0); footer.Children.Add(cancel);
            Button save = Button("保存设置", delegate
            {
                bool priorStartup = StartupManager.IsEnabled;
                try
                {
                    if (!testMode && priorStartup != (startup.IsChecked == true)) StartupManager.SetEnabled(startup.IsChecked == true);
                    if (Change(delegate { store.Data.Settings.AutoStart = StartupManager.IsEnabled; store.Data.Settings.AlwaysOnTop = pin.IsChecked == true; store.Data.Settings.Opacity = opacity.Value; }))
                    {
                        Window.Topmost = store.Data.Settings.AlwaysOnTop; Window.Opacity = store.Data.Settings.Opacity; RenderPin(); dialog.Close();
                    }
                    else if (!testMode && StartupManager.IsEnabled != priorStartup) StartupManager.SetEnabled(priorStartup);
                }
                catch (Exception e) { MessageBox.Show(dialog, "设置未保存：" + e.Message, "每日计划", MessageBoxButton.OK, MessageBoxImage.Warning); }
            }); save.Background = ColorBrush("#355B8F"); save.Foreground = Brushes.White; footer.Children.Add(save); panel.Children.Add(footer);
            dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; dialog.ShowDialog();
        }

        private void ShowAppearance()
        {
            PlannerSettings settings = store.Data.Settings;
            var dialog = Dialog("外观设置", 450, 680);
            var panel = new StackPanel { Margin = new Thickness(24, 10, 24, 22) };
            panel.Children.Add(Label("主标题"));
            var title = new TextBox { Text = settings.AppTitle, MaxLength = 12 };
            AutomationProperties.SetName(title, "主标题"); panel.Children.Add(title);
            panel.Children.Add(Label("周计划背景文字"));
            var subtitle = new TextBox { Text = settings.Subtitle, MaxLength = 30 };
            AutomationProperties.SetName(subtitle, "周计划背景文字"); panel.Children.Add(subtitle);
            panel.Children.Add(Label("日期标签（标签文字可改，仍按对应日期显示）"));
            var labels = new Grid();
            labels.ColumnDefinitions.Add(new ColumnDefinition()); labels.ColumnDefinitions.Add(new ColumnDefinition()); labels.ColumnDefinitions.Add(new ColumnDefinition());
            var today = new TextBox { Text = settings.TodayLabel, MaxLength = 6, Margin = new Thickness(0, 0, 6, 0) }; AutomationProperties.SetName(today, "今天的标签"); labels.Children.Add(today);
            var tomorrow = new TextBox { Text = settings.TomorrowLabel, MaxLength = 6, Margin = new Thickness(0, 0, 6, 0) }; AutomationProperties.SetName(tomorrow, "明天的标签"); Grid.SetColumn(tomorrow, 1); labels.Children.Add(tomorrow);
            var calendar = new TextBox { Text = settings.CalendarLabel, MaxLength = 6 }; AutomationProperties.SetName(calendar, "选日期的标签"); Grid.SetColumn(calendar, 2); labels.Children.Add(calendar); panel.Children.Add(labels);
            panel.Children.Add(Label("背景图片"));
            string chosenPath = settings.BackgroundImage;
            var imageName = new TextBlock { Text = String.IsNullOrEmpty(chosenPath) ? "当前使用默认浅色背景" : Path.GetFileName(chosenPath), Foreground = muted, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 10) };
            panel.Children.Add(imageName);
            var imageButtons = new StackPanel { Orientation = Orientation.Horizontal };
            Button choose = Button("选择本地图片…", delegate
            {
                var picker = new Microsoft.Win32.OpenFileDialog { Title = "选择小窗背景", Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp|所有文件|*.*", CheckFileExists = true, Multiselect = false };
                if (picker.ShowDialog(dialog) == true) { chosenPath = picker.FileName; imageName.Text = Path.GetFileName(chosenPath); }
            }); choose.Margin = new Thickness(0, 0, 8, 0); imageButtons.Children.Add(choose);
            Button clear = Button("恢复默认背景", delegate { chosenPath = ""; imageName.Text = "当前使用默认浅色背景"; }); imageButtons.Children.Add(clear); panel.Children.Add(imageButtons);
            panel.Children.Add(Label("图片深浅（向右更明显）"));
            var intensity = new Slider { Minimum = 0, Maximum = .30, Value = Math.Min(.30, settings.BackgroundOpacity), TickFrequency = .05, IsSnapToTickEnabled = true, Margin = new Thickness(0, 3, 0, 0) }; panel.Children.Add(intensity);
            panel.Children.Add(new TextBlock { Text = "周计划背景文字不显示标点，分号用于分行。\n图片完整显示在下方，图片深浅也会调整背景文字。", TextWrapping = TextWrapping.Wrap, Foreground = muted, FontSize = 11, LineHeight = 20, Margin = new Thickness(0, 10, 0, 14) });
            var feedback = new TextBlock { FontSize = 11, Foreground = ColorBrush("#B6564D"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) }; panel.Children.Add(feedback);
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Button defaults = Button("重置文字", delegate { title.Text = "每日计划"; subtitle.Text = "把一天，安排得刚刚好。"; today.Text = "今天"; tomorrow.Text = "明天"; calendar.Text = "选日期"; }); defaults.Margin = new Thickness(0, 0, 8, 0); footer.Children.Add(defaults);
            Button cancel = Button("取消", delegate { dialog.Close(); }); cancel.IsCancel = true; cancel.Margin = new Thickness(0, 0, 8, 0); footer.Children.Add(cancel);
            Button save = Button("保存外观", delegate
            {
                if (String.IsNullOrWhiteSpace(title.Text) || String.IsNullOrWhiteSpace(today.Text) || String.IsNullOrWhiteSpace(tomorrow.Text) || String.IsNullOrWhiteSpace(calendar.Text)) { feedback.Text = "主标题和日期标签不能为空。"; return; }
                string savedPath = chosenPath;
                try
                {
                    if (!String.IsNullOrEmpty(chosenPath) && chosenPath != store.Data.Settings.BackgroundImage)
                    {
                        if (!File.Exists(chosenPath)) throw new IOException("找不到选择的图片，请重新选择。");
                        if (new FileInfo(chosenPath).Length > 25 * 1024 * 1024) throw new IOException("请选择不超过 25 MB 的背景图片。");
                        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 1200; bitmap.UriSource = new Uri(Path.GetFullPath(chosenPath)); bitmap.EndInit();
                        string assets = Path.Combine(Path.GetDirectoryName(DataPath), "backgrounds"); Directory.CreateDirectory(assets);
                        savedPath = Path.Combine(assets, "background-" + Guid.NewGuid().ToString("N") + Path.GetExtension(chosenPath).ToLowerInvariant()); File.Copy(chosenPath, savedPath, false);
                    }
                    bool saved = Change(delegate
                    {
                        store.Data.Settings.AppTitle = title.Text.Trim(); store.Data.Settings.Subtitle = subtitle.Text.Trim();
                        store.Data.Settings.TodayLabel = today.Text.Trim(); store.Data.Settings.TomorrowLabel = tomorrow.Text.Trim(); store.Data.Settings.CalendarLabel = calendar.Text.Trim();
                        store.Data.Settings.BackgroundImage = savedPath ?? ""; store.Data.Settings.BackgroundOpacity = intensity.Value;
                    });
                    if (saved) { Render(); dialog.Close(); }
                }
                catch (Exception e) { feedback.Text = "外观未保存：" + e.Message; }
            }); save.Background = ColorBrush("#355B8F"); save.Foreground = Brushes.White; footer.Children.Add(save); panel.Children.Add(footer);
            dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            dialog.ShowDialog();
        }

        private string DataPath
        {
            get
            {
                string[] args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "--data-dir");
                return Path.Combine(index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data"), "plans.json");
            }
        }

        private bool Change(Action action, bool showError = true)
        {
            PlannerData snapshot;
            List<TaskItem> originalTasks = store.Data.Tasks.ToList();
            List<JournalEntry> originalJournal = store.Data.Journal.ToList();
            var serializer = new DataContractJsonSerializer(typeof(PlannerData));
            using (var stream = new MemoryStream()) { serializer.WriteObject(stream, store.Data); stream.Position = 0; snapshot = (PlannerData)serializer.ReadObject(stream); }
            try
            {
                action(); store.Save(); SetStatus("已保存到本机 · " + DateTime.Now.ToString("HH:mm"), false); return true;
            }
            catch (Exception e)
            {
                foreach (TaskItem original in originalTasks)
                {
                    TaskItem saved = snapshot.Tasks.First(t => t.Id == original.Id);
                    original.Title = saved.Title; original.Notes = saved.Notes; original.Date = saved.Date;
                    original.Scope = saved.Scope;
                    original.Progress = saved.Progress;
                    original.Completed = saved.Completed; original.CreatedAt = saved.CreatedAt; original.CompletedAt = saved.CompletedAt;
                }
                for (int i = 0; i < originalJournal.Count; i++)
                {
                    originalJournal[i].Date = snapshot.Journal[i].Date; originalJournal[i].Content = snapshot.Journal[i].Content; originalJournal[i].UpdatedAt = snapshot.Journal[i].UpdatedAt;
                }
                store.Data.Tasks = originalTasks; store.Data.Journal = originalJournal; store.Data.Settings = snapshot.Settings; store.Data.Version = snapshot.Version;
                SetStatus("保存失败 · 修改未生效", true);
                if (showError) MessageBox.Show(Window, "修改没有保存，已恢复修改前的记录。\n\n" + e.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }
        private void SetStatus(string text, bool error)
        {
            Find<Button>("SettingsButton").ToolTip = error ? "设置 · " + text : "设置";
            if (error && tray != null && text != lastErrorNotification)
                tray.ShowBalloonTip(5000, store.Data.Settings.AppTitle, text, Forms.ToolTipIcon.Warning);
            lastErrorNotification = error ? text : null;
        }
    }
}
