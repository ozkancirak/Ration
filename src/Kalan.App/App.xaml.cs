using System;
using System.IO;
using Microsoft.UI.Xaml;
using Kalan.App.Views;

namespace Kalan.App;

public partial class App : Application
{
    private FlyoutWindow? _flyoutWindow;

    public App()
    {
        InitializeComponent();

        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log");

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            File.AppendAllText(logPath, $"AppDomain UnhandledException: {e.ExceptionObject}\n");
        };

        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            File.AppendAllText(logPath, $"ProcessExit called at {DateTime.Now}\nStack:\n{Environment.StackTrace}\n");
        };

        this.UnhandledException += (s, e) =>
        {
            File.AppendAllText(logPath, $"Xaml UnhandledException: {e.Exception}\n");
        };
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log");
        try
        {
            File.WriteAllText(logPath, $"App.OnLaunched started at {DateTime.Now}\n");
            _flyoutWindow = new FlyoutWindow();
            File.AppendAllText(logPath, "FlyoutWindow created\n");
            _flyoutWindow.InitializeHidden();
            File.AppendAllText(logPath, "FlyoutWindow gizlendi, uygulama tray'de calisiyor\n");
        }
        catch (Exception ex)
        {
            File.AppendAllText(logPath, $"Exception in OnLaunched: {ex}\n");
        }
    }
}
