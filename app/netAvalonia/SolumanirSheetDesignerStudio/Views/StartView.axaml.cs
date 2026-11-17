using Avalonia.Controls;
using SolumanirSheetDesignerStudio.App.ViewModels;

namespace SolumanirSheetDesignerStudio.App.Views;

public partial class StartView : UserControl
{
    public StartView()
    {
        InitializeComponent();
        // New / Open live on the shell view model (the window's data context), not on the start screen's own.
        NewButton.Click += (_, _) => Shell?.NewProjectCommand.Execute(null);
        OpenButton.Click += (_, _) => Shell?.OpenProjectCommand.Execute(null);
    }

    private MainViewModel? Shell => TopLevel.GetTopLevel(this)?.DataContext as MainViewModel;
}
