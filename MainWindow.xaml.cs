using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WiredViewer
{
    public class Node : INotifyPropertyChanged
    {
        public string Path { get; set; }
        public string Label { get; set; }
        ImageSource _t;
        public ImageSource Thumb { get => _t; set { _t = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb))); } }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public partial class MainWindow : Window
    {
        static readonly string[] Ext = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico" };
        static readonly string[] Idle = {
            "signal stable. nobody is watching.", "packet loss 0.00%", "the archive remembers what you forget",
            "you are connected", "layer 07 idle", "present day. present time.", "carrier tone detected", "no new messages" };

        readonly ObservableCollection<Node> nodes = new ObservableCollection<Node>();
        readonly RotateTransform rt = new RotateTransform();
        readonly ScaleTransform sc = new ScaleTransform();
        readonly TranslateTransform tr = new TranslateTransform();
        readonly Dictionary<string, TextBlock> meta = new Dictionary<string, TextBlock>();
        readonly List<string> log = new List<string>();
        readonly Random rnd = new Random();
        BitmapSource bmp;
        ImageBrush grain;
        int cur = -1, rot, idleTicks;
        double scale = 1;
        Point pan, dragFrom, panFrom;
        bool fit = true, drag, full, uiOn = true;
        WindowState prevState;

        Brush R(string k) => (Brush)FindResource(k);
        double VW => ViewPort.ActualWidth;
        double VH => ViewPort.ActualHeight;

        public MainWindow()
        {
            InitializeComponent();
            var g = new TransformGroup();
            g.Children.Add(rt); g.Children.Add(sc); g.Children.Add(tr);
            Img.RenderTransform = g;
            Thumbs.ItemsSource = nodes;

            foreach (var k in new[] { "file", "node", "dimensions", "weight", "modified", "format", "density", "checksum", "location", "rotation" })
            {
                Meta.Children.Add(new TextBlock { Text = k, Foreground = R("Dim"), FontSize = 9, Margin = new Thickness(0, 11, 0, 1) });
                var v = new TextBlock { Text = "—", Foreground = R("Off"), FontSize = 11, TextWrapping = TextWrapping.Wrap };
                Meta.Children.Add(v); meta[k] = v;
            }

            // film grain: a noise tile that jitters ~10fps
            var wb = new WriteableBitmap(128, 128, 96, 96, PixelFormats.Bgra32, null);
            var px = new byte[128 * 128 * 4];
            for (int i = 0; i < px.Length; i += 4) { byte v = (byte)rnd.Next(256); px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = (byte)rnd.Next(60, 255); }
            wb.WritePixels(new Int32Rect(0, 0, 128, 128), px, 128 * 4, 0);
            grain = new ImageBrush(wb) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 128, 128), Stretch = Stretch.Fill };
            Grain.Fill = grain;
            var gt = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            gt.Tick += (s, e) => grain.Viewport = new Rect(rnd.Next(128), rnd.Next(128), 128, 128);
            gt.Start();

            var ct = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            ct.Tick += (s, e) =>
            {
                Clock.Text = DateTime.Now.ToString("yyyy.MM.dd  HH:mm:ss");
                if (++idleTicks % 14 == 0) StatusMsg.Text = Idle[rnd.Next(Idle.Length)];
            };
            ct.Start(); Clock.Text = DateTime.Now.ToString("yyyy.MM.dd  HH:mm:ss");

            Log("session opened");
            Loaded += (s, e) =>
            {
                var a = Environment.GetCommandLineArgs();
                if (a.Length > 1) OpenPath(a[1]);
            };
        }

        // ---------- loading ----------
        void OpenPath(string p)
        {
            if (Directory.Exists(p)) LoadFolder(p, null);
            else if (File.Exists(p)) LoadFolder(System.IO.Path.GetDirectoryName(p), p);
        }

        void LoadFolder(string dir, string select)
        {
            var files = Directory.EnumerateFiles(dir)
                .Where(f => Ext.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => System.IO.Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToList();
            nodes.Clear(); cur = -1;
            for (int i = 0; i < files.Count; i++) nodes.Add(new Node { Path = files[i], Label = (i + 1).ToString("000") });
            PathText.Text = dir;
            IndexCount.Text = files.Count + (files.Count == 1 ? " node" : " nodes");
            Log($"mounted {files.Count} nodes");
            if (files.Count == 0) { Empty.Visibility = Visibility.Visible; Img.Source = null; return; }

            var list = nodes.ToList();
            Task.Run(() =>
            {
                foreach (var n in list)
                {
                    try
                    {
                        var b = new BitmapImage();
                        b.BeginInit(); b.UriSource = new Uri(n.Path); b.DecodePixelWidth = 180;
                        b.CacheOption = BitmapCacheOption.OnLoad; b.EndInit(); b.Freeze();
                        Dispatcher.BeginInvoke((Action)(() => n.Thumb = b));
                    }
                    catch { }
                }
            });
            Show(select == null ? 0 : Math.Max(0, files.IndexOf(select)));
        }

        void Show(int i)
        {
            if (nodes.Count == 0) return;
            i = (i % nodes.Count + nodes.Count) % nodes.Count;
            var n = nodes[i];
            BitmapImage b;
            try
            {
                b = new BitmapImage();
                b.BeginInit(); b.UriSource = new Uri(n.Path); b.CacheOption = BitmapCacheOption.OnLoad; b.EndInit(); b.Freeze();
            }
            catch { Log("decode failed: " + System.IO.Path.GetFileName(n.Path)); return; }

            cur = i; bmp = b; rot = 0; pan = new Point();
            Thumbs.SelectedIndex = i; Thumbs.ScrollIntoView(n);
            Img.Source = b; Img.Width = b.PixelWidth; Img.Height = b.PixelHeight;
            Empty.Visibility = Visibility.Collapsed;
            FitView();
            Img.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

            var fi = new FileInfo(n.Path);
            uint h = Fnv(fi.Name + fi.Length);
            double lat = (h % 18000) / 100.0 - 90, lon = ((h >> 8) % 36000) / 100.0 - 180;
            meta["file"].Text = fi.Name;
            meta["node"].Text = $"{i + 1:000} / {nodes.Count:000}";
            meta["dimensions"].Text = $"{b.PixelWidth} × {b.PixelHeight}";
            meta["weight"].Text = Human(fi.Length);
            meta["modified"].Text = fi.LastWriteTime.ToString("yyyy.MM.dd HH:mm");
            meta["format"].Text = fi.Extension.TrimStart('.').ToLowerInvariant();
            meta["density"].Text = $"{b.DpiX:0} dpi";
            meta["checksum"].Text = h.ToString("x8");
            meta["location"].Text = $"{Math.Abs(lat):00.00}°{(lat < 0 ? "S" : "N")}  {Math.Abs(lon):000.00}°{(lon < 0 ? "W" : "E")}";
            Log($"node {i + 1:000} loaded");
        }

        // ---------- view math ----------
        void Place()
        {
            if (bmp == null) return;
            Canvas.SetLeft(Img, (VW - bmp.PixelWidth) / 2);
            Canvas.SetTop(Img, (VH - bmp.PixelHeight) / 2);
        }

        void FitView()
        {
            if (bmp == null) return;
            double w = bmp.PixelWidth, h = bmp.PixelHeight;
            if (rot % 180 != 0) { var t = w; w = h; h = t; }
            double padX = uiOn ? 110 : 40, padY = uiOn ? 110 : 40;
            scale = Math.Min(3, Math.Min(Math.Max(50, VW - padX) / w, Math.Max(50, VH - padY) / h));
            pan = new Point(); fit = true; Place(); Apply();
        }

        void Apply()
        {
            sc.ScaleX = sc.ScaleY = scale; tr.X = pan.X; tr.Y = pan.Y; rt.Angle = rot;
            RenderOptions.SetBitmapScalingMode(Img, scale >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
            ZoomText.Text = bmp == null ? "" : $"{scale * 100:0}%   rot {rot:000}°";
            if (meta.ContainsKey("rotation")) meta["rotation"].Text = $"{rot:000}°";
        }

        void ZoomAt(double factor, Point p)
        {
            double ns = Math.Max(0.02, Math.Min(64, scale * factor)), k = ns / scale;
            pan = new Point(p.X - (p.X - pan.X) * k, p.Y - (p.Y - pan.Y) * k);
            scale = ns; fit = false; Apply();
        }

        Point Center => new Point(VW / 2, VH / 2);
        Point Rel(MouseEventArgs e) { var m = e.GetPosition(ViewPort); return new Point(m.X - VW / 2, m.Y - VH / 2); }

        void Rotate(int d)
        {
            if (bmp == null) return;
            rot = ((rot + d) % 360 + 360) % 360;
            if (fit) FitView(); else Apply();
            Log($"rotate {rot:000}°");
        }

        // ---------- mouse ----------
        void OnWheel(object s, MouseWheelEventArgs e) { if (bmp != null) ZoomAt(e.Delta > 0 ? 1.15 : 1 / 1.15, Rel(e)); }
        void OnDown(object s, MouseButtonEventArgs e)
        {
            if (bmp == null) return;
            if (e.ClickCount == 2) { if (fit) { ZoomAt(1 / scale, Rel(e)); } else FitView(); return; }
            drag = true; dragFrom = e.GetPosition(ViewPort); panFrom = pan; ViewPort.CaptureMouse();
        }
        void OnUp(object s, MouseButtonEventArgs e) { drag = false; ViewPort.ReleaseMouseCapture(); }
        void OnLeave(object s, MouseEventArgs e) { Reticle.Visibility = Visibility.Collapsed; }
        void OnMove(object s, MouseEventArgs e)
        {
            var m = e.GetPosition(ViewPort);
            Reticle.Visibility = Visibility.Visible; RetT.X = m.X; RetT.Y = m.Y;
            if (drag) { pan = new Point(panFrom.X + m.X - dragFrom.X, panFrom.Y + m.Y - dragFrom.Y); fit = false; Apply(); }
            if (bmp == null) return;
            double dx = m.X - VW / 2 - pan.X, dy = m.Y - VH / 2 - pan.Y, a = -rot * Math.PI / 180;
            double ux = dx * Math.Cos(a) - dy * Math.Sin(a), uy = dx * Math.Sin(a) + dy * Math.Cos(a);
            double ix = ux / scale + bmp.PixelWidth / 2.0, iy = uy / scale + bmp.PixelHeight / 2.0;
            bool inside = ix >= 0 && iy >= 0 && ix < bmp.PixelWidth && iy < bmp.PixelHeight;
            CoordText.Text = inside ? $"x {(int)ix:0000}  y {(int)iy:0000}" : "x ----  y ----";
        }
        void OnViewSize(object s, SizeChangedEventArgs e) { if (bmp == null) return; if (fit) FitView(); else { Place(); Apply(); } }

        // ---------- buttons ----------
        void OnPrev(object s, RoutedEventArgs e) => Show(cur - 1);
        void OnNext(object s, RoutedEventArgs e) => Show(cur + 1);
        void OnPick(object s, SelectionChangedEventArgs e) { if (Thumbs.SelectedIndex >= 0 && Thumbs.SelectedIndex != cur) Show(Thumbs.SelectedIndex); }
        void OnZoomIn(object s, RoutedEventArgs e) { if (bmp != null) ZoomAt(1.25, new Point()); }
        void OnZoomOut(object s, RoutedEventArgs e) { if (bmp != null) ZoomAt(0.8, new Point()); }
        void OnFit(object s, RoutedEventArgs e) { FitView(); Log("fit to frame"); }
        void OnActual(object s, RoutedEventArgs e) { if (bmp != null) { pan = new Point(); scale = 1; fit = false; Apply(); Log("scale 1:1"); } }
        void OnRotL(object s, RoutedEventArgs e) => Rotate(-90);
        void OnRotR(object s, RoutedEventArgs e) => Rotate(90);
        void OnFull(object s, RoutedEventArgs e) => ToggleFull();
        void OnOpen(object s, RoutedEventArgs e)
        {
            var d = new OpenFolderDialog { Title = "mount an archive" };
            if (d.ShowDialog() == true) LoadFolder(d.FolderName, null);
        }
        void OnExport(object s, RoutedEventArgs e)
        {
            if (cur < 0) return;
            var src = nodes[cur].Path;
            var d = new SaveFileDialog { FileName = System.IO.Path.GetFileName(src), Title = "export a copy" };
            if (d.ShowDialog() == true) { File.Copy(src, d.FileName, true); Log("copy exported"); }
        }

        // ---------- window ----------
        void OnTitleDown(object s, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) OnMax(null, null);
            else if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }
        void OnMin(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        void OnMax(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        void OnClose(object s, RoutedEventArgs e) => Close();
        void OnState(object s, EventArgs e) => Root.Margin = WindowState == WindowState.Maximized && !full ? new Thickness(8) : new Thickness(0);

        void SetUi(bool on)
        {
            uiOn = on;
            var v = on ? Visibility.Visible : Visibility.Collapsed;
            TopBar.Visibility = StatusBar.Visibility = LeftPanel.Visibility = RightPanel.Visibility = v;
        }

        void ToggleFull()
        {
            full = !full;
            var chrome = WindowChrome.GetWindowChrome(this);
            if (full)
            {
                prevState = WindowState; ResizeMode = ResizeMode.NoResize;
                chrome.ResizeBorderThickness = new Thickness(0);
                WindowState = WindowState.Normal; WindowState = WindowState.Maximized;
                Root.Margin = new Thickness(0); SetUi(false); Log("fullscreen on");
            }
            else
            {
                ResizeMode = ResizeMode.CanResize; chrome.ResizeBorderThickness = new Thickness(6);
                WindowState = prevState; SetUi(true); OnState(null, null); Log("fullscreen off");
            }
        }

        void OnDrop(object s, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0) OpenPath(f[0]);
        }

        void OnKey(object s, KeyEventArgs e)
        {
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            switch (e.Key)
            {
                case Key.Left: case Key.PageUp: Show(cur - 1); break;
                case Key.Right: case Key.PageDown: case Key.Space: Show(cur + 1); break;
                case Key.Home: Show(0); break;
                case Key.End: Show(nodes.Count - 1); break;
                case Key.Add: case Key.OemPlus: OnZoomIn(null, null); break;
                case Key.Subtract: case Key.OemMinus: OnZoomOut(null, null); break;
                case Key.R: Rotate(shift ? -90 : 90); break;
                case Key.F: OnFit(null, null); break;
                case Key.D1: case Key.NumPad1: OnActual(null, null); break;
                case Key.O: OnOpen(null, null); break;
                case Key.S: OnExport(null, null); break;
                case Key.F11: ToggleFull(); break;
                case Key.Escape: if (full) ToggleFull(); break;
                case Key.Tab: SetUi(!uiOn); if (fit) FitView(); break;
                default: return;
            }
            e.Handled = true;
        }

        // ---------- helpers ----------
        void Log(string m)
        {
            log.Add($"[{DateTime.Now:HH:mm:ss}] {m}");
            if (log.Count > 7) log.RemoveAt(0);
            LogText.Text = string.Join("\n", log);
            StatusMsg.Text = m; idleTicks = 0;
        }

        static uint Fnv(string s) { uint h = 2166136261; foreach (var c in s) { h ^= c; h *= 16777619; } return h; }

        static string Human(long b)
        {
            string[] u = { "b", "kb", "mb", "gb" }; double v = b; int i = 0;
            while (v >= 1024 && i < 3) { v /= 1024; i++; }
            return $"{v:0.##} {u[i]}";
        }
    }
}
