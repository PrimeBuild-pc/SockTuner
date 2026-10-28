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

/// <summary>Queueing QoS policies into the tuning plan.</summary>
public partial class MainWindow
{
    /// <summary>
    /// Queues a policy that marks one application's packets. Like every other write in this app it
    /// goes to the tuning plan rather than being applied here, so it is previewed, confirmed,
    /// verified by read-back and recorded where it can be rolled back.
    /// </summary>
    private void QosRunningApplication_DropDownOpened(object sender, EventArgs e)
    {
        var names = new List<string>();
        try
        {
            foreach (var process in System.Diagnostics.Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        names.Add(process.ProcessName);
                    }
                    catch (InvalidOperationException)
                    {
                        // The process exited between enumeration and reading its name.
                    }
                }
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            QosActionStatusText.Text = Loc.F($"Running applications could not be listed: {exception.Message}");
            return;
        }

        QosRunningApplicationComboBox.ItemsSource = NormalizeApplicationNames(names);
    }

    private void QosRunningApplication_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (QosRunningApplicationComboBox.SelectedItem is string application)
            QosApplicationText.Text = application;
    }

    private void QueueQosPolicy_Click(object sender, RoutedEventArgs e)
    {
        var application = QosApplicationText.Text.Trim();
        if (application.Length == 0)
        {
            QosActionStatusText.Text =
                Loc.T("Name the application to match. A policy with no application would mark everything this "
                + "machine sends, which is not what prioritising a game means.");
            return;
        }

        if (!int.TryParse(QosDscpText.Text.Trim(), out var dscp))
        {
            QosActionStatusText.Text = Loc.T("The DSCP value must be a number between 0 and 63.");
            return;
        }

        var value = new QosPolicyValue(
            dscp,
            application,
            (QosProtocolComboBox.SelectedItem as string) ?? "*",
            QosPortsText.Text.Trim() is { Length: > 0 } ports ? ports : "*");

        QueueQos(QosPolicySpecification.NameFor(application), value.Canonical, $"mark {application} with DSCP {dscp}");
    }

    private void QueueRemoveQosPolicy_Click(object sender, RoutedEventArgs e)
    {
        var application = QosApplicationText.Text.Trim();
        if (application.Length == 0)
        {
            QosActionStatusText.Text = Loc.T("Name the application whose policy should be removed.");
            return;
        }

        var name = QosPolicySpecification.NameFor(application);
        QueueQos(name, null, $"remove the policy {name}");
    }

    /// <summary>
    /// A null value queues the removal: absent is how "no policy" is expressed, and it is what a
    /// rollback of a created policy restores.
    /// </summary>
    private void QueueQos(string name, string? value, string description)
    {
        var accepted = TuningPlan.ProposeDeviceChanges(
            [new ChangeRequest(QosPolicySpecification.SettingId, name, value, ChangeSource.Manual)]);

        QosActionStatusText.Text = accepted == 1
            ? Loc.F($"Queued: {description}. Nothing is written until you preview and confirm it in the tuning plan. "
              + $"A mark only does something once your router is configured to act on it.")
            : Loc.F($"Refused: \u201c{name}\u201d is not a policy SockTuner can manage, or the values are not valid.");
        WriteLog("qos.queued", $"Policy={name}; Remove={value is null}; Accepted={accepted}.");
    }
}
