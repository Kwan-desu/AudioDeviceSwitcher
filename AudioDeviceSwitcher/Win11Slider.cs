using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AudioDeviceSwitcher
{
    public class Win11Slider : Control
    {
        private int _value = 100;
        private int _maximum = 100;
        private int _minimum = 0;
        private bool _isDragging = false;
        private bool _isHovered = false;

        public event EventHandler? ValueChanged;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Value
        {
            get => _value;
            set
            {
                int clamped = Math.Clamp(value, _minimum, _maximum);
                if (_value != clamped)
                {
                    _value = clamped;
                    Invalidate();
                    ValueChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Maximum
        {
            get => _maximum;
            set { _maximum = value; Invalidate(); }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Minimum
        {
            get => _minimum;
            set { _minimum = value; Invalidate(); }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color TrackColor { get; set; } = Color.FromArgb(65, 65, 65);

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color AccentColor { get; set; } = Color.FromArgb(96, 205, 255); // Fluent Accent Blue (#60CDFF)

        public Win11Slider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Height = 28;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _isHovered = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _isHovered = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int trackHeight = 4;
            int trackY = (Height - trackHeight) / 2;
            int thumbWidth = 6;
            int thumbHeight = 18;

            int startX = thumbWidth / 2 + 2;
            int endX = Width - thumbWidth / 2 - 2;
            int usableWidth = Math.Max(1, endX - startX);

            float percent = (_maximum > _minimum) ? (float)(_value - _minimum) / (_maximum - _minimum) : 0f;
            int thumbX = startX + (int)(usableWidth * percent);

            // Inactive Track
            using (var trackPen = new Pen(TrackColor, trackHeight) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(trackPen, startX, trackY + trackHeight / 2, endX, trackY + trackHeight / 2);
            }

            // Active Track
            if (thumbX > startX)
            {
                using (var activePen = new Pen(AccentColor, trackHeight) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(activePen, startX, trackY + trackHeight / 2, thumbX, trackY + trackHeight / 2);
                }
            }

            // Thumb Capsule
            int thumbY = (Height - thumbHeight) / 2;
            Rectangle thumbRect = new Rectangle(thumbX - thumbWidth / 2, thumbY, thumbWidth, thumbHeight);
            Color thumbColor = _isDragging || _isHovered ? Color.White : AccentColor;
            using (var thumbBrush = new SolidBrush(thumbColor))
            using (var path = GetRoundedRect(thumbRect, 3))
            {
                g.FillPath(thumbBrush, path);
            }
        }

        private GraphicsPath GetRoundedRect(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;
            Rectangle arc = new Rectangle(bounds.Location, new Size(diameter, diameter));

            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _isDragging = true;
                UpdateValueFromMouse(e.X);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_isDragging)
            {
                UpdateValueFromMouse(e.X);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _isDragging = false;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int step = e.Delta > 0 ? 2 : -2;
            Value += step;
        }

        private void UpdateValueFromMouse(int mouseX)
        {
            int thumbWidth = 6;
            int startX = thumbWidth / 2 + 2;
            int endX = Width - thumbWidth / 2 - 2;
            int usableWidth = Math.Max(1, endX - startX);

            float fraction = (float)(mouseX - startX) / usableWidth;
            fraction = Math.Clamp(fraction, 0f, 1f);

            Value = _minimum + (int)(fraction * (_maximum - _minimum));
        }
    }
}
