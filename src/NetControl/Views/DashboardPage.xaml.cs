using System.Windows;
using System.Windows.Controls;
using NetControl.Services;

namespace NetControl.Views;

public partial class DashboardPage : UserControl
{
    public DashboardPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Chart.Attach(AppServices.Traffic);
    }
}
