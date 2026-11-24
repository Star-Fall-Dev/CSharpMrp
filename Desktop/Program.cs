using System.Text;

namespace CSharpMrp.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ApplicationConfiguration.Initialize();
        using var window = new PlayerWindow(args);
        Application.Run(window);
    }
}
