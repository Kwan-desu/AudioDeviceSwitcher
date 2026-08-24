namespace AudioDeviceSwitcherInstaller;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        
        bool silent = args.Contains("--silent") || args.Contains("-s");
        Application.Run(new Form1(silent));
    }    
}