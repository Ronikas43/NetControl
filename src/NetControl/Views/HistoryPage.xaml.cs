using System.Windows;
using System.Windows.Controls;
using NetControl.ViewModels;

namespace NetControl.Views;

public partial class HistoryPage : UserControl
{
    private HistoryViewModel Vm => DataContext as HistoryViewModel;

    public HistoryPage()
    {
        InitializeComponent();
    }

    private void OnShowDeviceHistory(object sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedDevice != null)
            Vm.ShowDeviceHistory(Vm.SelectedDevice);
    }
}
