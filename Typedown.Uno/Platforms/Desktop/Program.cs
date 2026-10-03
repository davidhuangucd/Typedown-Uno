using Uno.UI.Hosting;

namespace Typedown.Uno;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        App.InitializeLogging();
        Services.ExitCleanup.HandleTerminate();
        Services.MacOpenDocuments.Install();
        Services.MacKeepRunning.Install();

        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseX11()
            .UseLinuxFrameBuffer()
            .UseMacOS()
            .UseWin32()
            .Build();

        host.Run();
    }
}
