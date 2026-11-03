using Velopack;

namespace Meetu.App;

public static class EntryPoint
{
    [STAThread]
    public static void Main()
    {
        // Called here directly because the packaging tool verifies it in this executable's own entry point. It must come first:
        // during an update the installer starts this program with hook arguments and expects it to exit.
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
