using System;
using System.Threading.Tasks;
using System.Windows;

namespace AudioDeviceSwitcher
{
    public partial class OsdWindow : Window
    {
        public OsdWindow()
        {
            InitializeComponent();
            
            // Position at bottom center of primary screen
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen != null)
            {
                this.Left = screen.WorkingArea.Left + (screen.WorkingArea.Width - this.Width) / 2;
                this.Top = screen.WorkingArea.Bottom - this.Height - 50;
            }
        }

        public async void ShowOsd(string deviceName)
        {
            DeviceNameText.Text = deviceName;
            this.Show();
            
            // Auto close after 2 seconds
            await Task.Delay(2000);
            this.Close();
        }
    }
}
