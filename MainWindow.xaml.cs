#nullable disable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

using WinForms = System.Windows.Forms;

namespace WallpaperChanger
{
    public class CursorPreviewItem
    {
        public string Name { get; set; }
        public string IconPath { get; set; }
    }

    public partial class MainWindow : Window
    {
        // -------------------------------------------------------------
        // Win32
        // -------------------------------------------------------------
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfo", SetLastError = true)]
        private static extern bool SystemParametersInfoSet(int action, int param, IntPtr vparam, int init);

        private const int SPI_SETCURSORS = 0x0057;
        private const int SPIF_UPDATEINIFILE = 0x01;
        private const int SPIF_SENDCHANGE = 0x02;

        private const uint WM_SETTINGCHANGE = 0x001A;
        private const uint SMTO_ABORTIFHUNG = 0x0002;

        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc enumFunc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);
        [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr newParent);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("gdi32.dll", SetLastError = true)] public static extern bool DeleteObject(IntPtr hObject);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int max);
        [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        private static class NativeMethods64
        {
            [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
            [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        }

        private static class NativeMethods32
        {
            [DllImport("user32.dll", EntryPoint = "GetWindowLong")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
            [DllImport("user32.dll", EntryPoint = "SetWindowLong")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        }

        public static IntPtr GetWindowLongSafe(IntPtr hWnd, int nIndex) =>
            IntPtr.Size == 8 ? NativeMethods64.GetWindowLongPtr(hWnd, nIndex) : new IntPtr(NativeMethods32.GetWindowLong(hWnd, nIndex));

        public static IntPtr SetWindowLongSafe(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
            IntPtr.Size == 8 ? NativeMethods64.SetWindowLongPtr(hWnd, nIndex, dwNewLong) : new IntPtr(NativeMethods32.SetWindowLong(hWnd, nIndex, dwNewLong.ToInt32()));

        const int GWL_STYLE = -16;
        const long WS_CHILD = 0x40000000L;
        const long WS_VISIBLE = 0x10000000L;
        const long WS_POPUP = 0x80000000L;
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint SWP_FRAMECHANGED = 0x0020;

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx, cy; public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; } }
        public enum SIIGBF { SIIGBF_RESIZEROWETHEM = 0x00000000, SIIGBF_BIGGERSIZEOK = 0x00000001 }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("bcc827b3-ec5c-41a2-b508-73724750062b")]
        public interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        public static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, out IShellItemImageFactory ppv);

        // -------------------------------------------------------------
        // الحالة
        // -------------------------------------------------------------
        public ObservableCollection<WallpaperItem> WallpapersList { get; } = new ObservableCollection<WallpaperItem>();
        public ObservableCollection<WallpaperItem> VideosList { get; } = new ObservableCollection<WallpaperItem>();

        private ICollectionView imagesView;
        private ICollectionView videosView;

        private readonly Random rand = new Random();
        private readonly List<WallpaperItem> imageDeck = new List<WallpaperItem>();
        private readonly List<WallpaperItem> videoDeck = new List<WallpaperItem>();
        private WallpaperItem currentActiveImage = null;
        private WallpaperItem currentActiveVideo = null;

        private const string AppName = "MostafaWallpaperChanger";
        private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        private readonly string saveFilePath = Path.Combine(AppData, "MostafaWallpapers.txt");
        private readonly string saveVideosPath = Path.Combine(AppData, "MostafaVideos.txt");
        private readonly string settingsFilePath = Path.Combine(AppData, "MostafaSettings.txt");
        private readonly string cursorsFilePath = Path.Combine(AppData, "MostafaCursors.txt");

        private readonly DispatcherTimer timer = new DispatcherTimer();
        private readonly DispatcherTimer coverTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private readonly DispatcherTimer hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        private Border hoverBorder;
        private WinForms.NotifyIcon notifyIcon;

        private VideoWindow currentVideoWindow = null;
        private bool isChangingMedia = false;
        private bool videoMode = false;
        private bool videoPaused = false;

        private static readonly Dictionary<string, ImageSource> ThumbCache = new Dictionary<string, ImageSource>();
        private static readonly SemaphoreSlim thumbGate = new SemaphoreSlim(3); // أقصى 3 صور مصغّرة بيتحمّلوا في نفس الوقت

        private static EnumWindowsProc _enumProc = EnumCallback;
        private static IntPtr _workerW = IntPtr.Zero;
        private IntPtr _progman = IntPtr.Zero;

        // -------------------------------------------------------------
        // دوال مساعدة صغيرة (كانت متكررة)
        // -------------------------------------------------------------
        private static string FormatSize(long bytes) =>
            bytes >= 1048576 ? (bytes / 1048576.0).ToString("0.00") + " MB" : (bytes / 1024.0).ToString("0.00") + " KB";

        private static Uri ToMediaUri(string path) =>
            new Uri(path.Replace("%", "%25").Replace("#", "%23"), UriKind.Absolute);

        private static void Msg(string text, string title, MessageBoxImage icon) =>
            System.Windows.MessageBox.Show(text, title, MessageBoxButton.OK, icon);

        private static SolidColorBrush B(string hex) =>
            new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

        // -------------------------------------------------------------
        // محرك الصور المصغّرة
        // -------------------------------------------------------------
        public static ImageSource GetShellThumbnailFast(string filePath)
        {
            lock (ThumbCache)
            {
                if (ThumbCache.TryGetValue(filePath, out var cached)) return cached;
            }

            try
            {
                Guid uuid = new Guid("bcc827b3-ec5c-41a2-b508-73724750062b");
                if (SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref uuid, out IShellItemImageFactory factory) == 0 && factory != null)
                {
                    try
                    {
                        if (factory.GetImage(new SIZE(256, 144), SIIGBF.SIIGBF_BIGGERSIZEOK, out IntPtr hBitmap) == 0 && hBitmap != IntPtr.Zero)
                        {
                            BitmapSource src = Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            src.Freeze();
                            DeleteObject(hBitmap);
                            lock (ThumbCache) { ThumbCache[filePath] = src; }
                            return src;
                        }
                    }
                    finally { Marshal.ReleaseComObject(factory); }
                }
            }
            catch { }
            return null;
        }

        public static ImageSource LoadImageThumbnailFast(string path)
        {
            lock (ThumbCache)
            {
                if (ThumbCache.TryGetValue(path, out var cached)) return cached;
            }

            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = fs;
                    bmp.DecodePixelWidth = 200;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bmp.EndInit();
                    bmp.Freeze();

                    lock (ThumbCache) { ThumbCache[path] = bmp; }
                    return bmp;
                }
            }
            catch { return null; }
        }

        // لازم تتنادى من UI thread. بتاخد لقطة من الفيديو لو الشيل ما رجّعش صورة.
        private static Task<ImageSource> CaptureVideoFrameAsync(string videoPath)
        {
            var tcs = new TaskCompletionSource<ImageSource>();
            // تغيير MediaPlayer إلى System.Windows.Media.MediaPlayer
            var player = new System.Windows.Media.MediaPlayer { ScrubbingEnabled = true };
            var frameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };

            void Finish(ImageSource result)
            {
                frameTimer.Stop();
                timeout.Stop();
                try { player.Close(); } catch { }
                tcs.TrySetResult(result);
            }

            frameTimer.Tick += (s, e) =>
            {
                try
                {
                    var rtb = new RenderTargetBitmap(160, 90, 96, 96, PixelFormats.Pbgra32);
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen()) dc.DrawVideo(player, new Rect(0, 0, 160, 90));
                    rtb.Render(dv);
                    rtb.Freeze();
                    Finish(rtb);
                }
                catch { Finish(null); }
            };
            timeout.Tick += (s, e) => Finish(null);

            // التايمر بيبدأ بعد ما الفيديو يتفتح فعلاً (قبل كده كان بيلتقط صورة سودا)
            player.MediaOpened += (s, e) => { player.Position = TimeSpan.FromSeconds(1); frameTimer.Start(); };
            player.MediaFailed += (s, e) => Finish(null);

            try
            {
                timeout.Start();
                player.Open(ToMediaUri(videoPath));
            }
            catch { Finish(null); }

            return tcs.Task;
        }

        private async Task LoadThumbAsync(WallpaperItem item, bool isVideo)
        {
            if (item.Thumbnail != null) return;

            await thumbGate.WaitAsync();
            try
            {
                string path = item.FilePath;
                ImageSource thumb;

                if (isVideo)
                {
                    thumb = await Task.Run(() => GetShellThumbnailFast(path)) ?? await CaptureVideoFrameAsync(path);
                }
                else
                {
                    thumb = await Task.Run(() => LoadImageThumbnailFast(path) ?? GetShellThumbnailFast(path));
                }

                if (thumb != null) item.Thumbnail = thumb;
            }
            finally { thumbGate.Release(); }
        }

        // -------------------------------------------------------------
        // إيقاف الفيديو لما شاشة كاملة تغطي سطح المكتب
        // -------------------------------------------------------------
        private bool IsDesktopCovered()
        {
            if (SHQueryUserNotificationState(out int s) == 0 && s >= 1 && s <= 4) return true;

            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;

            var sb = new System.Text.StringBuilder(64);
            GetClassName(fg, sb, sb.Capacity);
            string cls = sb.ToString();
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd") return false;

            return IsZoomed(fg);
        }

        private void CoverTimer_Tick(object sender, EventArgs e)
        {
            if (currentVideoWindow == null) return;
            bool covered = IsDesktopCovered();
            if (covered == videoPaused) return;

            videoPaused = covered;
            if (covered) currentVideoWindow.PauseVideo(); else currentVideoWindow.ResumeVideo();
        }

        // -------------------------------------------------------------
        // المعاينة الكبيرة
        // -------------------------------------------------------------
        private void Thumb_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            StopPreview();
            hoverBorder = sender as Border;
            hoverTimer.Stop();
            hoverTimer.Start();
        }

        private void Thumb_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            hoverTimer.Stop();
            StopPreview();
        }

        private void StartPreview(Border border)
        {
            if (border == null || !border.IsMouseOver || !(border.DataContext is WallpaperItem item)) return;

            try
            {
                PreviewTitle.Text = item.FileName;

                if (item.IsVideo)
                {
                    PreviewImage.Visibility = Visibility.Collapsed;
                    PreviewVideo.Visibility = Visibility.Visible;
                    PreviewVideo.Source = ToMediaUri(item.FilePath);
                    PreviewVideo.Play();
                }
                else
                {
                    PreviewVideo.Visibility = Visibility.Collapsed;
                    PreviewImage.Visibility = Visibility.Visible;
                    PreviewImage.Source = item.Thumbnail ?? LoadImageThumbnailFast(item.FilePath);
                }

                PreviewPanel.Visibility = Visibility.Visible;
            }
            catch { StopPreview(); }
        }

        private void StopPreview()
        {
            try { PreviewVideo.Stop(); PreviewVideo.Close(); PreviewVideo.Source = null; } catch { }
            PreviewImage.Source = null;
            PreviewPanel.Visibility = Visibility.Collapsed;
        }

        private void PreviewVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            try { PreviewVideo.Position = TimeSpan.Zero; PreviewVideo.Play(); } catch { }
        }

        private void PreviewVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e) => StopPreview();

        // -------------------------------------------------------------
        // البناء
        // -------------------------------------------------------------
        public MainWindow()
        {
            InitializeComponent();
            ImagesListBox.ItemsSource = WallpapersList;
            VideosListBox.ItemsSource = VideosList;

            imagesView = CollectionViewSource.GetDefaultView(WallpapersList);
            videosView = CollectionViewSource.GetDefaultView(VideosList);

            cursorCards = new Dictionary<string, CursorCard>
            {
                ["dark"] = new CursorCard { List = icDarkCursors, Name = lblDarkName, Size = lblDarkSize, Path = lblDarkPath },
                ["light"] = new CursorCard { List = icLightCursors, Name = lblLightName, Size = lblLightSize, Path = lblLightPath }
            };

            LoadSavedData();
            CheckStartupState();
            LoadSettings();
            LoadCursorSelections();

            timer.Tick += Timer_Tick;
            timer.Start();

            // الـ coverTimer بيشتغل بس لما يكون فيه فيديو شغال (كان شغال دايماً من غير لازمة)
            coverTimer.Tick += CoverTimer_Tick;

            hoverTimer.Tick += (s, a) => { hoverTimer.Stop(); StartPreview(hoverBorder); };

            try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; } catch { }

            SetupTrayIcon();

            string[] args = Environment.GetCommandLineArgs();
            if (args.Contains("-startup"))
            {
                this.WindowState = WindowState.Minimized;
                this.ShowInTaskbar = false;
                this.Hide();
                ChangeMediaNow(false);
            }
        }

        private static DependencyObject GetParentSafe(DependencyObject d)
        {
            if (d is Visual || d is System.Windows.Media.Media3D.Visual3D) return VisualTreeHelper.GetParent(d);
            return LogicalTreeHelper.GetParent(d);
        }

        private bool IsInsideSettings(DependencyObject source)
        {
            for (DependencyObject p = source; p != null; p = GetParentSafe(p))
            {
                if (p == btnSettings || (SettingsPopup != null && p == SettingsPopup.Child)) return true;
                if (p is System.Windows.Controls.ComboBox || p is System.Windows.Controls.ComboBoxItem) return true;
                if (p.GetType().Name == "PopupRoot") return true;
            }
            return false;
        }

        private void Window_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            bool insideSettings = IsInsideSettings(source);

            if (SettingsPopup != null && SettingsPopup.IsOpen && !insideSettings) SettingsPopup.IsOpen = false;
            if (insideSettings) return;

            for (DependencyObject p = source; p != null; p = GetParentSafe(p))
                if (p is System.Windows.Controls.TextBox) return;

            // احفظ اسم الملف اللي كان بيتعدّل قبل ما نشيل الفوكس
            if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox tb)
                tb.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();

            System.Windows.Input.Keyboard.ClearFocus();
        }

        // Enter في خانة اسم الملف = حفظ
        private void NameBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            if (sender is System.Windows.Controls.TextBox tb)
                tb.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            System.Windows.Input.Keyboard.ClearFocus();
            e.Handled = true;
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            if (SettingsPopup != null) SettingsPopup.IsOpen = false;
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string filterText = txtSearch.Text.Trim();

            Predicate<object> filter = item =>
            {
                if (string.IsNullOrWhiteSpace(filterText)) return true;
                return item is WallpaperItem wp && wp.FileName.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0;
            };

            if (imagesView != null) imagesView.Filter = filter;
            if (videosView != null) videosView.Filter = filter;
        }

        private WallpaperItem GetNextMediaItem(ObservableCollection<WallpaperItem> fullList, List<WallpaperItem> deck, ref WallpaperItem currentActive)
        {
            var activeItems = fullList.Where(w => w.IsSelected).ToList();
            if (activeItems.Count == 0) return null;
            if (activeItems.Count == 1) return currentActive = activeItems[0];

            var activeSet = new HashSet<WallpaperItem>(activeItems);
            deck.RemoveAll(item => !activeSet.Contains(item));

            if (deck.Count == 0)
            {
                var temp = new List<WallpaperItem>(activeItems);
                int n = temp.Count;
                while (n > 1)
                {
                    n--;
                    int k = rand.Next(n + 1);
                    var value = temp[k]; temp[k] = temp[n]; temp[n] = value;
                }
                deck.AddRange(temp);
            }

            currentActive = deck[0];
            deck.RemoveAt(0);
            return currentActive;
        }

        // -------------------------------------------------------------
        // الإعدادات / الثيم / اللغة
        // -------------------------------------------------------------
        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsPopup != null)
            {
                bool targetState = !SettingsPopup.IsOpen;
                DoubleAnimation rotateAnim = new DoubleAnimation
                {
                    To = targetState ? 180 : 0,
                    Duration = TimeSpan.FromSeconds(0.35),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                settingsRotateTransform.BeginAnimation(RotateTransform.AngleProperty, rotateAnim);
                SettingsPopup.IsOpen = targetState;
            }
        }

        private void SettingsPopup_Opened(object sender, EventArgs e)
        {
            DoubleAnimation opacityAnim = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.25));
            DoubleAnimation scaleYAnim = new DoubleAnimation(0.85, 1, TimeSpan.FromSeconds(0.25)) { EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut } };
            SettingsPopupBorder.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
            SettingsScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
        }

        private void cmbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbTheme == null || !this.IsLoaded) return;
            string[] keys = { "MainTextBrush", "SubTextBrush", "CardBgBrush", "CardBorderBrush", "HeaderBgBrush", "ButtonBgBrush", "ButtonBorderBrush", "ButtonHoverBgBrush" };
            string[] v = cmbTheme.SelectedIndex == 1
                ? new[] { "#F5F5F7", "#121216", "#555566", "#FFFFFF", "#DDDDEE", "#E8E8EE", "#E4E4EC", "#CCCCCC", "#D0D0DD" }
                : new[] { "#1A1A24", "#FFFFFF", "#A8A8B8", "#242432", "#323246", "#242432", "#2E2E40", "#42425A", "#3E3E56" };

            this.Background = B(v[0]);
            for (int i = 0; i < keys.Length; i++) this.Resources[keys[i]] = B(v[i + 1]);
        }

        private void cmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbLanguage == null || !this.IsLoaded) return;
            bool isEng = cmbLanguage.SelectedIndex == 1;

            this.FlowDirection = isEng ? System.Windows.FlowDirection.LeftToRight : System.Windows.FlowDirection.RightToLeft;
            this.Resources["StrSize"] = isEng ? "Size: " : "الحجم: ";
            this.Resources["StrChange"] = isEng ? "Change" : "تغيير";
            this.Resources["StrOpenFolder"] = isEng ? "Open Folder" : "فتح الموقع";
            this.Resources["StrSetNowImage"] = isEng ? "Apply Image" : "تعيين الصورة";
            this.Resources["StrSetNowVideo"] = isEng ? "Apply Video" : "تعيين الفيديو";

            if (txtBtnSettings != null) txtBtnSettings.Text = isEng ? "Settings" : "إعدادات";
            if (lblChangeEvery != null) lblChangeEvery.Text = isEng ? "Change every:" : "تغيير كل:";
            if (lblMinutes != null) lblMinutes.Text = isEng ? "Minutes" : "دقيقة";
            if (txtAdd != null) txtAdd.Text = isEng ? "Add" : "إضافة";
            if (txtChangeNow != null) txtChangeNow.Text = isEng ? "Change Wallpaper" : "تغيير الخلفية";
            if (tabImages != null) tabImages.Header = isEng ? "Photos" : "الـصـور";
            if (tabVideos != null) tabVideos.Header = isEng ? "Videos" : "الفيديوهات";
            if (lblSettingsHeader != null) lblSettingsHeader.Text = isEng ? "General Settings" : "الإعدادات العامة";
            if (chkStartup != null) chkStartup.Content = isEng ? "Run on system startup" : "تشغيل مع بداية النظام";
            if (chkLockImages != null) chkLockImages.Content = isEng ? "Lock automatic photos" : "قفل الصور التلقائي";
            if (chkLockVideos != null) chkLockVideos.Content = isEng ? "Lock automatic videos" : "قفل الفيديوهات التلقائي";
            if (lblTheme != null) lblTheme.Text = isEng ? "Theme:" : "تغيير اللون:";
            if (lblLang != null) lblLang.Text = isEng ? "Language:" : "اللغة:";
            if (cmbItemDark != null) cmbItemDark.Content = isEng ? "Dark" : "أسود";
            if (cmbItemLight != null) cmbItemLight.Content = isEng ? "Light" : "أبيض";
        }

        // =============================================================
        // تبويب "ثيم"
        // =============================================================
        private void BtnSetDarkTheme_Click(object sender, RoutedEventArgs e) => SetSystemThemeMode(true);
        private void BtnSetLightTheme_Click(object sender, RoutedEventArgs e) => SetSystemThemeMode(false);

        private void SetSystemThemeMode(bool isDark)
        {
            try
            {
                int value = isDark ? 0 : 1;

                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", true))
                {
                    if (key != null)
                    {
                        key.SetValue("AppsUseLightTheme", value, RegistryValueKind.DWord);
                        key.SetValue("SystemUsesLightTheme", value, RegistryValueKind.DWord);
                    }
                }

                IntPtr param = Marshal.StringToHGlobalUni("ImmersiveColorSet");
                try { SendMessageTimeout(new IntPtr(0xffff), WM_SETTINGCHANGE, IntPtr.Zero, param, SMTO_ABORTIFHUNG, 100, out _); }
                finally { Marshal.FreeHGlobal(param); } // كان بيسرّب ذاكرة

                Msg(isDark ? "تم تطبيق الوضع الداكن للنظام بنجاح." : "تم تطبيق الوضع الفاتح للنظام بنجاح.", "تغيير الثيم", MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Msg($"حدث خطأ أثناء تغيير ثيم النظام:\n{ex.Message}", "خطأ", MessageBoxImage.Error);
            }
        }

        // =============================================================
        // تبويب "ماوس"
        // كل كارت (dark / light) له: اختار + تطبيق + مسح، ومعلومات الاسم والحجم والمسار.
        //
        // سبب إن الماوس ما كانش بيتغيّر: الكود القديم كان يستدعي SetSystemCursor
        // وبعدها SPI_SETCURSORS، والأخيرة بتعمل "reload" للمؤشرات من الـ Registry
        // فبتمسح التغيير. الحل: نكتب المسارات في الـ Registry الأول وبعدها نعمل reload.
        // =============================================================
        private sealed class CursorCard
        {
            public ItemsControl List;
            public TextBlock Name, Size, Path;
            public string Folder = "";
        }

        private Dictionary<string, CursorCard> cursorCards;

        // اسم القيمة في الـ Registry + الكلمات اللي بندوّر بيها في أسماء الملفات (بالترتيب)
        private static readonly (string Value, string[] Keys)[] CursorRoles =
        {
            ("Help",        new[] { "help" }),
            ("AppStarting", new[] { "workinginbackground", "working", "background", "appstarting", "progress" }),
            ("Wait",        new[] { "busy", "wait" }),
            ("Crosshair",   new[] { "precision", "cross" }),
            ("IBeam",       new[] { "text", "ibeam" }),
            ("NWPen",       new[] { "handwriting", "pen" }),
            ("No",          new[] { "unavailable", "notallowed", "no" }),
            ("SizeNS",      new[] { "vertical", "ns", "size4" }),
            ("SizeWE",      new[] { "horizontal", "we", "ew", "size3" }),
            ("SizeNWSE",    new[] { "diagonalresize1", "diagonal1", "nwse", "size1" }),
            ("SizeNESW",    new[] { "diagonalresize2", "diagonal2", "nesw", "size2" }),
            ("SizeAll",     new[] { "move", "sizeall" }),
            ("UpArrow",     new[] { "alternate", "uparrow", "up" }),
            ("Pin",         new[] { "location", "pin" }),
            ("Person",      new[] { "person" }),
            ("Hand",        new[] { "link", "hand" }),
            ("Arrow",       new[] { "normal", "arrow", "pointer", "default", "select" }),
        };

        private static bool IsCursorFile(string f)
        {
            string ext = Path.GetExtension(f);
            return ext.Equals(".cur", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ani", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasCursorFiles(string dir)
        {
            try { return Directory.EnumerateFiles(dir).Any(IsCursorFile); } catch { return false; }
        }

        private static bool NameMatches(string file, string key)
        {
            string stem = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            string[] tokens = stem.Split(new[] { ' ', '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries);
            return key.Length <= 3 ? tokens.Contains(key) : string.Concat(tokens).Contains(key);
        }

        private static string FindCursorFile(List<string> files, HashSet<string> used, string[] keys)
        {
            foreach (string key in keys)
                foreach (string f in files)
                    if (!used.Contains(f) && NameMatches(f, key)) return f;
            return null;
        }

        private bool TryGetCard(object sender, out string tag, out CursorCard card)
        {
            tag = (sender as FrameworkElement)?.Tag as string;
            card = null;
            return tag != null && cursorCards.TryGetValue(tag, out card);
        }

        // لو الفولدر اللي اتختار جواه فولدر dark/light ومفيهوش مؤشرات مباشرة، ندخل عليه
        private static string ResolveCursorDir(string dir, string tag)
        {
            if (!HasCursorFiles(dir))
            {
                string sub = Path.Combine(dir, tag);
                if (Directory.Exists(sub) && HasCursorFiles(sub)) return sub;
            }
            return dir;
        }

        private void ShowCursorFolder(string tag, string folder)
        {
            var card = cursorCards[tag];
            string dir = ResolveCursorDir(folder, tag);
            card.Folder = dir;

            var files = Directory.EnumerateFiles(dir).Where(IsCursorFile)
                                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            long bytes = files.Sum(f => new FileInfo(f).Length);

            var info = new DirectoryInfo(dir);
            string name = info.Name.Equals(tag, StringComparison.OrdinalIgnoreCase) ? (info.Parent?.Name ?? info.Name) : info.Name;

            card.List.ItemsSource = files.Select(f => new CursorPreviewItem { Name = Path.GetFileNameWithoutExtension(f), IconPath = f }).ToList();
            card.Name.Text = "اسم الثيم: " + name;
            card.Size.Text = "حجمه: " + FormatSize(bytes) + $"  ({files.Count} ملف)";
            card.Path.Text = "مساره: " + dir;
            card.Path.ToolTip = dir;
        }

        private void BtnChooseCursor_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetCard(sender, out string tag, out CursorCard card)) return;

            using (var dlg = new WinForms.FolderBrowserDialog
            {
                Description = tag == "dark" ? "اختار فولدر مؤشرات الوضع الأسود (Dark)" : "اختار فولدر مؤشرات الوضع الأبيض (Light)",
                ShowNewFolderButton = false,
                SelectedPath = Directory.Exists(card.Folder) ? card.Folder : ""
            })
            {
                if (dlg.ShowDialog() != WinForms.DialogResult.OK) return;

                string resolved = ResolveCursorDir(dlg.SelectedPath, tag);
                if (!HasCursorFiles(resolved))
                {
                    Msg("الفولدر ده مفيهوش ملفات مؤشرات (.cur / .ani).", "تنبيه", MessageBoxImage.Warning);
                    return;
                }

                ShowCursorFolder(tag, resolved);
                SaveCursorSelections();
            }
        }

        private void BtnClearCursor_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetCard(sender, out string tag, out CursorCard card)) return;

            card.Folder = "";
            card.List.ItemsSource = null;
            card.Name.Text = "اسم الثيم: -";
            card.Size.Text = "حجمه: -";
            card.Path.Text = "مساره: -";
            card.Path.ToolTip = null;
            SaveCursorSelections();
        }

        private void BtnApplyCursor_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetCard(sender, out string tag, out CursorCard card)) return;

            if (string.IsNullOrEmpty(card.Folder) || !Directory.Exists(card.Folder))
            {
                Msg("اختار فولدر المؤشرات الأول بزر 'اختار'.", "تنبيه", MessageBoxImage.Warning);
                return;
            }

            var files = Directory.EnumerateFiles(card.Folder).Where(IsCursorFile).ToList();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matched = new List<KeyValuePair<string, string>>();
            var missing = new List<string>();

            foreach (var role in CursorRoles)
            {
                string found = FindCursorFile(files, used, role.Keys);
                if (found == null) { missing.Add(role.Value); continue; }
                used.Add(found);
                matched.Add(new KeyValuePair<string, string>(role.Value, found));
            }

            if (matched.Count == 0)
            {
                string names = string.Join("\n", files.Take(20).Select(Path.GetFileName));
                Msg("ما قدرتش أعرف أسماء ملفات المؤشرات دي. الملفات اللي لقيتها:\n\n" + names, "خطأ", MessageBoxImage.Error);
                return;
            }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors", true))
                {
                    if (key == null) throw new InvalidOperationException("مش قادر أفتح مفتاح المؤشرات في الـ Registry.");

                    foreach (var m in matched) key.SetValue(m.Key, m.Value, RegistryValueKind.ExpandString);

                    string name = new DirectoryInfo(card.Folder).Name;
                    key.SetValue("", name, RegistryValueKind.String);   // (Default) = اسم المجموعة
                    key.SetValue("Scheme Source", 1, RegistryValueKind.DWord);
                }

                // دلوقتي الـ reload هيقرا القيم الجديدة من الـ Registry
                SystemParametersInfoSet(SPI_SETCURSORS, 0, IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);

                string text = $"تم تطبيق {matched.Count} مؤشر ({(tag == "dark" ? "Dark" : "Light")}) بنجاح.";
                if (missing.Count > 0) text += "\n\nمؤشرات ملقتهاش في الفولدر (فضلت زي ما هي): " + string.Join(", ", missing);
                Msg(text, "نجاح", MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Msg("فشل تطبيق المؤشرات:\n" + ex.Message, "خطأ", MessageBoxImage.Error);
            }
        }

        private void SaveCursorSelections()
        {
            try { File.WriteAllLines(cursorsFilePath, new[] { cursorCards["dark"].Folder, cursorCards["light"].Folder }); } catch { }
        }

        private void LoadCursorSelections()
        {
            try
            {
                if (!File.Exists(cursorsFilePath)) return;
                string[] lines = File.ReadAllLines(cursorsFilePath);
                string[] tags = { "dark", "light" };
                for (int i = 0; i < tags.Length && i < lines.Length; i++)
                    if (!string.IsNullOrWhiteSpace(lines[i]) && Directory.Exists(lines[i])) ShowCursorFolder(tags[i], lines[i]);
            }
            catch { }
        }

        // =============================================================
        // الصور والفيديوهات
        // =============================================================
        private WallpaperItem CreateItem(string path, bool isVideo, string index, bool selected)
        {
            var fi = new FileInfo(path);
            return new WallpaperItem(this)
            {
                IsVideo = isVideo,
                Index = index,
                FilePath = path,
                FileName = fi.Name,
                FileSize = FormatSize(fi.Exists ? fi.Length : 0),
                IsSelected = selected
            };
        }

        private void AddFileToList(string filepath, ObservableCollection<WallpaperItem> list)
        {
            if (list.Any(w => string.Equals(w.FilePath, filepath, StringComparison.OrdinalIgnoreCase))) return;

            bool isVideo = ReferenceEquals(list, VideosList);
            var item = CreateItem(filepath, isVideo, (list.Count + 1) + " -", true);
            list.Add(item);
            _ = LoadThumbAsync(item, isVideo);
        }

        private void LoadSavedData() { LoadList(saveFilePath, WallpapersList); LoadList(saveVideosPath, VideosList); }

        private void LoadList(string path, ObservableCollection<WallpaperItem> list)
        {
            try
            {
                if (File.Exists(path))
                {
                    foreach (string file in File.ReadAllLines(path))
                        if (File.Exists(file)) AddFileToList(file, list);
                }
            }
            catch { }
        }

        public void SaveAllData()
        {
            try
            {
                File.WriteAllLines(saveFilePath, WallpapersList.Select(w => w.FilePath).ToArray());
                File.WriteAllLines(saveVideosPath, VideosList.Select(w => w.FilePath).ToArray());
            }
            catch { }
        }

        private void BtnSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            bool isVideoTab = MainTabs.SelectedIndex == 1;
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = isVideoTab ? "اختار الفيديوهات" : "اختار الصور",
                Multiselect = true,
                Filter = isVideoTab ? "Video Files|*.mp4;*.wmv;*.avi;*.mov;*.mkv" : "Image Files|*.jpg;*.jpeg;*.png"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                foreach (string filename in openFileDialog.FileNames) AddFileToList(filename, isVideoTab ? VideosList : WallpapersList);
                SaveAllData();
            }
        }

        private void BtnChange_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as System.Windows.Controls.Button)?.DataContext is WallpaperItem item)) return;

            bool isVideo = item.IsVideo;
            var list = isVideo ? VideosList : WallpapersList;
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "اختار الملف الجديد",
                Filter = isVideo ? "Video Files|*.mp4;*.wmv;*.avi;*.mov;*.mkv" : "Image Files|*.jpg;*.jpeg;*.png"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                int idx = list.IndexOf(item);
                var newItem = CreateItem(openFileDialog.FileName, isVideo, item.Index, item.IsSelected);
                list[idx] = newItem;
                _ = LoadThumbAsync(newItem, isVideo);
                SaveAllData();
            }
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as System.Windows.Controls.Button)?.DataContext is WallpaperItem item && !string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.FilePath}\"");
            }
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as System.Windows.Controls.Button)?.DataContext is WallpaperItem item)
            {
                var list = item.IsVideo ? VideosList : WallpapersList;
                list.Remove(item);
                for (int i = 0; i < list.Count; i++) list[i].Index = (i + 1) + " -";
                SaveAllData();
            }
        }

        private void BtnSetNow_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as System.Windows.Controls.Button)?.DataContext is WallpaperItem item)) return;

            bool isVideo = item.IsVideo;
            if (!isVideo && chkLockImages?.IsChecked == true) return;
            if (isVideo && chkLockVideos?.IsChecked == true) return;

            videoMode = isVideo;

            if (isVideo)
            {
                currentActiveVideo = item;
                videoDeck.Remove(item);
                SetVideoAsWallpaper(item.FilePath);
            }
            else
            {
                SetImageWallpaper(item.FilePath);
                currentActiveImage = item;
                imageDeck.Remove(item);
            }
        }

        // دالة واحدة لتعيين الصورة (كانت مكررة في BtnSetNow و ChangeMediaNow)
        private void SetImageWallpaper(string path)
        {
            if (isChangingMedia) return;
            isChangingMedia = true;
            try
            {
                CloseExistingVideoWindow();
                SystemParametersInfo(0x0014, 0, path, 0x01 | 0x02);
            }
            finally { isChangingMedia = false; }
        }

        private void CloseExistingVideoWindow()
        {
            coverTimer.Stop();
            videoPaused = false;

            if (currentVideoWindow != null)
            {
                try { currentVideoWindow.StopAndClose(); } catch { }
                currentVideoWindow = null;
            }
        }

        private static bool EnumCallback(IntPtr hwnd, IntPtr lParam)
        {
            if (FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            {
                _workerW = FindWindowEx(IntPtr.Zero, hwnd, "WorkerW", null);
            }
            return true;
        }

        private IntPtr GetWorkerW()
        {
            _progman = FindWindow("Progman", null);
            if (_progman == IntPtr.Zero) return IntPtr.Zero;

            SendMessageTimeout(_progman, 0x052C, new IntPtr(0xD), new IntPtr(1), 0x0000, 1000, out _);
            _workerW = IntPtr.Zero;

            EnumWindows(_enumProc, IntPtr.Zero);
            return _workerW != IntPtr.Zero ? _workerW : _progman;
        }

        private void SetVideoAsWallpaper(string videoPath)
        {
            if (isChangingMedia) return;
            isChangingMedia = true;

            try
            {
                if (currentVideoWindow != null && currentVideoWindow.IsLoaded)
                {
                    currentVideoWindow.Dispatcher.Invoke(() => currentVideoWindow.PlayVideo(videoPath));
                    videoPaused = false;
                    coverTimer.Start();
                    return;
                }

                IntPtr targetWindow = GetWorkerW();
                if (targetWindow == IntPtr.Zero) return;

                CloseExistingVideoWindow();

                currentVideoWindow = new VideoWindow
                {
                    Width = SystemParameters.VirtualScreenWidth,
                    Height = SystemParameters.VirtualScreenHeight,
                    Left = SystemParameters.VirtualScreenLeft,
                    Top = SystemParameters.VirtualScreenTop
                };

                currentVideoWindow.Show();

                IntPtr hwnd = new WindowInteropHelper(currentVideoWindow).Handle;
                IntPtr currentStyle = GetWindowLongSafe(hwnd, GWL_STYLE);
                long newStyle = (currentStyle.ToInt64() | WS_CHILD | WS_VISIBLE) & ~WS_POPUP;
                SetWindowLongSafe(hwnd, GWL_STYLE, new IntPtr(newStyle));

                SetParent(hwnd, targetWindow);

                IntPtr zOrderAfter = IntPtr.Zero;
                uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED;

                if (targetWindow == _progman)
                {
                    IntPtr defView = FindWindowEx(_progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (defView != IntPtr.Zero) zOrderAfter = defView;
                    else flags |= SWP_NOZORDER;
                }
                else flags |= SWP_NOZORDER;

                SetWindowPos(hwnd, zOrderAfter, 0, 0, 0, 0, flags);
                currentVideoWindow.PlayVideo(videoPath);
                videoPaused = false;
                coverTimer.Start();
            }
            catch (Exception ex)
            {
                Msg("خطأ في تهيئة خلفية الفيديو: " + ex.Message, "خطأ", MessageBoxImage.Error);
            }
            finally
            {
                isChangingMedia = false;
            }
        }

        private void Timer_Tick(object sender, EventArgs e) => ChangeMediaNow(videoMode);

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (refreshRotateTransform != null)
            {
                DoubleAnimation rotateAnimation = new DoubleAnimation
                {
                    From = 0,
                    To = 360,
                    Duration = TimeSpan.FromSeconds(0.45),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
                };
                refreshRotateTransform.BeginAnimation(RotateTransform.AngleProperty, rotateAnimation);
            }
            ChangeMediaNow(MainTabs.SelectedIndex == 1);
        }

        private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source is System.Windows.Controls.TabControl && MainTabs.SelectedItem is TabItem selectedTab && selectedTab.Content is FrameworkElement content)
            {
                DoubleAnimation opacityAnim = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.2));
                TranslateTransform translate = new TranslateTransform(0, 8);
                content.RenderTransform = translate;
                DoubleAnimation translateAnim = new DoubleAnimation(8, 0, TimeSpan.FromSeconds(0.2)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };

                content.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
                translate.BeginAnimation(TranslateTransform.YProperty, translateAnim);
            }
        }

        private void ChangeMediaNow(bool video)
        {
            if (isChangingMedia) return;

            if (video)
            {
                if (chkLockVideos?.IsChecked == true) return;
                var nextVideo = GetNextMediaItem(VideosList, videoDeck, ref currentActiveVideo);
                if (nextVideo != null) { videoMode = true; SetVideoAsWallpaper(nextVideo.FilePath); }
            }
            else
            {
                if (chkLockImages?.IsChecked == true) return;
                var nextImage = GetNextMediaItem(WallpapersList, imageDeck, ref currentActiveImage);
                if (nextImage != null)
                {
                    videoMode = false;
                    SetImageWallpaper(nextImage.FilePath);
                }
            }
        }

        // -------------------------------------------------------------
        // التايمر
        // -------------------------------------------------------------
        private void BtnIncreaseTimer_Click(object sender, RoutedEventArgs e) { if (int.TryParse(txtTimer.Text, out int c)) txtTimer.Text = (c + 1).ToString(); }
        private void BtnDecreaseTimer_Click(object sender, RoutedEventArgs e) { if (int.TryParse(txtTimer.Text, out int c) && c > 1) txtTimer.Text = (c - 1).ToString(); }
        private void TxtTimer_TextChanged(object sender, TextChangedEventArgs e) { if (timer != null && int.TryParse(txtTimer.Text, out int m) && m > 0) { timer.Interval = TimeSpan.FromMinutes(m); SaveSettings(m); } }
        private void SaveSettings(int minutes) { try { File.WriteAllText(settingsFilePath, minutes.ToString()); } catch { } }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(settingsFilePath) && int.TryParse(File.ReadAllText(settingsFilePath), out int m) && m > 0)
                {
                    txtTimer.Text = m.ToString();
                    timer.Interval = TimeSpan.FromMinutes(m);
                    return;
                }
            }
            catch { }
            timer.Interval = TimeSpan.FromMinutes(10);
        }

        // -------------------------------------------------------------
        // التشغيل مع النظام + التراي
        // -------------------------------------------------------------
        private void chkStartup_Checked(object sender, RoutedEventArgs e) => SetStartup(true);
        private void chkStartup_Unchecked(object sender, RoutedEventArgs e) => SetStartup(false);

        private void SetStartup(bool enable)
        {
            try
            {
                using var rk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                if (enable) rk?.SetValue(AppName, "\"" + Environment.ProcessPath + "\" -startup");
                else rk?.DeleteValue(AppName, false);
            }
            catch { }
        }

        private void CheckStartupState()
        {
            try
            {
                using var rk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
                if (rk?.GetValue(AppName) != null) chkStartup.IsChecked = true;
            }
            catch { }
        }

        private void ShowFromTray()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.ShowInTaskbar = true;
        }

        private void SetupTrayIcon()
        {
            try
            {
                System.Drawing.Icon customIcon = null;
                if (!string.IsNullOrEmpty(Environment.ProcessPath)) customIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);

                notifyIcon = new WinForms.NotifyIcon { Icon = customIcon ?? System.Drawing.SystemIcons.Application, Visible = true, Text = "Wallpaper Changer" };
                notifyIcon.DoubleClick += (s, args) => ShowFromTray();

                var contextMenu = new WinForms.ContextMenuStrip();
                contextMenu.Items.Add("فتح البرنامج", null, (s, args) => ShowFromTray());
                contextMenu.Items.Add("إغلاق البرنامج نهائياً", null, (s, args) => { notifyIcon.Visible = false; notifyIcon.Dispose(); System.Windows.Application.Current.Shutdown(); });
                notifyIcon.ContextMenuStrip = contextMenu;
            }
            catch { }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            StopPreview();
            if (SettingsPopup != null) SettingsPopup.IsOpen = false;
            e.Cancel = true;
            this.Hide();
            this.ShowInTaskbar = false;
            base.OnClosing(e);
        }
    }

    public class WallpaperItem : INotifyPropertyChanged
    {
        private string _index, _filePath, _fileName, _fileSize;
        private bool _isSelected = true;
        private ImageSource _thumbnail;
        private readonly MainWindow _owner;

        public WallpaperItem(MainWindow owner = null)
        {
            _owner = owner;
        }

        // بدل ما نسأل "هل الملف ده في قايمة الفيديوهات؟" كل مرة
        public bool IsVideo { get; set; }

        public string Index { get => _index; set { _index = value; OnPropertyChanged(nameof(Index)); } }
        public string FilePath { get => _filePath; set { _filePath = value; OnPropertyChanged(nameof(FilePath)); } }

        public string FileName
        {
            get => _fileName;
            set
            {
                _fileName = value;
                OnPropertyChanged(nameof(FileName));
                OnPropertyChanged(nameof(FileNameOnly));
                OnPropertyChanged(nameof(FileExtension));
            }
        }

        public string FileNameOnly
        {
            get => Path.GetFileNameWithoutExtension(_fileName ?? "");
            set
            {
                value = value?.Trim();
                if (string.IsNullOrWhiteSpace(value) || value == FileNameOnly)
                {
                    OnPropertyChanged(nameof(FileNameOnly));
                    return;
                }

                if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    System.Windows.MessageBox.Show("الاسم فيه رموز غير مسموحة في أسماء الملفات.", "خطأ", MessageBoxButton.OK, MessageBoxImage.Warning);
                    OnPropertyChanged(nameof(FileNameOnly));
                    return;
                }

                string newFileName = value + FileExtension;
                string newFilePath = Path.Combine(Path.GetDirectoryName(_filePath) ?? "", newFileName);

                try
                {
                    if (File.Exists(_filePath) && !File.Exists(newFilePath))
                    {
                        File.Move(_filePath, newFilePath);
                        _filePath = newFilePath;
                        _fileName = newFileName;

                        OnPropertyChanged(nameof(FilePath));
                        OnPropertyChanged(nameof(FileName));
                        OnPropertyChanged(nameof(FileNameOnly));

                        _owner?.SaveAllData();
                    }
                    else OnPropertyChanged(nameof(FileNameOnly));
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show("فشل إعادة تسمية الملف: " + ex.Message, "خطأ", MessageBoxButton.OK, MessageBoxImage.Error);
                    OnPropertyChanged(nameof(FileNameOnly));
                }
            }
        }

        public string FileExtension => Path.GetExtension(_fileName ?? "");
        public string FileSize { get => _fileSize; set { _fileSize = value; OnPropertyChanged(nameof(FileSize)); } }
        public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } }
        public ImageSource Thumbnail { get => _thumbnail; set { _thumbnail = value; OnPropertyChanged(nameof(Thumbnail)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
