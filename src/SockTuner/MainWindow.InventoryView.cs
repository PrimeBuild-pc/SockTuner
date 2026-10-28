using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using SockTuner.Models;
using SockTuner.Persistence;
using SockTuner.Services;
using SockTuner.Services.Collection;
using SockTuner.Services.Diagnosis;
using SockTuner.Views;

namespace SockTuner;

/// <summary>Filtering and copying rows of the inventory tables.</summary>
public partial class MainWindow
{
    private void InventoryFilter_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyInventoryFilter();

    private void InventoryTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Either level of navigation: switching a Network details sub-page changes the table too.
        if (e.OriginalSource is System.Windows.Controls.TabControl)
        {
            _selectedInventoryItem = null;
            _selectedInventoryGrid = null;
            CopyInventoryRowButton.IsEnabled = false;
        }
    }

    private void InventoryGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.DataGrid grid)
        {
            return;
        }

        if (grid.SelectedItem is { } item)
        {
            _selectedInventoryItem = item;
            _selectedInventoryGrid = grid;
            CopyInventoryRowButton.IsEnabled = true;
        }
        else if (ReferenceEquals(grid, _selectedInventoryGrid))
        {
            _selectedInventoryItem = null;
            _selectedInventoryGrid = null;
            CopyInventoryRowButton.IsEnabled = false;
        }
    }

    private void CopyInventorySelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInventoryItem is null)
        {
            StatusText.Text = Loc.T("Select an inventory row to copy.");
            return;
        }

        CopyToClipboard(
            JsonSerializer.Serialize(_selectedInventoryItem, _selectedInventoryItem.GetType(), new JsonSerializerOptions { WriteIndented = true }),
            "Copied selected inventory row.");
    }

    private void ApplyInventoryFilter()
    {
        if (_snapshot is null)
        {
            return;
        }

        var query = InventoryFilterText.Text;
        RoutesGrid.ItemsSource = Filter(_snapshot.Routes, query);
        DnsInterfacesGrid.ItemsSource = Filter(_snapshot.Adapters.Where(adapter => adapter.Ipv4Index > 0 || adapter.Ipv6Index > 0), query);
        NetworkProfilesGrid.ItemsSource = Filter(_snapshot.NetworkProfiles ?? [], query);
        BindingsGrid.ItemsSource = Filter(_snapshot.NetworkBindings ?? [], query);
        GlobalOffloadsGrid.ItemsSource = Filter(_snapshot.GlobalOffloads ?? [], query);
        AdapterOffloadsGrid.ItemsSource = Filter(_snapshot.AdapterOffloads ?? [], query);
        TcpSettingsGrid.ItemsSource = Filter(_snapshot.TcpSettings ?? [], query);
        QosPoliciesGrid.ItemsSource = Filter(_snapshot.QosPolicies ?? [], query);
        WinsockProvidersGrid.ItemsSource = Filter(_snapshot.WinsockProviders ?? [], query);
        ApplyAdapterFilter();
        ApplyNdisFilter();
    }

    private void AdapterFilter_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyAdapterFilter();

    private void NdisFilter_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyNdisFilter();

    private void ApplyAdapterFilter() =>
        AdaptersGrid.ItemsSource = Filter(
            Filter(_adapterRows, AdapterFilterText.Text), InventoryFilterText.Text);

    private void ApplyNdisFilter() =>
        NdisPropertiesGrid.ItemsSource = Filter(
            Filter(_ndisRows, NdisFilterText.Text), InventoryFilterText.Text);

    private void CopyAdapter_Click(object sender, RoutedEventArgs e)
    {
        if (AdaptersGrid.SelectedItem is not AdapterInfo adapter)
        {
            StatusText.Text = Loc.T("Select an adapter to copy.");
            return;
        }

        CopyToClipboard(
            $"Name\t{adapter.Name}\nDescription\t{adapter.Description}\nID\t{adapter.Id}\nStatus\t{adapter.Status}\nKind\t{adapter.AdapterKindDisplay}\nType\t{adapter.InterfaceType}\nSpeed\t{adapter.SpeedDisplay}\nMTU\t{adapter.MtuDisplay}\nIPv4 index\t{adapter.Ipv4Index}\nIPv6 index\t{adapter.Ipv6Index}\nIP metrics\t{adapter.MetricDisplay}\nDefault routes\t{adapter.DefaultRoutePolicyDisplay}\nReceived\t{adapter.ReceivedDisplay}\nSent\t{adapter.SentDisplay}\nReceive issues\t{adapter.ReceiveIssuesDisplay}\nSend issues\t{adapter.SendIssuesDisplay}\nDriver\t{adapter.DriverDisplay}\nNDIS\t{adapter.NdisPropertyCountDisplay}\nProtocols\t{adapter.ProtocolsDisplay}\nInventory\t{adapter.InventoryStatus}\nAddresses\t{adapter.AddressesDisplay}\nGateways\t{adapter.GatewaysDisplay}\nDNS\t{adapter.DnsDisplay}",
            $"Copied adapter {adapter.Name}.");
    }

    private void CopyNdisProperty_Click(object sender, RoutedEventArgs e)
    {
        if (NdisPropertiesGrid.SelectedItem is not NdisPropertyRow property)
        {
            StatusText.Text = Loc.T("Select an NDIS property to copy.");
            return;
        }

        CopyToClipboard(
            $"Adapter\t{property.Adapter}\nDriver\t{property.Driver}\nProperty\t{property.Property}\nKeyword\t{property.Keyword}\nCurrent\t{property.Current}\nDefault\t{property.Default}\nType\t{property.Type}\nValid values\t{property.ValidValues}",
            $"Copied {property.Keyword}.");
    }

    private void CopyToClipboard(string text, string successMessage)
    {
        try
        {
            Clipboard.SetText(text);
            StatusText.Text = successMessage;
        }
        catch (ExternalException exception)
        {
            StatusText.Text = Loc.F($"Clipboard unavailable: {exception.Message}");
        }
    }
}
