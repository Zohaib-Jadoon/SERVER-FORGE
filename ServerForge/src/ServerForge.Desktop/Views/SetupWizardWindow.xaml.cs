using System.Windows;
using ServerForge.Desktop.ViewModels;

namespace ServerForge.Desktop.Views;

public partial class SetupWizardWindow : Window
{
    private readonly SetupWizardViewModel _viewModel;

    public SetupWizardWindow(SetupWizardViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.WizardCompleted += (_, succeeded) =>
        {
            DialogResult = succeeded;
            Close();
        };
    }

    private void OnAdminPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox pb)
            _viewModel.AdminPassword = pb.Password;
    }
}
