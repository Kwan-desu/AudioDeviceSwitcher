using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AudioDeviceSwitcher
{
    public class WaveformPeakMeter : System.Windows.Controls.UserControl
    {
        public static readonly DependencyProperty PeakValueProperty =
            DependencyProperty.Register("PeakValue", typeof(double), typeof(WaveformPeakMeter), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPeakValueChanged));

        public double PeakValue
        {
            get { return (double)GetValue(PeakValueProperty); }
            set { SetValue(PeakValueProperty, value); }
        }

        private static void OnPeakValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((WaveformPeakMeter)d).UpdateWaveform((double)e.NewValue);
        }

        private Path _path;
        private List<double> _history = new List<double>();
        private const int MaxPoints = 40;

        public WaveformPeakMeter()
        {
            _path = new Path
            {
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(140, 96, 205, 255)), // Semi-transparent blue wave
                Stretch = Stretch.Fill,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left
            };

            var grid = new Grid { ClipToBounds = true };
            grid.Children.Add(_path);
            this.Content = grid;
            
            for (int i = 0; i < MaxPoints; i++) _history.Add(0);
        }

        private void UpdateWaveform(double newValue)
        {
            double val = Math.Clamp(newValue / 100.0, 0, 1);
            
            // Smooth the input slightly
            double lastVal = _history[_history.Count - 1];
            double smoothed = lastVal + (val - lastVal) * 0.4;

            _history.Add(smoothed);
            if (_history.Count > MaxPoints)
            {
                _history.RemoveAt(0);
            }

            DrawPath();
        }

        private void DrawPath()
        {
            if (ActualWidth == 0 || ActualHeight == 0) return;

            double width = ActualWidth;
            double height = ActualHeight;
            double step = width / (MaxPoints - 1);

            StreamGeometry geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                double baseLine = height / 2 + 1; // Align with the center track
                
                ctx.BeginFigure(new System.Windows.Point(0, baseLine), true, true);
                
                for (int i = 0; i < _history.Count; i++)
                {
                    double x = i * step;
                    double amplitude = _history[i] * (height / 2); 
                    double y = baseLine - amplitude;
                    
                    ctx.LineTo(new System.Windows.Point(x, y), true, true);
                }

                ctx.LineTo(new System.Windows.Point(width, baseLine), true, true);
            }
            geometry.Freeze();
            _path.Data = geometry;
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            DrawPath();
        }
    }
}
