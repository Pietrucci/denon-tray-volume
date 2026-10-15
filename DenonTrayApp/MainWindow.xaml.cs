using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace DenonTrayApp
{
    public partial class MainWindow : Window
    {
        private readonly DenonClient _denon = new DenonClient("192.168.1.42", 23);
        private static readonly string TvIp = "192.168.1.8";
        private bool _isUpdatingUI = false;
        private bool _userInteracting = false;
        private readonly DispatcherTimer _debounceTimer;
        private readonly DispatcherTimer _interactionTimer;
        private readonly DispatcherTimer _brightnessRefreshTimer;
        private readonly DispatcherTimer _brightnessDebounceTimer;
        private double _pendingVolumeForSend = -1;
        private double _pendingBrightnessForSend = -1;
        private bool _isUpdatingBrightnessUI = false;
        private bool _isUpdatingSoundModeUI = false;

        public MainWindow()
        {
            InitializeComponent();

            // Podpięcie zdarzeń
            _denon.VolumeChanged += Denon_VolumeChanged;
            _denon.ConnectionStateChanged += Denon_ConnectionStateChanged;
            _denon.SoundModeChanged += Denon_SoundModeChanged;

            // Uruchomienie stałego nasłuchu w tle
            _denon.Start();

            _debounceTimer = new DispatcherTimer();
            _debounceTimer.Interval = TimeSpan.FromMilliseconds(30);
            _debounceTimer.Tick += DebounceTimer_Tick;

            _interactionTimer = new DispatcherTimer();
            _interactionTimer.Interval = TimeSpan.FromMilliseconds(300);
            _interactionTimer.Tick += InteractionTimer_Tick;

            _brightnessRefreshTimer = new DispatcherTimer();
            _brightnessRefreshTimer.Interval = TimeSpan.FromSeconds(2);
            _brightnessRefreshTimer.Tick += BrightnessRefreshTimer_Tick;

            _brightnessDebounceTimer = new DispatcherTimer();
            _brightnessDebounceTimer.Interval = TimeSpan.FromMilliseconds(60);
            _brightnessDebounceTimer.Tick += BrightnessDebounceTimer_Tick;

            // set a simple speaker icon for the window
            this.Icon = CreateSpeakerIcon();

            // Try to ensure adb connection in background (TV has static IP)
            _ = EnsureAdbConnectedAsync();
            _ = RefreshBrightnessAsync();
        }

        private static async Task<bool> EnsureAdbConnectedAsync()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "adb",
                    Arguments = $"connect {TvIp}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return false;

                var stdout = await process.StandardOutput.ReadToEndAsync();
                var stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                var outTrim = stdout?.Trim().ToLowerInvariant() ?? string.Empty;
                var errTrim = stderr?.Trim().ToLowerInvariant() ?? string.Empty;

                if (outTrim.Contains("connected to") || outTrim.Contains("already connected") || errTrim.Contains("already connected"))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        private void Denon_ConnectionStateChanged(bool isConnected)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (isConnected)
                {
                    TxtStatus.Text = "192.168.1.42";
                    VolSlider.IsEnabled = true;
                }
                else
                {
                    TxtStatus.Text = "Łączenie...";
                    TxtVolume.Text = "--";
                    VolSlider.IsEnabled = false;
                }
            });
        }

        private void Denon_SoundModeChanged(string mode)
        {
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    TxtStatus.ToolTip = $"Sound mode: {mode}";

                    // Normalize the mode string and update the ComboBox selection accordingly
                    var norm = (mode ?? string.Empty).Trim().ToUpperInvariant();
                    string? friendly = null;
                    if (norm.Contains("DOLBY DIGITAL")) friendly = "Dolby Surround";
                    else if (norm.Contains("DOLBY")) friendly = "Dolby Surround";
                    else if (norm.Contains("DTS SURROUND")) friendly = "DTS Neural:X";
                    else if (norm.Contains("NEURAL")) friendly = "DTS Neural:X";
                    else if (norm.Contains("MCH") || norm.Contains("MULTI")) friendly = "Multi Ch Stereo";
                    else if (norm.Contains("STEREO")) friendly = "Stereo";

                    if (friendly != null)
                    {
                        _isUpdatingSoundModeUI = true;
                        try
                        {
                            CmbSoundMode.SelectedItem = null;
                            foreach (var item in CmbSoundMode.Items)
                            {
                                if (item is ComboBoxItem cbi && (cbi.Content as string) == friendly)
                                {
                                    CmbSoundMode.SelectedItem = cbi;
                                    break;
                                }
                            }
                        }
                        finally { _isUpdatingSoundModeUI = false; }
                    }
                }
                catch { }
            });
        }

        private void Denon_VolumeChanged(int newVolume)
        {
            // Aktualizacja suwaka na wątku UI, gdy Denon zgłosi zmianę (np. z pilota)
            Dispatcher.InvokeAsync(() =>
            {
                if (_userInteracting)
                    return; // ignore device updates while user interacts

                _isUpdatingUI = true;
                VolSlider.Value = Math.Clamp(newVolume, VolSlider.Minimum, VolSlider.Maximum);
                TxtVolume.Text = FormatDb(VolSlider.Value);
                _isUpdatingUI = false;
            });
        }

        public void ShowFlyout(System.Drawing.Point? iconScreenPos = null)
        {
            // Position the window above the provided screen point (cursor/icon), or fallback to bottom-right
            if (iconScreenPos.HasValue)
            {
                try
                {
                    var source = PresentationSource.FromVisual(this);
                    var transform = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
                    var pt = transform.Transform(new System.Windows.Point(iconScreenPos.Value.X, iconScreenPos.Value.Y));
                    Left = pt.X - (Width / 2.0);
                    Top = pt.Y - Height - 6;
                }
                catch
                {
                    var workingArea = SystemParameters.WorkArea;
                    Left = workingArea.Right - Width - 10;
                    Top = workingArea.Bottom - Height - 10;
                }
            }
            else
            {
                var workingArea = SystemParameters.WorkArea;
                Left = workingArea.Right - Width - 10;
                Top = workingArea.Bottom - Height - 10;
            }

            Show();
            Activate();
            Focus();
            _ = RefreshBrightnessAsync();
        }

        private async void VolSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Jeśli zmiana wartości pochodzi z odczytu z Denona, nie wysyłamy jej z powrotem
            if (_isUpdatingUI) return;

            double val = e.NewValue;
            TxtVolume.Text = FormatDb(val);

            // mark that the user is interacting and restart interaction timeout
            _userInteracting = true;
            _interactionTimer.Stop();
            _interactionTimer.Start();

            // debounce sending commands so a long scroll produces one event
            _pendingVolumeForSend = val;
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }



        private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (IsMouseOverSlider(VolSlider, e))
            {
                HandleSliderWheel(VolSlider, e, 0.5, true);
                return;
            }

            if (IsMouseOverSlider(BrightnessSlider, e))
            {
                HandleSliderWheel(BrightnessSlider, e, 10.0, false);
                return;
            }

            if (!VolSlider.IsEnabled) return;
            double step = Math.Sign(e.Delta) * 0.5;
            _userInteracting = true;
            _interactionTimer.Stop();
            _interactionTimer.Start();

            VolSlider.Value = Math.Clamp(VolSlider.Value + step, VolSlider.Minimum, VolSlider.Maximum);
            e.Handled = true;
        }

        private static bool IsMouseOverSlider(Slider slider, MouseWheelEventArgs e)
        {
            var mousePos = Mouse.GetPosition(slider);
            return mousePos.X >= 0 && mousePos.X <= slider.ActualWidth && mousePos.Y >= 0 && mousePos.Y <= slider.ActualHeight;
        }

        private void HandleSliderWheel(Slider slider, MouseWheelEventArgs e, double stepSize, bool isVolume)
        {
            if (!slider.IsEnabled)
                return;

            double step = Math.Sign(e.Delta) * stepSize;
            if (isVolume)
            {
                _userInteracting = true;
                _interactionTimer.Stop();
                _interactionTimer.Start();
            }

            slider.Value = Math.Clamp(slider.Value + step, slider.Minimum, slider.Maximum);
            e.Handled = true;
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (!VolSlider.IsEnabled) return;

            if (e.Key == Key.Up)
                VolSlider.Value = Math.Clamp(VolSlider.Value + 1, VolSlider.Minimum, VolSlider.Maximum);
            else if (e.Key == Key.Down)
                VolSlider.Value = Math.Clamp(VolSlider.Value - 1, VolSlider.Minimum, VolSlider.Maximum);
            else if (e.Key == Key.Escape)
                Hide();
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            Hide();
        }

        private void DebounceTimer_Tick(object? sender, EventArgs e)
        {
            _debounceTimer.Stop();
            if (_pendingVolumeForSend >= 0)
            {
                var toSend = _pendingVolumeForSend;
                _pendingVolumeForSend = -1;
                _ = _denon.SetVolumeAsync(toSend);
            }
        }

        private void InteractionTimer_Tick(object? sender, EventArgs e)
        {
            _interactionTimer.Stop();
            _userInteracting = false;
        }

        private async void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingBrightnessUI)
                return;

            var value = Math.Clamp(e.NewValue, BrightnessSlider.Minimum, BrightnessSlider.Maximum);
            TxtBrightness.Text = FormatBrightnessPercent(value);
            _pendingBrightnessForSend = value;
            _brightnessDebounceTimer.Stop();
            _brightnessDebounceTimer.Start();
            _brightnessRefreshTimer.Stop();
            _brightnessRefreshTimer.Start();
        }

        private void BrightnessSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _brightnessRefreshTimer.Stop();
        }

        private void BrightnessSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _brightnessDebounceTimer.Stop();
            if (_pendingBrightnessForSend >= 0)
            {
                var val = _pendingBrightnessForSend;
                _pendingBrightnessForSend = -1;
                _ = SetTvBrightnessAsync((int)Math.Round(val));
            }
            _brightnessRefreshTimer.Stop();
            _brightnessRefreshTimer.Start();
        }

        private void BrightnessDebounceTimer_Tick(object? sender, EventArgs e)
        {
            _brightnessDebounceTimer.Stop();
            if (_pendingBrightnessForSend >= 0)
            {
                var value = (int)Math.Round(_pendingBrightnessForSend);
                _pendingBrightnessForSend = -1;
                _ = SetTvBrightnessAsync(value);
            }
        }

        private void BrightnessRefreshTimer_Tick(object? sender, EventArgs e)
        {
            _brightnessRefreshTimer.Stop();
            _ = RefreshBrightnessAsync();
        }

        private static string FormatBrightnessPercent(double value)
        {
            var percent = Math.Clamp(value, 0.0, 100.0);
            return $"{Math.Round(percent, MidpointRounding.AwayFromZero)}%";
        }

        private async Task RefreshBrightnessAsync()
        {
            var brightness = await GetTvBrightnessAsync();
            if (brightness < 0)
                return;

            var percent = Math.Clamp((brightness / 255.0) * 100.0, 0.0, 100.0);
            await Dispatcher.InvokeAsync(() =>
            {
                _isUpdatingBrightnessUI = true;
                BrightnessSlider.Value = percent;
                TxtBrightness.Text = FormatBrightnessPercent(percent);
                _isUpdatingBrightnessUI = false;
            });
        }

        private static async Task<int> GetTvBrightnessAsync()
        {
            // ensure adb connection first
            var connected = await EnsureAdbConnectedAsync();
            if (!connected)
                return -1;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "adb",
                    Arguments = "shell settings get system screen_brightness",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return -1;

                var stdout = await process.StandardOutput.ReadToEndAsync();
                var stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                var raw = stdout?.Trim();
                if (string.IsNullOrWhiteSpace(raw))
                    return -1;

                if (int.TryParse(raw, out var value))
                    return Math.Clamp(value, 0, 255);
            }
            catch { }

            return -1;
        }

        private static async Task SetTvBrightnessAsync(int percentValue)
        {
            // ensure adb connection first
            var connected = await EnsureAdbConnectedAsync();
            if (!connected)
                return;

            var percent = Math.Clamp(percentValue, 0, 100);
            var actual = (int)Math.Round((percent / 100.0) * 255.0);
            if (percent == 0)
                actual = 1; // Android ignores 0 as a real brightness value; minimum valid level is 1

            var safeValue = Math.Clamp(actual, 1, 255);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "adb",
                    Arguments = $"shell settings put system screen_brightness {safeValue}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return;

                await process.WaitForExitAsync();
            }
            catch { }
        }

        private void VolSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            HandleSliderWheel(VolSlider, e, 0.5, true);
        }

        private void BrightnessSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            HandleSliderWheel(BrightnessSlider, e, 10.0, false);
        }

        private void VolSlider_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _userInteracting = true;
            _interactionTimer.Stop();
        }

        private void VolSlider_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // send immediately on mouse up
            _interactionTimer.Stop();
            _userInteracting = false;

            _debounceTimer.Stop();
            double val = VolSlider.Value;
            _ = _denon.SetVolumeAsync(val);
        }

        private string FormatDb(double volumeValue)
        {
            double max = VolSlider.Maximum;
            const double minDb = -80.0;
            double db = minDb + (volumeValue / max) * (0 - minDb);
            double rounded = Math.Round(db * 2.0) / 2.0;
            return $"{rounded.ToString("0.0", CultureInfo.InvariantCulture)} dB";
        }

        // Helper to send a raw command to Denon (useful for testing sound-mode commands)
        private async Task<bool> SendDenonRawCommandAsync(string cmd)
        {
            try
            {
                if (!_denon.IsConnected)
                    return false;

                return await _denon.SendRawAsync(cmd);
            }
            catch { return false; }
        }

        private async void CmbSoundMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingSoundModeUI) return;
            if (!_denon.IsConnected) return;

                if (CmbSoundMode.SelectedItem is ComboBoxItem cbi && cbi.Content is string sel)
            {
                    string cmd = sel switch
                    {
                        "Stereo" => "MSSTEREO",
                        "Dolby Surround" => "MSDOLBY DIGITAL",
                        "DTS Neural:X" => "MSDTS SURROUND",
                        "Multi Ch Stereo" => "MSMCH STEREO",
                        _ => string.Empty
                    };

                if (!string.IsNullOrEmpty(cmd))
                {
                    // send command but do not update UI optimistically; wait for Denon's response
                    _ = SendDenonRawCommandAsync(cmd);
                }
            }
        }

        private void TxtStatus_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var ip = "192.168.1.42";
            if (string.IsNullOrWhiteSpace(TxtStatus.Text) || TxtStatus.Text == "Łączenie...")
                return;

            var url = $"http://{ip}/";
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch
            {
                // ignore browser launch failures
            }
        }

        private System.Windows.Media.ImageSource CreateSpeakerIcon()
        {
            var group = new System.Windows.Media.DrawingGroup();

            // Speaker rectangle
            var speakerBrush = System.Windows.Media.Brushes.White;
            var speakerPen = new System.Windows.Media.Pen(System.Windows.Media.Brushes.Transparent, 0);
            var rect = new System.Windows.Rect(2, 6, 6, 8);
            group.Children.Add(new System.Windows.Media.GeometryDrawing(speakerBrush, speakerPen, new System.Windows.Media.RectangleGeometry(rect)));

            // Triangle cone
            var tri = new System.Windows.Media.PathGeometry();
            var fig = new System.Windows.Media.PathFigure { StartPoint = new System.Windows.Point(8,6) };
            fig.Segments.Add(new System.Windows.Media.LineSegment(new System.Windows.Point(12,4), true));
            fig.Segments.Add(new System.Windows.Media.LineSegment(new System.Windows.Point(12,16), true));
            fig.IsClosed = true;
            tri.Figures.Add(fig);
            group.Children.Add(new System.Windows.Media.GeometryDrawing(speakerBrush, speakerPen, tri));

            // Sound waves (two arcs)
            var wavePen = new System.Windows.Media.Pen(System.Windows.Media.Brushes.White, 1.2);
            var wave1 = new System.Windows.Media.PathGeometry();
            var f1 = new System.Windows.Media.PathFigure { StartPoint = new System.Windows.Point(14,7) };
            f1.Segments.Add(new System.Windows.Media.ArcSegment(new System.Windows.Point(14,13), new System.Windows.Size(3,3), 0, false, System.Windows.Media.SweepDirection.Clockwise, true));
            wave1.Figures.Add(f1);
            group.Children.Add(new System.Windows.Media.GeometryDrawing(null, wavePen, wave1));

            var wave2 = new System.Windows.Media.PathGeometry();
            var f2 = new System.Windows.Media.PathFigure { StartPoint = new System.Windows.Point(16,5) };
            f2.Segments.Add(new System.Windows.Media.ArcSegment(new System.Windows.Point(16,15), new System.Windows.Size(5,5), 0, false, System.Windows.Media.SweepDirection.Clockwise, true));
            wave2.Figures.Add(f2);
            group.Children.Add(new System.Windows.Media.GeometryDrawing(null, wavePen, wave2));

            var drawingImage = new System.Windows.Media.DrawingImage(group);
            drawingImage.Freeze();
            return drawingImage;
        }
    }
}