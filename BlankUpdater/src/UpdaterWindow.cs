using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace BlankUpdater
{
    internal enum UpdaterMode
    {
        Checking,
        Downloading,
        Done,
        Error,
    }

    /// <summary>
    /// Animated progress window shown while the game starts. Everything is drawn by hand with GDI+
    /// (no WinForms in Unity): rounded window with shadow, fade in/out, animated status icon and a
    /// custom loading bar with a shine effect. It runs on its own thread with its own message loop,
    /// so the main thread only sets values. If anything fails the window stays hidden and the
    /// update check runs anyway.
    /// </summary>
    internal sealed class UpdaterWindow
    {
        private const int Width = 480;
        private const int Height = 164;
        private const int Radius = 16;

        // ARGB colors
        private const uint BackgroundTop = 0xFF1F2029, BackgroundBottom = 0xFF15161C, Border = 0x2EFFFFFF;
        private const uint TextColor = 0xFFF2F2F5, DetailColor = 0xFF8E90A0, Track = 0xFF2A2C36, RingTrack = 0x24FFFFFF;
        private const uint AccentA = 0xFF3DA5F4, AccentB = 0xFF8A5CF6;
        private const uint GreenA = 0xFF3DD68C, GreenB = 0xFF2BC4C4;
        private const uint AmberA = 0xFFF5A524, AmberB = 0xFFF04F5F;

        private static WndProc _wndProc; // kept alive for Windows
        private static bool _classRegistered;

        private readonly object _lock = new object();
        private readonly ManualResetEvent _ready = new ManualResetEvent(false);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Thread _thread;
        private IntPtr _window;
        private IntPtr _gdiplusToken;
        private IntPtr _fontTitle, _fontText, _fontSmall, _fontFamily, _formatLeft, _formatRight;

        private string _title = "BlankUpdater", _status = "", _detail = "", _footer = "";
        private volatile int _mode = (int)UpdaterMode.Checking;
        private volatile float _progress;      // overall 0..1
        private volatile float _fileProgress;  // current file 0..1
        private volatile bool _closing;
        private float _shownProgress, _shownFileProgress;
        private double _modeStart, _lastTick;
        private int _alpha;

        public static UpdaterWindow TryOpen(string footer)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                return null;
            try
            {
                var window = new UpdaterWindow { _footer = footer ?? "" };
                window._thread = new Thread(window.ThreadMain) { IsBackground = true, Name = "BlankUpdater window" };
                window._thread.Start();
                if (window._ready.WaitOne(3000) && window._window != IntPtr.Zero)
                    return window;
                window.Close(0);
            }
            catch
            {
                // no window, updates still run
            }
            return null;
        }

        public void SetStatus(string text) { lock (_lock) _status = text ?? ""; }
        public void SetDetail(string text) { lock (_lock) _detail = text ?? ""; }

        /// <summary>Overall progress from 0 to 1000.</summary>
        public void SetProgress(int permille) => _progress = Math.Max(0, Math.Min(1000, permille)) / 1000f;

        /// <summary>Progress of the file that is downloading right now, 0 to 1.</summary>
        public void SetFileProgress(float value) => _fileProgress = Math.Max(0f, Math.Min(1f, value));

        public void SetMode(UpdaterMode mode)
        {
            if (_mode == (int)mode)
                return;
            _modeStart = _clock.Elapsed.TotalSeconds;
            if (mode == UpdaterMode.Downloading)
                _shownFileProgress = 0;
            _mode = (int)mode;
        }

        public void Close(int delayMs)
        {
            if (delayMs > 0)
                Thread.Sleep(delayMs);
            _closing = true;
            _thread?.Join(2000);
        }

        // ---------------------------------------------------------------- window thread

        private void ThreadMain()
        {
            try
            {
                var input = new GdiplusStartupInput { GdiplusVersion = 1 };
                if (GdiplusStartup(out _gdiplusToken, ref input, IntPtr.Zero) != 0)
                    return;
                CreateFonts();

                IntPtr instance = GetModuleHandleW(null);
                if (!_classRegistered)
                {
                    _wndProc = WindowProc;
                    var windowClass = new WNDCLASSEX
                    {
                        cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX)),
                        style = CS_DROPSHADOW,
                        lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                        hInstance = instance,
                        hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)IDC_ARROW),
                        lpszClassName = "BlankUpdaterWindow",
                    };
                    if (RegisterClassExW(ref windowClass) == 0)
                        return;
                    _classRegistered = true;
                }

                int x = (GetSystemMetrics(SM_CXSCREEN) - Width) / 2;
                int y = (GetSystemMetrics(SM_CYSCREEN) - Height) / 2;
                _window = CreateWindowExW(WS_EX_TOPMOST | WS_EX_APPWINDOW | WS_EX_LAYERED, "BlankUpdaterWindow", "BlankUpdater",
                    WS_POPUP, x, y, Width, Height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                if (_window == IntPtr.Zero)
                    return;

                SetWindowRgn(_window, CreateRoundRectRgn(0, 0, Width + 1, Height + 1, Radius * 2, Radius * 2), true);
                SetLayeredWindowAttributes(_window, 0, 0, LWA_ALPHA);
                ShowWindow(_window, SW_SHOWNOACTIVATE);
                SetTimer(_window, (IntPtr)1, 16, IntPtr.Zero);
                _lastTick = _clock.Elapsed.TotalSeconds;
                _ready.Set();

                while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
            }
            catch
            {
                // window is optional
            }
            finally
            {
                _window = IntPtr.Zero;
                _ready.Set();
                DeleteFonts();
                if (_gdiplusToken != IntPtr.Zero)
                    GdiplusShutdown(_gdiplusToken);
            }
        }

        private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                switch (msg)
                {
                    case WM_TIMER:
                        Tick(hWnd);
                        return IntPtr.Zero;
                    case WM_ERASEBKGND:
                        return (IntPtr)1;
                    case WM_PAINT:
                        IntPtr hdc = BeginPaint(hWnd, out PAINTSTRUCT ps);
                        Paint(hdc);
                        EndPaint(hWnd, ref ps);
                        return IntPtr.Zero;
                    case WM_DESTROY:
                        KillTimer(hWnd, (IntPtr)1);
                        PostQuitMessage(0);
                        return IntPtr.Zero;
                }
            }
            catch
            {
                // never let an exception escape into Windows
            }
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        private void Tick(IntPtr hWnd)
        {
            double now = _clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(0.1, now - _lastTick);
            _lastTick = now;

            // ease the shown values towards the real ones so the bar glides instead of jumping
            float ease = Math.Min(1f, dt * 8f);
            _shownProgress += (_progress - _shownProgress) * ease;
            _shownFileProgress += (_fileProgress - _shownFileProgress) * ease;

            int target = _closing ? 0 : 255;
            int step = _closing ? 34 : 22;
            if (_alpha != target)
            {
                _alpha = _alpha < target ? Math.Min(target, _alpha + step) : Math.Max(target, _alpha - step);
                SetLayeredWindowAttributes(hWnd, 0, (byte)_alpha, LWA_ALPHA);
                if (_closing && _alpha == 0)
                {
                    DestroyWindow(hWnd);
                    return;
                }
            }
            InvalidateRect(hWnd, IntPtr.Zero, false);
        }

        // ---------------------------------------------------------------- drawing

        private void Paint(IntPtr hdc)
        {
            IntPtr memDc = CreateCompatibleDC(hdc);
            IntPtr bitmap = CreateCompatibleBitmap(hdc, Width, Height);
            IntPtr oldBitmap = SelectObject(memDc, bitmap);
            IntPtr g = IntPtr.Zero;
            try
            {
                if (GdipCreateFromHDC(memDc, out g) != 0)
                    return;
                GdipSetSmoothingMode(g, SmoothingModeAntiAlias);
                GdipSetPixelOffsetMode(g, PixelOffsetModeHighQuality);
                GdipSetTextRenderingHint(g, TextRenderingHintClearTypeGridFit);

                double t = _clock.Elapsed.TotalSeconds;
                var mode = (UpdaterMode)_mode;

                // background with a soft vertical gradient and a thin border
                FillGradientRect(g, 0, 0, Width, Height, 0, 0, 0, Height, BackgroundTop, BackgroundBottom);
                using (var border = Path.RoundedRect(0.5f, 0.5f, Width - 1, Height - 1, Radius))
                    DrawPath(g, border.Handle, Border, 1f);

                DrawIcon(g, 56, 58, mode, t);

                string title, status, detail;
                lock (_lock)
                {
                    title = _title;
                    status = _status;
                    detail = _detail;
                }
                DrawText(g, title, _fontTitle, TextColor, 108, 24, Width - 132, 28, _formatLeft);
                DrawText(g, status, _fontText, TextColor, 108, 54, Width - 132, 22, _formatLeft);
                DrawText(g, detail, _fontSmall, DetailColor, 108, 77, Width - 132, 20, _formatLeft);

                DrawBar(g, 24, 114, Width - 48, 10, mode, t);

                DrawText(g, _footer, _fontSmall, DetailColor, 24, 132, Width - 120, 20, _formatLeft);
                if (mode != UpdaterMode.Checking)
                    DrawText(g, $"{(int)Math.Round(_shownProgress * 100)}%", _fontSmall, DetailColor, Width - 96, 132, 72, 20, _formatRight);

                BitBlt(hdc, 0, 0, Width, Height, memDc, 0, 0, SRCCOPY);
            }
            finally
            {
                if (g != IntPtr.Zero)
                    GdipDeleteGraphics(g);
                SelectObject(memDc, oldBitmap);
                DeleteObject(bitmap);
                DeleteDC(memDc);
            }
        }

        private void DrawIcon(IntPtr g, float cx, float cy, UpdaterMode mode, double t)
        {
            const float r = 30f;
            double sinceMode = t - _modeStart;

            uint colorA = AccentA, colorB = AccentB;
            if (mode == UpdaterMode.Done) { colorA = GreenA; colorB = GreenB; }
            else if (mode == UpdaterMode.Error) { colorA = AmberA; colorB = AmberB; }

            // soft glowing disc behind the icon, gently pulsing
            float pulse = 1f + 0.035f * (float)Math.Sin(t * 2.4);
            float discR = r * pulse;
            FillGradientEllipse(g, cx - discR, cy - discR, discR * 2, discR * 2, WithAlpha(colorA, 0x30), WithAlpha(colorB, 0x30));

            // ring track
            DrawArc(g, cx - r + 2, cy - r + 2, (r - 2) * 2, (r - 2) * 2, 0, 360, RingTrack, 4f);

            float ring = (r - 2) * 2;
            switch (mode)
            {
                case UpdaterMode.Checking:
                {
                    // spinning arc that breathes in length
                    float start = (float)(t * 320 % 360);
                    float sweep = 90f + 50f * (float)Math.Sin(t * 3.1);
                    DrawGradientArc(g, cx - r + 2, cy - r + 2, ring, ring, start, sweep, colorA, colorB, 4f);

                    // magnifying glass that hovers
                    float bob = 2f * (float)Math.Sin(t * 4);
                    DrawArc(g, cx - 12, cy - 12 + bob, 17, 17, 0, 360, TextColor, 3f);
                    DrawLine(g, cx + 3.5f, cy + 3.5f + bob, cx + 11, cy + 11 + bob, TextColor, 3.5f);
                    break;
                }
                case UpdaterMode.Downloading:
                {
                    // ring fills with the current file, arrow bounces down into the tray
                    float sweep = Math.Max(4f, 360f * _shownFileProgress);
                    DrawGradientArc(g, cx - r + 2, cy - r + 2, ring, ring, -90, sweep, colorA, colorB, 4f);

                    float bounce = 3f * (float)Math.Abs(Math.Sin(t * 4.5));
                    DrawLine(g, cx, cy - 13 + bounce, cx, cy + 5 + bounce, TextColor, 3.5f);
                    DrawPolyline(g, TextColor, 3.5f, cx - 7.5f, cy - 2 + bounce, cx, cy + 5.5f + bounce, cx + 7.5f, cy - 2 + bounce);
                    DrawLine(g, cx - 11, cy + 13, cx + 11, cy + 13, WithAlpha(TextColor, 0xB0), 3f);
                    break;
                }
                case UpdaterMode.Done:
                {
                    // ring closes, then the check mark draws itself with a little pop
                    float k = Clamp01((float)(sinceMode / 0.35));
                    DrawGradientArc(g, cx - r + 2, cy - r + 2, ring, ring, -90, 360 * EaseOut(k), colorA, colorB, 4f);

                    float c = Clamp01((float)((sinceMode - 0.15) / 0.35));
                    float pop = 1f + 0.12f * (float)Math.Sin(Math.PI * Clamp01((float)((sinceMode - 0.45) / 0.3)));
                    DrawCheck(g, cx, cy, EaseOut(c), pop, TextColor);
                    break;
                }
                case UpdaterMode.Error:
                {
                    DrawGradientArc(g, cx - r + 2, cy - r + 2, ring, ring, -90, 360, colorA, colorB, 4f);
                    float shake = sinceMode < 0.5 ? 3f * (float)Math.Sin(sinceMode * 40) * (float)(1 - sinceMode * 2) : 0f;
                    DrawLine(g, cx + shake, cy - 13, cx + shake, cy + 3, TextColor, 4f);
                    FillEllipse(g, cx - 2.75f + shake, cy + 8, 5.5f, 5.5f, TextColor);
                    break;
                }
            }
        }

        private void DrawCheck(IntPtr g, float cx, float cy, float k, float scale, uint color)
        {
            float ax = cx - 11 * scale, ay = cy + 1 * scale;
            float bx = cx - 3 * scale, by = cy + 9 * scale;
            float qx = cx + 12 * scale, qy = cy - 9 * scale;
            float first = (float)Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            float second = (float)Math.Sqrt((qx - bx) * (qx - bx) + (qy - by) * (qy - by));
            float length = (first + second) * k;
            if (length <= 0.01f)
                return;
            if (length <= first)
            {
                float f = length / first;
                DrawLine(g, ax, ay, ax + (bx - ax) * f, ay + (by - ay) * f, color, 4f);
                return;
            }
            float s = (length - first) / second;
            DrawPolyline(g, color, 4f, ax, ay, bx, by, bx + (qx - bx) * s, by + (qy - by) * s);
        }

        private void DrawBar(IntPtr g, float x, float y, float w, float h, UpdaterMode mode, double t)
        {
            float radius = h / 2;
            using (var track = Path.RoundedRect(x, y, w, h, radius))
            {
                FillPath(g, track.Handle, Track);

                if (mode == UpdaterMode.Checking)
                {
                    // indeterminate: a glowing segment slides through the track
                    float segment = w * 0.32f;
                    float pos = x + (float)((t * 0.75 % 1.35) - 0.32) * w;
                    GdipSetClipPath(g, track.Handle, CombineModeReplace);
                    FillGradientRect(g, pos, y, segment / 2, h, pos, 0, pos + segment / 2, 0, WithAlpha(AccentA, 0), AccentA);
                    FillGradientRect(g, pos + segment / 2, y, segment / 2, h, pos + segment / 2, 0, pos + segment, 0, AccentB, WithAlpha(AccentB, 0));
                    GdipResetClip(g);
                    return;
                }
            }

            uint colorA = AccentA, colorB = AccentB;
            if (mode == UpdaterMode.Done) { colorA = GreenA; colorB = GreenB; }
            else if (mode == UpdaterMode.Error) { colorA = AmberA; colorB = AmberB; }

            float fill = Math.Max(h, w * Clamp01(_shownProgress));
            using (var bar = Path.RoundedRect(x, y, fill, h, radius))
            {
                // the gradient spans the whole track, so the colour shifts as the bar grows
                IntPtr brush = CreateLineBrush(x, 0, x + w, 0, colorA, colorB);
                GdipFillPath(g, brush, bar.Handle);
                GdipDeleteBrush(brush);

                // shine sweeping across the filled part
                if (mode == UpdaterMode.Downloading || (mode == UpdaterMode.Done && t - _modeStart < 1.0))
                {
                    float band = 46f;
                    float sx = x - band + (float)(t * 0.9 % 1.4) / 1.4f * (fill + band * 2);
                    GdipSetClipPath(g, bar.Handle, CombineModeReplace);
                    FillGradientRect(g, sx, y, band / 2, h, sx, 0, sx + band / 2, 0, 0x00FFFFFF, 0x66FFFFFF);
                    FillGradientRect(g, sx + band / 2, y, band / 2, h, sx + band / 2, 0, sx + band, 0, 0x66FFFFFF, 0x00FFFFFF);
                    GdipResetClip(g);
                }

                // glowing tip at the end of the bar
                if (mode == UpdaterMode.Downloading && fill > h)
                    FillEllipse(g, x + fill - h * 1.1f, y - h * 0.1f, h * 1.2f, h * 1.2f, WithAlpha(0xFFFFFFFF, (byte)(0x40 + 0x30 * Math.Sin(t * 6))));
            }
        }

        // ---------------------------------------------------------------- GDI+ helpers

        private void CreateFonts()
        {
            foreach (string name in new[] { "Segoe UI", "Arial" })
            {
                if (GdipCreateFontFamilyFromName(name, IntPtr.Zero, out _fontFamily) == 0)
                    break;
                _fontFamily = IntPtr.Zero;
            }
            if (_fontFamily == IntPtr.Zero)
                GdipGetGenericFontFamilySansSerif(out _fontFamily);

            GdipCreateFont(_fontFamily, 19f, FontStyleBold, UnitPixel, out _fontTitle);
            GdipCreateFont(_fontFamily, 14.5f, FontStyleRegular, UnitPixel, out _fontText);
            GdipCreateFont(_fontFamily, 12.5f, FontStyleRegular, UnitPixel, out _fontSmall);

            GdipCreateStringFormat(StringFormatFlagsNoWrap, 0, out _formatLeft);
            GdipSetStringFormatTrimming(_formatLeft, StringTrimmingEllipsisCharacter);
            GdipSetStringFormatLineAlign(_formatLeft, StringAlignmentCenter);

            GdipCreateStringFormat(StringFormatFlagsNoWrap, 0, out _formatRight);
            GdipSetStringFormatAlign(_formatRight, StringAlignmentFar);
            GdipSetStringFormatLineAlign(_formatRight, StringAlignmentCenter);
        }

        private void DeleteFonts()
        {
            if (_fontTitle != IntPtr.Zero) GdipDeleteFont(_fontTitle);
            if (_fontText != IntPtr.Zero) GdipDeleteFont(_fontText);
            if (_fontSmall != IntPtr.Zero) GdipDeleteFont(_fontSmall);
            if (_fontFamily != IntPtr.Zero) GdipDeleteFontFamily(_fontFamily);
            if (_formatLeft != IntPtr.Zero) GdipDeleteStringFormat(_formatLeft);
            if (_formatRight != IntPtr.Zero) GdipDeleteStringFormat(_formatRight);
        }

        private static void DrawText(IntPtr g, string text, IntPtr font, uint color, float x, float y, float w, float h, IntPtr format)
        {
            if (string.IsNullOrEmpty(text) || font == IntPtr.Zero)
                return;
            GdipCreateSolidFill(color, out IntPtr brush);
            var rect = new RectF { X = x, Y = y, Width = w, Height = h };
            GdipDrawString(g, text, -1, font, ref rect, format, brush);
            GdipDeleteBrush(brush);
        }

        private static IntPtr CreateLineBrush(float x1, float y1, float x2, float y2, uint c1, uint c2)
        {
            var p1 = new PointF { X = x1, Y = y1 };
            var p2 = new PointF { X = x2 + (x1 == x2 && y1 == y2 ? 1 : 0), Y = y2 };
            GdipCreateLineBrush(ref p1, ref p2, c1, c2, WrapModeTileFlipXY, out IntPtr brush);
            return brush;
        }

        private static void FillGradientRect(IntPtr g, float x, float y, float w, float h, float gx1, float gy1, float gx2, float gy2, uint c1, uint c2)
        {
            if (w <= 0 || h <= 0)
                return;
            IntPtr brush = CreateLineBrush(gx1, gy1, gx2, gy2, c1, c2);
            GdipFillRectangle(g, brush, x, y, w, h);
            GdipDeleteBrush(brush);
        }

        private static void FillGradientEllipse(IntPtr g, float x, float y, float w, float h, uint c1, uint c2)
        {
            IntPtr brush = CreateLineBrush(x, y, x + w, y + h, c1, c2);
            GdipFillEllipse(g, brush, x, y, w, h);
            GdipDeleteBrush(brush);
        }

        private static void FillEllipse(IntPtr g, float x, float y, float w, float h, uint color)
        {
            GdipCreateSolidFill(color, out IntPtr brush);
            GdipFillEllipse(g, brush, x, y, w, h);
            GdipDeleteBrush(brush);
        }

        private static void FillPath(IntPtr g, IntPtr path, uint color)
        {
            GdipCreateSolidFill(color, out IntPtr brush);
            GdipFillPath(g, brush, path);
            GdipDeleteBrush(brush);
        }

        private static IntPtr CreatePen(uint color, float width)
        {
            GdipCreatePen1(color, width, UnitPixel, out IntPtr pen);
            GdipSetPenStartCap(pen, LineCapRound);
            GdipSetPenEndCap(pen, LineCapRound);
            GdipSetPenLineJoin(pen, LineJoinRound);
            return pen;
        }

        private static void DrawPath(IntPtr g, IntPtr path, uint color, float width)
        {
            IntPtr pen = CreatePen(color, width);
            GdipDrawPath(g, pen, path);
            GdipDeletePen(pen);
        }

        private static void DrawArc(IntPtr g, float x, float y, float w, float h, float start, float sweep, uint color, float width)
        {
            IntPtr pen = CreatePen(color, width);
            GdipDrawArc(g, pen, x, y, w, h, start, sweep);
            GdipDeletePen(pen);
        }

        private static void DrawGradientArc(IntPtr g, float x, float y, float w, float h, float start, float sweep, uint c1, uint c2, float width)
        {
            IntPtr brush = CreateLineBrush(x, y, x + w, y + h, c1, c2);
            GdipCreatePen2(brush, width, UnitPixel, out IntPtr pen);
            GdipSetPenStartCap(pen, LineCapRound);
            GdipSetPenEndCap(pen, LineCapRound);
            GdipDrawArc(g, pen, x, y, w, h, start, Math.Min(359.9f, sweep));
            GdipDeletePen(pen);
            GdipDeleteBrush(brush);
        }

        private static void DrawLine(IntPtr g, float x1, float y1, float x2, float y2, uint color, float width)
        {
            IntPtr pen = CreatePen(color, width);
            GdipDrawLine(g, pen, x1, y1, x2, y2);
            GdipDeletePen(pen);
        }

        private static void DrawPolyline(IntPtr g, uint color, float width, params float[] points)
        {
            using (var path = new Path())
            {
                for (int i = 0; i + 3 < points.Length; i += 2)
                    GdipAddPathLine(path.Handle, points[i], points[i + 1], points[i + 2], points[i + 3]);
                DrawPath(g, path.Handle, color, width);
            }
        }

        private static uint WithAlpha(uint color, byte alpha) => (color & 0x00FFFFFF) | ((uint)alpha << 24);
        private static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
        private static float EaseOut(float v) => 1f - (1f - v) * (1f - v) * (1f - v);

        private sealed class Path : IDisposable
        {
            public readonly IntPtr Handle;

            public Path()
            {
                GdipCreatePath(0, out Handle);
            }

            public static Path RoundedRect(float x, float y, float w, float h, float r)
            {
                var path = new Path();
                float d = Math.Min(r * 2, Math.Min(w, h));
                GdipAddPathArc(path.Handle, x, y, d, d, 180, 90);
                GdipAddPathArc(path.Handle, x + w - d, y, d, d, 270, 90);
                GdipAddPathArc(path.Handle, x + w - d, y + h - d, d, d, 0, 90);
                GdipAddPathArc(path.Handle, x, y + h - d, d, d, 90, 90);
                GdipClosePathFigure(path.Handle);
                return path;
            }

            public void Dispose()
            {
                if (Handle != IntPtr.Zero)
                    GdipDeletePath(Handle);
            }
        }

        // ---------------------------------------------------------------- Windows API

        private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private const uint WS_POPUP = 0x80000000;
        private const uint WS_EX_TOPMOST = 0x00000008, WS_EX_APPWINDOW = 0x00040000, WS_EX_LAYERED = 0x00080000;
        private const uint CS_DROPSHADOW = 0x00020000, LWA_ALPHA = 0x2, SRCCOPY = 0x00CC0020;
        private const uint WM_DESTROY = 0x0002, WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_TIMER = 0x0113;
        private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1, SW_SHOWNOACTIVATE = 4, IDC_ARROW = 32512;

        private const int SmoothingModeAntiAlias = 4, PixelOffsetModeHighQuality = 2, TextRenderingHintClearTypeGridFit = 5;
        private const int UnitPixel = 2, FontStyleRegular = 0, FontStyleBold = 1, WrapModeTileFlipXY = 3, CombineModeReplace = 0;
        private const int LineCapRound = 2, LineJoinRound = 2;
        private const int StringFormatFlagsNoWrap = 0x1000, StringTrimmingEllipsisCharacter = 3, StringAlignmentCenter = 1, StringAlignmentFar = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
            public uint lPrivate;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public int fErase;
            public int left, top, right, bottom;
            public int fRestore;
            public int fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GdiplusStartupInput
        {
            public uint GdiplusVersion;
            public IntPtr DebugEventCallback;
            public int SuppressBackgroundThread;
            public int SuppressExternalCodecs;
        }

        [StructLayout(LayoutKind.Sequential)] private struct RectF { public float X, Y, Width, Height; }
        [StructLayout(LayoutKind.Sequential)] private struct PointF { public float X, Y; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
        [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr cursor);
        [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr id, uint elapse, IntPtr func);
        [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hWnd, IntPtr id);
        [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);
        [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT ps);
        [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);
        [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hWnd, IntPtr region, bool redraw);
        [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string moduleName);

        [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

        [DllImport("gdiplus.dll")] private static extern int GdiplusStartup(out IntPtr token, ref GdiplusStartupInput input, IntPtr output);
        [DllImport("gdiplus.dll")] private static extern void GdiplusShutdown(IntPtr token);
        [DllImport("gdiplus.dll")] private static extern int GdipCreateFromHDC(IntPtr hdc, out IntPtr graphics);
        [DllImport("gdiplus.dll")] private static extern int GdipDeleteGraphics(IntPtr graphics);
        [DllImport("gdiplus.dll")] private static extern int GdipSetSmoothingMode(IntPtr graphics, int mode);
        [DllImport("gdiplus.dll")] private static extern int GdipSetPixelOffsetMode(IntPtr graphics, int mode);
        [DllImport("gdiplus.dll")] private static extern int GdipSetTextRenderingHint(IntPtr graphics, int hint);
        [DllImport("gdiplus.dll")] private static extern int GdipSetClipPath(IntPtr graphics, IntPtr path, int combineMode);
        [DllImport("gdiplus.dll")] private static extern int GdipResetClip(IntPtr graphics);
        [DllImport("gdiplus.dll")] private static extern int GdipCreateSolidFill(uint argb, out IntPtr brush);
        [DllImport("gdiplus.dll")] private static extern int GdipCreateLineBrush(ref PointF p1, ref PointF p2, uint c1, uint c2, int wrapMode, out IntPtr brush);
        [DllImport("gdiplus.dll")] private static extern int GdipDeleteBrush(IntPtr brush);
        [DllImport("gdiplus.dll")] private static extern int GdipCreatePen1(uint argb, float width, int unit, out IntPtr pen);
        [DllImport("gdiplus.dll")] private static extern int GdipCreatePen2(IntPtr brush, float width, int unit, out IntPtr pen);
        [DllImport("gdiplus.dll")] private static extern int GdipSetPenStartCap(IntPtr pen, int cap);
        [DllImport("gdiplus.dll")] private static extern int GdipSetPenEndCap(IntPtr pen, int cap);
        [DllImport("gdiplus.dll")] private static extern int GdipSetPenLineJoin(IntPtr pen, int join);
        [DllImport("gdiplus.dll")] private static extern int GdipDeletePen(IntPtr pen);
        [DllImport("gdiplus.dll")] private static extern int GdipCreatePath(int fillMode, out IntPtr path);
        [DllImport("gdiplus.dll")] private static extern int GdipDeletePath(IntPtr path);
        [DllImport("gdiplus.dll")] private static extern int GdipAddPathArc(IntPtr path, float x, float y, float w, float h, float start, float sweep);
        [DllImport("gdiplus.dll")] private static extern int GdipAddPathLine(IntPtr path, float x1, float y1, float x2, float y2);
        [DllImport("gdiplus.dll")] private static extern int GdipClosePathFigure(IntPtr path);
        [DllImport("gdiplus.dll")] private static extern int GdipFillPath(IntPtr graphics, IntPtr brush, IntPtr path);
        [DllImport("gdiplus.dll")] private static extern int GdipDrawPath(IntPtr graphics, IntPtr pen, IntPtr path);
        [DllImport("gdiplus.dll")] private static extern int GdipFillRectangle(IntPtr graphics, IntPtr brush, float x, float y, float w, float h);
        [DllImport("gdiplus.dll")] private static extern int GdipFillEllipse(IntPtr graphics, IntPtr brush, float x, float y, float w, float h);
        [DllImport("gdiplus.dll")] private static extern int GdipDrawArc(IntPtr graphics, IntPtr pen, float x, float y, float w, float h, float start, float sweep);
        [DllImport("gdiplus.dll")] private static extern int GdipDrawLine(IntPtr graphics, IntPtr pen, float x1, float y1, float x2, float y2);
        [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)] private static extern int GdipCreateFontFamilyFromName(string name, IntPtr collection, out IntPtr family);
        [DllImport("gdiplus.dll")] private static extern int GdipGetGenericFontFamilySansSerif(out IntPtr family);
        [DllImport("gdiplus.dll")] private static extern int GdipDeleteFontFamily(IntPtr family);
        [DllImport("gdiplus.dll")] private static extern int GdipCreateFont(IntPtr family, float size, int style, int unit, out IntPtr font);
        [DllImport("gdiplus.dll")] private static extern int GdipDeleteFont(IntPtr font);
        [DllImport("gdiplus.dll")] private static extern int GdipCreateStringFormat(int flags, int language, out IntPtr format);
        [DllImport("gdiplus.dll")] private static extern int GdipDeleteStringFormat(IntPtr format);
        [DllImport("gdiplus.dll")] private static extern int GdipSetStringFormatTrimming(IntPtr format, int trimming);
        [DllImport("gdiplus.dll")] private static extern int GdipSetStringFormatAlign(IntPtr format, int align);
        [DllImport("gdiplus.dll")] private static extern int GdipSetStringFormatLineAlign(IntPtr format, int align);
        [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
        private static extern int GdipDrawString(IntPtr graphics, string text, int length, IntPtr font, ref RectF layout, IntPtr format, IntPtr brush);
    }
}
