using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;

namespace SolumanirSheetDesignerStudio.App.Dialogs
{
    public partial class ExportDialog : Window
    {
        public ExportDialog() => InitializeComponent();
        private void OnOk(object? sender, RoutedEventArgs e) => Close(true);
        private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
    }

    public partial class ImportPagesDialog : Window
    {
        public ImportPagesDialog() => InitializeComponent();
        private void OnOk(object? sender, RoutedEventArgs e) => Close(true);
        private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
    }

    public partial class SettingsDialog : Window
    {
        public SettingsDialog() => InitializeComponent();
        private void OnOk(object? sender, RoutedEventArgs e) => Close(true);
        private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
    }
}

namespace SolumanirSheetDesignerStudio.App.Services
{
    public static class Converters
    {
        /// <summary>Full opacity for true, dimmed for false (rows that are not part of the export).</summary>
        public static readonly IValueConverter DimWhenFalse = new FuncValueConverter<bool, double>(on => on ? 1.0 : 0.45);
    }
}
