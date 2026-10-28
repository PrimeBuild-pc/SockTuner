using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace SockTuner.Views;

public partial class BusySpinner : UserControl
{
    public BusySpinner() => InitializeComponent();

    /// <summary>
    /// Turns only while visible, so a hidden spinner costs nothing. With Windows animations switched
    /// off the ring stays still; the text beside it still says the app is working.
    /// </summary>
    private void Spinner_VisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && SystemParameters.ClientAreaAnimation)
            Spin.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
        else
            Spin.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
    }
}
