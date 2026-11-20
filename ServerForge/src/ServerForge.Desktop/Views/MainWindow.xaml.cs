using System.Windows;
using ServerForge.Desktop.ViewModels;

namespace ServerForge.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.RefreshLiveDataCommand.ExecuteAsync(null);
    }
}
