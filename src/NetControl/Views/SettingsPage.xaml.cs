using System.Windows;
using System.Windows.Controls;
using NetControl.ViewModels;

namespace NetControl.Views;

public partial class SettingsPage : UserControl
{
    private SettingsViewModel Vm => DataContext as SettingsViewModel;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (Vm != null)
            {
                Vm.ThemeChangeRequested += theme => App.ApplyTheme(theme);
                RouterPassword.Password = Vm.S.Router.Password;
            }
        };
    }

    private void OnRouterPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (Vm != null) Vm.S.Router.Password = RouterPassword.Password;
    }
}
