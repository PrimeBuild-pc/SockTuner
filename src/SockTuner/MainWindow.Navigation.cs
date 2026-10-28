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

/// <summary>Sections, the basic/advanced mode, search and keyboard shortcuts.</summary>
public partial class MainWindow
{
    /// <summary>
    /// F5 re-reads the inventory, Ctrl+F goes to the global search, Ctrl+K jumps to a section by
    /// name, and Ctrl+1..9 select the first nine destinations in navigation order. Twenty sections
    /// is more than a mouse should have to carry.
    /// </summary>
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var control = e.KeyboardDevice.Modifiers == System.Windows.Input.ModifierKeys.Control;

        if (e.Key == System.Windows.Input.Key.F5)
        {
            e.Handled = true;
            _ = RefreshInventoryAsync();
            return;
        }

        if (control && e.Key == System.Windows.Input.Key.F)
        {
            e.Handled = true;
            InventoryFilterText.SelectAll();
            InventoryFilterText.Focus();
            return;
        }

        if (control && e.Key == System.Windows.Input.Key.K)
        {
            e.Handled = true;
            ShowSectionJump();
            return;
        }

        // Ctrl+1..9 over the selectable tabs, in the order the navigation shows them.
        if (control && e.Key is >= System.Windows.Input.Key.D1 and <= System.Windows.Input.Key.D9)
        {
            var index = e.Key - System.Windows.Input.Key.D1;
            var selectable = SelectableTabs();
            if (index < selectable.Count)
            {
                e.Handled = true;
                InventoryTabs.SelectedItem = selectable[index];
            }
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => SelectTab("Preferences");

    private void AdvancedMode_Changed(object sender, RoutedEventArgs e)
    {
        var advanced = AdvancedModeCheck.IsChecked == true;
        if (_preferences.AdvancedMode != advanced)
            _preferences = AppPreferences.Update(preferences => preferences with { AdvancedMode = advanced });
        ApplyMode();
    }

    /// <summary>Hides advanced sections and any group header left with nothing under it, and never strands the user on a hidden page.</summary>
    private void ApplyMode()
    {
        var tabs = InventoryTabs.Items.OfType<System.Windows.Controls.TabItem>().ToList();
        // Preferences is reached from the gear, not from the list.
        var visible = SectionVisibility.Compute(
            [.. tabs.Select(tab => (SectionName(tab) ?? string.Empty, !tab.IsEnabled))],
            AdvancedModeCheck.IsChecked == true, BasicSections, "Preferences");
        for (var i = 0; i < tabs.Count; i++) tabs[i].Visibility = visible[i] ? Visibility.Visible : Visibility.Collapsed;
        if (InventoryTabs.SelectedItem is System.Windows.Controls.TabItem { Visibility: not Visibility.Visible } hidden && SectionName(hidden) != "Preferences")
            InventoryTabs.SelectedItem = tabs.First(tab => SectionName(tab) == "Dashboard");
    }

    private static string? SectionName(System.Windows.Controls.TabItem item) =>
        item.Tag as string ?? item.Header?.ToString();

    private void ShowSection(System.Windows.Controls.TabItem tab)
    {
        // A health finding or a search can point at an advanced page: open it rather than refuse.
        var top = tab.Parent is System.Windows.Controls.TabControl parent && !ReferenceEquals(parent, InventoryTabs) ? parent.Parent as System.Windows.Controls.TabItem : tab;
        if (top is { Visibility: not Visibility.Visible } && SectionName(top) != "Preferences" && AdvancedModeCheck.IsChecked != true) AdvancedModeCheck.IsChecked = true;
        if (tab.Parent is System.Windows.Controls.TabControl inner && !ReferenceEquals(inner, InventoryTabs))
        {
            inner.SelectedItem = tab;
            InventoryTabs.SelectedItem = inner.Parent;
            return;
        }
        InventoryTabs.SelectedItem = tab;
    }

    /// <summary>
    /// Jump to a section by typing part of its name. The global search box doubles as the input:
    /// typing there and pressing Ctrl+K again moves to the first section whose name matches, which
    /// keeps one text field rather than introducing a modal palette over a modal-free app.
    /// </summary>
    private void ShowSectionJump()
    {
        var typed = InventoryFilterText.Text.Trim();
        if (typed.Length == 0)
        {
            InventoryFilterText.Focus();
            StatusText.Text = Loc.T("Type part of a section name, then press Ctrl+K again to jump to it.");
            return;
        }

        var match = AllSections().FirstOrDefault(item =>
            item.Header?.ToString()?.Contains(typed, StringComparison.OrdinalIgnoreCase) == true);
        if (match is null)
        {
            StatusText.Text = Loc.F($"No section matching \u201c{typed}\u201d.");
            return;
        }

        ShowSection(match);
        StatusText.Text = Loc.F($"Jumped to {match.Header}.");
    }

    private void SelectTab(string header)
    {
        var tab = AllSections().FirstOrDefault(item => string.Equals(SectionName(item), header, StringComparison.Ordinal));
        if (tab is not null) ShowSection(tab);
    }

    private void OpenHealthSection_Click(object sender, RoutedEventArgs e)
    {
        if (HealthGrid.SelectedItem is not HealthFinding finding) return;

        var tab = AllSections().FirstOrDefault(item => string.Equals(
            SectionName(item)?.Replace("_", string.Empty), finding.Section, StringComparison.OrdinalIgnoreCase));
        if (tab is null)
        {
            StatusText.Text = Loc.F($"No section named {finding.Section}.");
            return;
        }

        ShowSection(tab);
        StatusText.Text = finding.Action;
    }

    /// <summary>
    /// Opens a reference in the default browser. The URL comes from the fixed list rather than from
    /// the control, and only https is launched, so a link can never become a way to start something
    /// local.
    /// </summary>
    private void OpenReference_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string url }) return;
        if (!ReferenceLinks.All.Any(link => string.Equals(link.Url, url, StringComparison.Ordinal)))
        {
            ReferenceStatusText.Text = Loc.T("That link is not one of the listed references.");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            ReferenceStatusText.Text = Loc.F($"Opened {url} in your browser.");
        }
        catch (Exception exception)
        {
            ReferenceStatusText.Text = Loc.F($"Could not open the link: {exception.Message}");
        }
    }
}
