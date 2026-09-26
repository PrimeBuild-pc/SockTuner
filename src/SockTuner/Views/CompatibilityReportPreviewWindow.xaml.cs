using System.Windows;
using SockTuner.Services;

namespace SockTuner.Views;

public partial class CompatibilityReportPreviewWindow : Window
{
    public CompatibilityReportPreviewWindow(string report)
    {
        InitializeComponent();
        UiTranslator.Apply(this);
        ReportTextBox.Text = report;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
