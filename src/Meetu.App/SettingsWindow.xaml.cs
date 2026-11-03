using System.Windows;
using Meetu.App.ViewModels;
using Microsoft.Win32;

namespace Meetu.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        this.viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void ChooseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Save meetings in" };
        if (dialog.ShowDialog(this) == true) viewModel.ChooseOutputDirectory(dialog.FolderName);
    }

    private void ChooseModel(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Summary model", Filter = "GGUF models (*.gguf)|*.gguf" };
        if (dialog.ShowDialog(this) == true) viewModel.ChooseSummaryModel(dialog.FileName);
    }

    private void UseDefaultModel(object sender, RoutedEventArgs e) => viewModel.ChooseSummaryModel(null);

    private void Save(object sender, RoutedEventArgs e)
    {
        if (viewModel.TrySave()) DialogResult = true;
    }
}
