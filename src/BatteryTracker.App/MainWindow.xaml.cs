using BatteryTracker.App.ViewModels;
using Microsoft.UI.Xaml;

namespace BatteryTracker.App;

public sealed partial class MainWindow : Window
{
    public DashboardViewModel ViewModel { get; }

    public MainWindow()
    {
        this.InitializeComponent();
        ViewModel = new DashboardViewModel();
        DataContext = ViewModel;
        this.Closed += async (_, _) => await ViewModel.DisposeAsync();
    }
}
