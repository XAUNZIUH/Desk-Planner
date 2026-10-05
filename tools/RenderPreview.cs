using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskPlanner
{
    // Documentation previews always use fictional data created here.
    internal static class RenderPreview
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Application app = null;
            try
            {
                if (args.Length != 1) throw new ArgumentException("Usage: RenderPreview output-directory");
                string images = Path.GetFullPath(args[0]);
                Directory.CreateDirectory(images);
                string fixture = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "demo-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(fixture);
                PlannerStore store = new PlannerStore(Path.Combine(fixture, "plans.json"));
                store.Data.Settings.AppTitle = "桌面计划";
                store.Data.Settings.Subtitle = "向内求真，向前求变；自我批评，自我革命。";
                store.Data.Settings.BackgroundOpacity = .22;
                store.Data.Settings.StackedLayout = true;
                store.Data.Settings.Width = 432;
                store.Data.Settings.Height = 920;
                DateTime today = DateTime.Today;
                DateTime week = PlannerDates.StartOfWeek(today);
                AddTask(store, "整理本周学习资料", "归档笔记和待解决的问题", week, true, 60, false);
                AddTask(store, "完成一个小练习", "选一个主题，动手验证", week, true, 30, false);
                AddTask(store, "阅读一章书", "记录三点收获", today, false, 0, false);
                AddTask(store, "练习 30 分钟", "给专注留一点时间", today, false, 0, false);
                AddTask(store, "整理今日笔记", "把问题写下来", today, false, 0, true);
                for (int index = 0; index < 18; index++)
                {
                    DateTime date = today.AddDays(-(index + index / 3));
                    store.Data.Journal.Add(new JournalEntry { Date = Key(date), UpdatedAt = DateTime.UtcNow.ToString("o"),
                        Content = index == 0 ? "今天读完一章书，把三个重点整理成了自己的笔记。\n\n练习时发现还有一个概念没想明白，明天先用一个简单例子验证。\n\n能说清楚为什么，比只记住结论更有帮助。"
                            : "整理了今天的阅读笔记，也完成了一个小练习。\n把暂时没想清楚的问题记下来，下次继续验证。" });
                }
                store.Save();
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                PlannerController controller = new PlannerController(store, true);
                Render(controller.Window, Path.Combine(images, "widget.png"));
                Window journal = (Window)typeof(PlannerController).GetMethod("CreateJournalDialog", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(controller, new object[] { today });
                Render(journal, Path.Combine(images, "journal.png"));
                journal.Close();
                controller.Window.Close();
                Console.WriteLine("Generated documentation images from fictional plans and journals only.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { if (app != null) app.Shutdown(); }
        }

        private static string Key(DateTime date) { return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }

        private static void AddTask(PlannerStore store, string title, string notes, DateTime date, bool weekly, int progress, bool done)
        {
            store.Data.Tasks.Add(new TaskItem { Id = Guid.NewGuid().ToString("N"), Title = title, Notes = notes, Date = Key(date),
                Scope = weekly ? TaskItem.WeeklyScope : TaskItem.DailyScope, Progress = progress, Completed = done,
                CreatedAt = DateTime.UtcNow.ToString("o"), CompletedAt = done ? DateTime.UtcNow.ToString("o") : "" });
        }

        private static void Render(Window window, string path)
        {
            FrameworkElement content = (FrameworkElement)window.Content;
            Size size = new Size(window.Width, window.Height);
            content.Measure(size);
            content.Arrange(new Rect(new Point(), size));
            content.UpdateLayout();
            RenderTargetBitmap bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * 1.5),
                (int)Math.Ceiling(size.Height * 1.5), 144, 144, PixelFormats.Pbgra32);
            if (window.Background != null)
            {
                DrawingVisual paper = new DrawingVisual();
                using (DrawingContext drawing = paper.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(new Point(), size));
                bitmap.Render(paper);
            }
            bitmap.Render(content);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(path)) encoder.Save(stream);
        }
    }
}
