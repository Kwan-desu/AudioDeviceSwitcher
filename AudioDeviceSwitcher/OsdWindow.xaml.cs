using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;

namespace AudioDeviceSwitcher
{
    public partial class OsdWindow : Window
    {
        public OsdWindow()
        {
            InitializeComponent();

            this.SourceInitialized += OsdWindow_SourceInitialized;

            // Position at bottom center of primary screen
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen != null)
            {
                this.Left = screen.WorkingArea.Left + (screen.WorkingArea.Width - this.Width) / 2;
                this.Top = screen.WorkingArea.Bottom - this.Height - 60;
            }
        }

        private void OsdWindow_SourceInitialized(object? sender, EventArgs e)
        {
            // Transient surface → Acrylic. Legacy tint kept for pre-22621 fallback.
            WindowBackdrop.Apply(this, WindowBackdrop.BackdropKind.Acrylic, ThemeManager.IsDark,
                legacyTint: unchecked((int)0x35121212));
        }

        private System.Threading.CancellationTokenSource? _hideCts;

        public void ShowOsd(string deviceName)
        {
            ShowOsd("Audio playback switched", deviceName, "\uE995");
        }

        /// <summary>Shows the OSD. If <paramref name="volume"/> is provided (0–100) a progress bar is shown.</summary>
        public async void ShowOsd(string title, string text, string glyph = "\uE995", int? volume = null)
        {
            TitleText.Text = title;
            DeviceNameText.Text = text;
            GlyphText.Text = glyph;

            if (volume.HasValue)
            {
                VolumeBarTrack.Visibility = Visibility.Visible;
                // Set fill width relative to the track once laid out.
                VolumeBarTrack.Loaded -= UpdateBar;
                VolumeBarTrack.Loaded += UpdateBar;
                UpdateBar(null, null);

                void UpdateBar(object? s, RoutedEventArgs? ev)
                {
                    double trackWidth = VolumeBarTrack.ActualWidth;
                    if (trackWidth <= 0) trackWidth = this.Width - 32 - 40 - 12; // fallback estimate
                    VolumeBarFill.Width = Math.Max(0, Math.Min(1.0, volume.Value / 100.0)) * trackWidth;
                }
            }
            else
            {
                VolumeBarTrack.Visibility = Visibility.Collapsed;
            }

            _hideCts?.Cancel();
            _hideCts = new System.Threading.CancellationTokenSource();
            var token = _hideCts.Token;

            this.BeginAnimation(UIElement.OpacityProperty, null);
            this.Opacity = 1;
            this.Show();

            try
            {
                await Task.Delay(1500, token);
                if (token.IsCancellationRequested) return;

                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
                fadeOut.Completed += (s, e) =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        this.Close();
                    }
                };
                this.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OSD] hide failed: {ex.Message}");
                this.Close();
            }
        }
    }
}
