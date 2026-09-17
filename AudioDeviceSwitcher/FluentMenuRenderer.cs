using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AudioDeviceSwitcher
{
    /// <summary>
    /// A dark, Windows 11-styled renderer for the tray <see cref="ContextMenuStrip"/>.
    /// Rounded selection backplates, thin separators, accent-tinted hover — matching the
    /// WPF surfaces so the whole app feels coherent. Colors are pulled from ThemeManager.
    /// </summary>
    public class FluentMenuRenderer : ToolStripProfessionalRenderer
    {
        public FluentMenuRenderer() : base(new FluentColorTable()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = ThemeManager.IsDark ? Color.FromArgb(245, 245, 245) : Color.FromArgb(16, 16, 16);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected)
            {
                base.OnRenderMenuItemBackground(e);
                return;
            }

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var rect = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            var a = ThemeManager.Accent;
            var accent = Color.FromArgb(a.R, a.G, a.B);
            using var brush = new SolidBrush(Color.FromArgb(ThemeManager.IsDark ? 40 : 34, accent));
            using var path = Rounded(rect, 5);
            g.FillPath(brush, path);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var g = e.Graphics;
            int y = e.Item.Height / 2;
            using var pen = new Pen(Color.FromArgb(ThemeManager.IsDark ? 28 : 24,
                ThemeManager.IsDark ? Color.White : Color.Black));
            g.DrawLine(pen, 12, y, e.Item.Width - 12, y);
        }

        private static GraphicsPath Rounded(Rectangle b, int r)
        {
            var p = new GraphicsPath();
            int d = r * 2;
            p.AddArc(b.X, b.Y, d, d, 180, 90);
            p.AddArc(b.Right - d, b.Y, d, d, 270, 90);
            p.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90);
            p.AddArc(b.X, b.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>Dark/light background + border colors for the menu surface.</summary>
    public class FluentColorTable : ProfessionalColorTable
    {
        public FluentColorTable() { UseSystemColors = false; }

        private static Color Surface => ThemeManager.IsDark ? Color.FromArgb(43, 43, 43) : Color.FromArgb(249, 249, 249);
        private static Color Border => ThemeManager.IsDark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(220, 220, 220);

        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Color.Transparent;
        public override Color MenuItemSelectedGradientBegin => Color.Transparent;
        public override Color MenuItemSelectedGradientEnd => Color.Transparent;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }
}
