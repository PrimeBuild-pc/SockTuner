using System.Windows;
using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner;

public partial class MainWindow : Window
{
    private readonly SystemInventoryService _inventory = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshInventoryAsync();
    }

    private async void RefreshInventory_Click(object sender, RoutedEventArgs e) => await RefreshInventoryAsync();

    private async Task RefreshInventoryAsync()
    {
        StatusText.Text = "Reading Windows network inventory…";

        try
        {
            var snapshot = await Task.Run(_inventory.Capture);
            ShowSnapshot(snapshot);
            StatusText.Text = $"Inventory refreshed at {snapshot.System.CapturedAt:HH:mm:ss}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Inventory failed: {exception.Message}";
        }
    }

    private void ShowSnapshot(NetworkSnapshot snapshot)
    {
        ActiveAdaptersText.Text = snapshot.ActiveAdapterCount.ToString();
        AdapterCountText.Text = snapshot.Adapters.Count.ToString();
        ProcessorCountText.Text = snapshot.System.LogicalProcessors.ToString();
        PrivilegeText.Text = snapshot.System.IsAdministrator ? "Elevated" : "Standard user";
        OsText.Text = snapshot.System.OperatingSystem;
        BuildText.Text = snapshot.System.Version;
        MachineText.Text = snapshot.System.MachineName;
        CapturedText.Text = snapshot.System.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss zzz");
        AdaptersGrid.ItemsSource = snapshot.Adapters;
    }
}
