using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using NetControl.Models;
using NetControl.Services;
using NetControl.ViewModels;

namespace NetControl.Views;

public partial class DevicesPage : UserControl
{
    private DevicesViewModel Vm => DataContext as DevicesViewModel;

    public DevicesPage()
    {
        InitializeComponent();
        CategoryBox.ItemsSource = Enum.GetValues<DeviceCategory>().ToList();
    }

    private System.Collections.Generic.List<Device> SelectedDevices() =>
        DevicesGrid.SelectedItems?.Cast<Device>().ToList() ?? new System.Collections.Generic.List<Device>();

    private void OnBlockInternet(object sender, RoutedEventArgs e)
    {
        var targets = SelectedDevices();
        if (targets.Count == 0 && Vm?.SelectedDevice != null) targets.Add(Vm.SelectedDevice);
        Vm?.BlockDeviceCommand.Execute(targets);
    }

    private void OnRestoreAccess(object sender, RoutedEventArgs e)
    {
        var targets = SelectedDevices();
        if (targets.Count == 0 && Vm?.SelectedDevice != null) targets.Add(Vm.SelectedDevice);
        Vm?.UnblockDeviceCommand.Execute(targets);
    }

    private void OnRestoreAll(object sender, RoutedEventArgs e) => Vm?.RestoreAllCommand.Execute(null);

    private void OnToggleTrust(object sender, RoutedEventArgs e)
    {
        var targets = SelectedDevices();
        if (targets.Count == 0 && Vm?.SelectedDevice != null) targets.Add(Vm.SelectedDevice);
        Vm?.ToggleTrustCommand.Execute(targets);
    }

    private void OnMarkOffline(object sender, RoutedEventArgs e)
    {
        var targets = SelectedDevices();
        if (targets.Count == 0 && Vm?.SelectedDevice != null) targets.Add(Vm.SelectedDevice);
        Vm?.MarkOfflineCommand.Execute(targets);
    }

    private void OnCopyInfo(object sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedDevice != null) Vm.CopyInfoCommand.Execute(Vm.SelectedDevice);
    }

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Vm?.SelectedDevice == null || CategoryBox.SelectedItem == null) return;
        if (CategoryBox.SelectedItem is DeviceCategory cat && Vm.SelectedDevice.Category != cat)
            AppServices.Devices.SetCategory(Vm.SelectedDevice, cat);
    }
}
