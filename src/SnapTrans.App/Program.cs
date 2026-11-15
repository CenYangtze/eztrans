using Velopack;

namespace SnapTrans.App;
internal static class Program
{
    [STAThread]
    public static void Main()
    {
        // Process Velopack install/update hooks before creating any WPF windows or mutex.
        VelopackApp.Build()
            .OnAfterUpdateFastCallback(_ =>
            {
                var autoStart = new SnapTrans.Platform.Windows.AutoStartService();
                if (autoStart.IsEnabled()) autoStart.SetEnabled(true);
            })
            .OnBeforeUninstallFastCallback(_ => new SnapTrans.Platform.Windows.AutoStartService().SetEnabled(false))
            .Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
