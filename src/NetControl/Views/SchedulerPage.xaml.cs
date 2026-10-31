using System.Windows;
using System.Windows.Controls;
using NetControl.Models;
using NetControl.ViewModels;

namespace NetControl.Views;

public partial class SchedulerPage : UserControl
{
    private SchedulerViewModel Vm => DataContext as SchedulerViewModel;

    public SchedulerPage()
    {
        InitializeComponent();
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Schedule s)
            Vm?.EditScheduleCommand.Execute(s);
    }

    private void OnToggleEnabled(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Schedule s)
            Vm?.ToggleEnabledCommand.Execute(s);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Schedule s)
            Vm?.DeleteScheduleCommand.Execute(s);
    }
}
