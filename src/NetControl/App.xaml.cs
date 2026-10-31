using System;
using System.Windows;
using Microsoft.Toolkit.Uwp.Notifications;
using NetControl.Services;
using NetControl.ViewModels;
using NetControl.Views;

namespace NetControl;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Build the full service graph on the UI thread so event marshalling works.
        AppServices.Build();

        ApplyTheme(AppServices.Settings.Settings.Theme);

        // Toast activation needs an AUMID; safe-guarded for unpackaged runs.
        try { ToastNotificationManagerCompat.Uninstall(); } catch { }

        var window = new MainWindow
        {
            DataContext = new MainViewModel()
        };

        // Clipboard plumbing from platform-neutral ViewModels.
        window.Loaded += (_, _) =>
        {
            if (window.DataContext is MainViewModel vm)
            {
                vm.DevicesPage.CopyToClipboard += text => Copy(text);
                vm.MacPage.CopyToClipboard += text => Copy(text);
                vm.HistoryPage.Reload();
                vm.DevicesPage.RebuildFiltered();
            }
        };

        // Start scanning / scheduler / security after the window shows.
        window.ContentRendered += (_, _) =>
        {
            AppServices.Scan.Start();
            AppServices.Scheduler.Start();
            AppServices.Security.LoadFromDatabase();
            AppServices.Security.StartProbing();
            _ = AppServices.Scan.ScanNowAsync();
        };

        MainWindow = window;
        window.Show();
    }

    private static void Copy(string text)
    {
        try { Clipboard.SetText(text ?? ""); } catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _ = AppServices.Arp?.StopAllSpoofing();
            AppServices.Arp?.Unbind();
            AppServices.Scan?.Dispose();
            AppServices.Scheduler?.Dispose();
            AppServices.Security?.Dispose();
            AppServices.Traffic?.Dispose();
        }
        catch { }
        base.OnExit(e);
    }

    public static void ApplyTheme(string theme)
    {
        var uri = theme == "Light"
            ? new Uri("Themes/Light.xaml", UriKind.Relative)
            : new Uri("Themes/Dark.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };
        var merged = Application.Current.Resources.MergedDictionaries;
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            var src = merged[i].Source?.OriginalString ?? "";
            if (src.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase) ||
                src.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase))
                merged.RemoveAt(i);
        }
        merged.Insert(0, dict);
    }
}
