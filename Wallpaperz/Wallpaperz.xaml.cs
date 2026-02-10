using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.Wave;
using NAudio.Dsp;
using IOPath = System.IO.Path;
using IODirectory = System.IO.Directory;

namespace Wallpaperz
{
    public partial class MainWindow : Window
    {
        #region Win32 API
        [DllImport("user32.dll")]
        static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
        [DllImport("user32.dll")]
        static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll")]
        static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);
        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
            SendMessageTimeoutFlags fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [Flags]
        enum SendMessageTimeoutFlags : uint
        {
            SMTO_NORMAL = 0x0,
            SMTO_BLOCK = 0x1,
            SMTO_ABORTIFHUNG = 0x2,
            SMTO_NOTIMEOUTIFNOTHUNG = 0x8
        }
        #endregion

        private DispatcherTimer mainTimer;
        private readonly Random random = new Random();

        private WasapiLoopbackCapture capture;
        private float[] audioBuffer;
        private int spectrumType = 0;
        private DateTime lastSoundTime = DateTime.Now;
        private double rotationAngle = 0;
        private float currentVolume = 0;
        private double glowOpacity = 0;

        private readonly List<Line> spectrumLines = new List<Line>();
        private readonly int spectrumBars = 60;
        private readonly float[] smoothedSpectrum;

        private readonly double polygonRadius = 700;
        private readonly double cornerRoundness = 0.5;

        private double backgroundVelocityX;
        private double backgroundVelocityY;
        private double backgroundOffsetX = 0;
        private double backgroundOffsetY = 0;
        private TranslateTransform backgroundTransform;

        public MainWindow()
        {
            InitializeComponent();

            this.Left = 0;
            this.Top = 0;
            this.Width = SystemParameters.PrimaryScreenWidth;
            this.Height = SystemParameters.PrimaryScreenHeight;

            spectrumType = random.Next(0, 5);
            smoothedSpectrum = new float[spectrumBars];

            InitializeBackgroundMovement();
            AddToStartup();
        }

        private void InitializeBackgroundMovement()
        {
            bool moveLeft = random.Next(2) == 0;
            double baseAngle = moveLeft ? 180 : 0;
            double deviation = (random.NextDouble() * 45 - 22.5);
            double backgroundMoveAngle = (baseAngle + deviation) * Math.PI / 180.0;

            double speed = 0.2f;
            backgroundVelocityX = Math.Cos(backgroundMoveAngle) * speed;
            backgroundVelocityY = Math.Sin(backgroundMoveAngle) * speed;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            SetAsWallpaper();
            LoadBackgroundImages();
            InitializeTimers();
            InitializeAudio();
            InitializeSpectrumShapes();
        }

        private void LoadBackgroundImages()
        {
            try
            {
                string appPath = AppDomain.CurrentDomain.BaseDirectory;

                string bgPath = GetRandomImagePath(appPath, "background");
                if (!string.IsNullOrEmpty(bgPath))
                {
                    BitmapImage bgImage = new BitmapImage();
                    bgImage.BeginInit();
                    bgImage.UriSource = new Uri(bgPath);
                    bgImage.CacheOption = BitmapCacheOption.OnLoad;
                    bgImage.EndInit();
                    bgImage.Freeze();

                    BackgroundBrush.ImageSource = bgImage;
                    backgroundTransform = new TranslateTransform(0, 0);
                    BackgroundBrush.Transform = backgroundTransform;
                    BackgroundRectangle.Visibility = Visibility.Visible;
                }

                string fgPath = GetRandomImagePath(appPath, "foreground");
                if (!string.IsNullOrEmpty(fgPath))
                {
                    BitmapImage fgImage = new BitmapImage();
                    fgImage.BeginInit();
                    fgImage.UriSource = new Uri(fgPath);
                    fgImage.DecodePixelWidth = (int)SystemParameters.PrimaryScreenWidth;
                    fgImage.CacheOption = BitmapCacheOption.OnLoad;
                    fgImage.EndInit();
                    fgImage.Freeze();

                    ForegroundImage.Source = fgImage;
                    ForegroundImage.Visibility = Visibility.Visible;

                    ForegroundGlow.Background = new ImageBrush(fgImage) { Stretch = Stretch.Fill };
                    ForegroundGlow.Visibility = Visibility.Visible;
                }
            }
            catch { }
        }

        private string GetRandomImagePath(string directory, string prefix)
        {
            try
            {
                List<string> imagePaths = new List<string>();

                if (IODirectory.Exists(directory))
                {
                    var files = IODirectory.GetFiles(directory, "*.png");

                    foreach (var file in files)
                    {
                        string fileName = IOPath.GetFileNameWithoutExtension(file);

                        if (fileName.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                            fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            string afterPrefix = fileName.Substring(prefix.Length);

                            if (string.IsNullOrEmpty(afterPrefix) ||
                                afterPrefix.All(char.IsDigit))
                            {
                                imagePaths.Add(file);
                            }
                        }
                    }
                }

                if (imagePaths.Count > 0)
                {
                    return imagePaths[random.Next(imagePaths.Count)];
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private void SetAsWallpaper()
        {
            try
            {
                IntPtr progman = FindWindow("Progman", null);
                IntPtr result = IntPtr.Zero;

                SendMessageTimeout(progman, 0x052C, new IntPtr(0), IntPtr.Zero,
                    SendMessageTimeoutFlags.SMTO_NORMAL, 1000, out result);

                IntPtr workerw = IntPtr.Zero;
                EnumWindows(new EnumWindowsProc((tophandle, topparamhandle) =>
                {
                    IntPtr p = FindWindowEx(tophandle, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (p != IntPtr.Zero)
                    {
                        workerw = FindWindowEx(IntPtr.Zero, tophandle, "WorkerW", null);
                    }
                    return true;
                }), IntPtr.Zero);

                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                SetParent(hwnd, workerw);
            }
            catch { }
        }

        private void InitializeTimers()
        {
            mainTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            mainTimer.Tick += MainTimer_Tick;
            mainTimer.Start();

            TimeText.Text = DateTime.Now.ToString("HH:mm:ss");
        }

        private void InitializeSpectrumShapes()
        {
            for (int i = 0; i < spectrumBars; i++)
            {
                var line = new Line
                {
                    Stroke = Brushes.White,
                    StrokeThickness = 2.5,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Visibility = Visibility.Collapsed
                };
                spectrumLines.Add(line);
                SpectrumCanvas.Children.Add(line);
            }
        }

        private void InitializeAudio()
        {
            try
            {
                capture = new WasapiLoopbackCapture();
                audioBuffer = new float[4096];

                capture.DataAvailable += (s, e) =>
                {
                    try
                    {
                        int samplesRecorded = e.BytesRecorded / 4;
                        if (samplesRecorded > 0)
                        {
                            float sum = 0;
                            int limit = Math.Min(samplesRecorded, audioBuffer.Length);

                            for (int i = 0; i < limit; i++)
                            {
                                float sample = BitConverter.ToSingle(e.Buffer, i * 4);
                                audioBuffer[i] = sample * 100;
                                sum += sample * sample;
                            }

                            currentVolume = (float)Math.Sqrt(sum / samplesRecorded);

                            if (currentVolume > 0.005f)
                            {
                                lastSoundTime = DateTime.Now;
                            }
                        }
                    }
                    catch { }
                };

                capture.StartRecording();
            }
            catch { }
        }

        private void MainTimer_Tick(object sender, EventArgs e)
        {
            TimeText.Text = DateTime.Now.ToString("HH:mm:ss");

            if ((DateTime.Now - lastSoundTime).TotalSeconds > 60)
            {
                int oldType = spectrumType;
                do
                {
                    spectrumType = random.Next(0, 5);
                } while (spectrumType == oldType);
            }

            UpdateGlowEffect();
            UpdateBackgroundPosition();
            DrawSpectrum();
        }

        private void UpdateBackgroundPosition()
        {
            if (backgroundTransform == null) return;

            backgroundOffsetX += backgroundVelocityX;
            backgroundOffsetY += backgroundVelocityY;

            backgroundTransform.X = backgroundOffsetX;
            backgroundTransform.Y = backgroundOffsetY;

            if (BackgroundBrush.ImageSource is BitmapImage img)
            {
                double imageWidth = img.PixelWidth;
                double imageHeight = img.PixelHeight;

                if (Math.Abs(backgroundOffsetX) > imageWidth)
                {
                    backgroundOffsetX %= imageWidth;
                }
                if (Math.Abs(backgroundOffsetY) > imageHeight)
                {
                    backgroundOffsetY %= imageHeight;
                }
            }
        }

        private void UpdateGlowEffect()
        {
            bool hasSound = (DateTime.Now - lastSoundTime).TotalSeconds <= 1;

            if (hasSound)
            {
                float targetOpacity = Math.Min(currentVolume * 15, 0.9f);
                glowOpacity = glowOpacity * 0.7 + targetOpacity * 0.3;
            }
            else
            {
                glowOpacity *= 0.92;
                if (glowOpacity < 0.01) glowOpacity = 0;
            }

            if (GlowBlurEffect != null)
            {
                GlowBlurEffect.Radius = 15 + glowOpacity * 35;
            }

            if (ForegroundGlow != null)
            {
                ForegroundGlow.Opacity = glowOpacity;
            }
        }

        private void DrawSpectrum()
        {
            bool hasActiveSound = (DateTime.Now - lastSoundTime).TotalSeconds <= 1;

            if (!hasActiveSound || audioBuffer == null)
            {
                for (int i = 0; i < spectrumBars; i++)
                {
                    smoothedSpectrum[i] *= 0.85f;
                }

                bool anyVisible = smoothedSpectrum.Any(x => x > 0.5f);
                if (!anyVisible)
                {
                    foreach (var line in spectrumLines)
                        line.Visibility = Visibility.Collapsed;
                    return;
                }
            }
            else
            {
                var fftData = new Complex[1024];
                for (int i = 0; i < 1024; i++)
                {
                    fftData[i].X = (i < audioBuffer.Length ? audioBuffer[i] : 0) * (float)FastFourierTransform.HammingWindow(i, 1024);
                    fftData[i].Y = 0;
                }
                FastFourierTransform.FFT(true, 10, fftData);

                var magnitudes = new List<float>(200);
                for (int i = 2; i < 200; i++)
                {
                    float mag = (float)Math.Sqrt(fftData[i].X * fftData[i].X + fftData[i].Y * fftData[i].Y);
                    magnitudes.Add(mag);
                }

                for (int i = magnitudes.Count - 1; i > 0; i--)
                {
                    int j = random.Next(i + 1);
                    (magnitudes[i], magnitudes[j]) = (magnitudes[j], magnitudes[i]);
                }

                int magCount = magnitudes.Count;
                for (int i = 0; i < spectrumBars; i++)
                {
                    float value = magnitudes[i % magCount];
                    float targetValue = Math.Min(value * 1000, 500);
                    smoothedSpectrum[i] = smoothedSpectrum[i] * 0.65f + targetValue * 0.35f;
                }
            }

            rotationAngle += 0.5;
            if (rotationAngle >= 360) rotationAngle -= 360;

            switch (spectrumType)
            {
                case 0: DrawCircularSpectrum(); break;
                case 1: DrawRoundedPolygon(3); break;
                case 2: DrawRoundedPolygon(4); break;
                case 3: DrawRoundedPolygon(5); break;
                case 4: DrawRoundedPolygon(6); break;
            }
        }

        private void DrawCircularSpectrum()
        {
            double centerX = Width / 2;
            double centerY = Height / 2;
            double rotRad = rotationAngle * Math.PI / 180;

            for (int i = 0; i < spectrumBars; i++)
            {
                double angle = (i / (double)spectrumBars) * 2 * Math.PI + rotRad;
                double height = smoothedSpectrum[i];

                double cosA = Math.Cos(angle);
                double sinA = Math.Sin(angle);

                double x1 = centerX + cosA * polygonRadius;
                double y1 = centerY + sinA * polygonRadius;
                double x2 = centerX + cosA * (polygonRadius + height);
                double y2 = centerY + sinA * (polygonRadius + height);

                var line = spectrumLines[i];
                line.X1 = x1;
                line.Y1 = y1;
                line.X2 = x2;
                line.Y2 = y2;
                line.Visibility = height > 0.5 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void DrawRoundedPolygon(int sides)
        {
            double centerX = Width / 2;
            double centerY = Height / 2;
            double rotRad = rotationAngle * Math.PI / 180;

            double R = polygonRadius;
            double sectorAngle = 2 * Math.PI / sides;
            double halfSector = sectorAngle / 2;
            double startAngle = -Math.PI / 2;
            double apothem = R * Math.Cos(halfSector);

            double cosRot = Math.Cos(rotRad);
            double sinRot = Math.Sin(rotRad);

            for (int i = 0; i < spectrumBars; i++)
            {
                double angle = startAngle + (i / (double)spectrumBars) * 2 * Math.PI;

                double normAngle = angle - startAngle;
                while (normAngle < 0) normAngle += 2 * Math.PI;
                while (normAngle >= 2 * Math.PI) normAngle -= 2 * Math.PI;

                int sector = (int)(normAngle / sectorAngle);
                if (sector >= sides) sector = sides - 1;

                double sectorCenterAngle = sector * sectorAngle + halfSector;
                double angleFromSideCenter = normAngle - sectorCenterAngle;

                double vertexProximity = Math.Abs(angleFromSideCenter) / halfSector;
                double smoothVertex = Smootherstep(vertexProximity);

                double sharpRadius = apothem / Math.Cos(angleFromSideCenter);
                double cornerPull = smoothVertex * cornerRoundness * (sharpRadius - apothem);
                double roundedRadius = sharpRadius - cornerPull;

                double px = Math.Cos(angle) * roundedRadius;
                double py = Math.Sin(angle) * roundedRadius;
                double nx = Math.Cos(angle);
                double ny = Math.Sin(angle);

                double height = smoothedSpectrum[i];

                double ex = px + nx * height;
                double ey = py + ny * height;

                double rx1 = px * cosRot - py * sinRot;
                double ry1 = px * sinRot + py * cosRot;
                double rx2 = ex * cosRot - ey * sinRot;
                double ry2 = ex * sinRot + ey * cosRot;

                var line = spectrumLines[i];
                line.X1 = centerX + rx1;
                line.Y1 = centerY + ry1;
                line.X2 = centerX + rx2;
                line.Y2 = centerY + ry2;
                line.Visibility = height > 0.5 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private double Smootherstep(double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return t * t * t * (t * (t * 6 - 15) + 10);
        }

        private void AddToStartup()
        {
            try
            {
                string appName = "Wallpaperz";
                string appPath = System.Reflection.Assembly.GetExecutingAssembly().Location.Replace(".dll", ".exe");

                RegistryKey rk = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                rk?.SetValue(appName, appPath);
            }
            catch { }
        }

        protected override void OnClosed(EventArgs e)
        {
            capture?.StopRecording();
            capture?.Dispose();
            mainTimer?.Stop();
            base.OnClosed(e);
        }
    }
}
