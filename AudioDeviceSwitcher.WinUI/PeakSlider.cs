using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AudioDeviceSwitcher
{
    /// <summary>
    /// Windows 11 Fluent 2 Slider with an integrated live peak-level indicator rendered inside the slider rail.
    /// </summary>
    public sealed class PeakSlider : Slider
    {
        public static readonly DependencyProperty PeakLevelProperty =
            DependencyProperty.Register(
                nameof(PeakLevel),
                typeof(double),
                typeof(PeakSlider),
                new PropertyMetadata(0.0, OnPeakLevelChanged));

        private FrameworkElement? _peakHost;
        private Border? _peakIndicator;

        public double PeakLevel
        {
            get => (double)GetValue(PeakLevelProperty);
            set => SetValue(PeakLevelProperty, value);
        }

        private static void OnPeakLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PeakSlider slider)
            {
                slider.UpdatePeakVisual();
            }
        }

        protected override void OnApplyTemplate()
        {
            if (_peakHost != null)
            {
                _peakHost.SizeChanged -= PeakHost_SizeChanged;
            }

            base.OnApplyTemplate();

            _peakHost = GetTemplateChild("PeakMeterHost") as FrameworkElement;
            _peakIndicator = GetTemplateChild("PeakMeterIndicator") as Border;

            if (_peakHost != null)
            {
                _peakHost.SizeChanged += PeakHost_SizeChanged;
            }

            UpdatePeakVisual();
        }

        private void PeakHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdatePeakVisual();
        }

        private void UpdatePeakVisual()
        {
            if (_peakIndicator == null || _peakHost == null)
                return;

            double level = Math.Clamp(PeakLevel, 0.0, 100.0);
            double hostWidth = _peakHost.ActualWidth;

            if (level <= 0.5 || hostWidth <= 0)
            {
                _peakIndicator.Opacity = 0;
                _peakIndicator.Width = 0;
                return;
            }

            _peakIndicator.Opacity = 1;
            _peakIndicator.Width = (level / 100.0) * hostWidth;
        }
    }
}
