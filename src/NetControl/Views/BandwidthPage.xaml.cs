using System.Windows.Controls;
using NetControl.Services;

namespace NetControl.Views;

public partial class BandwidthPage : UserControl
{
    public BandwidthPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Chart.Attach(AppServices.Traffic);
    }
}
