using System.Windows;
using ServerForge.Desktop.ViewModels;

namespace ServerForge.Desktop.Views;

public partial class SetupWizardWindow : Window
{
    public SetupWizardWindow(SetupWizardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.WizardCompleted += (_, succeeded) =>
        {
            DialogResult = succeeded;
            Close();
        };
    }
}
