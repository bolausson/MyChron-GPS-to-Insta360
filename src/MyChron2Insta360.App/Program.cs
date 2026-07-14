using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MyChron2Insta360.App;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    [STAThread]
    private static int Main(string[] args)
    {
        // Any dash-prefixed flag means the user drove this from a terminal -> headless CLI.
        bool cli = args.Any(a => a.Length > 0 && a[0] == '-');
        if (cli)
        {
            AttachConsole(ATTACH_PARENT_PROCESS); // route Console output to the calling console
            return Cli.Run(args);
        }

        // GUI mode. A bare path (CSV dragged onto the .exe) preloads the form.
        string? preload = args.FirstOrDefault(File.Exists);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(preload));
        return 0;
    }
}
