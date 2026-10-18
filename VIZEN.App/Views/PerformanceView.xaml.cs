using System.Windows;
using System.Windows.Controls;
using VIZEN.App.ViewModels;

namespace VIZEN.App.Views;

public partial class PerformanceView : UserControl
{
    public PerformanceView()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => (DataContext as PerformanceViewModel)?.EnsureSystemDetected();
}
