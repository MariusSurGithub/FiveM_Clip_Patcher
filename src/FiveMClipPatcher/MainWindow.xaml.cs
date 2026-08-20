using System.IO;
using System.Windows;
using System.Windows.Input;
using FiveMClipPatcher.ViewModels;

namespace FiveMClipPatcher;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
            return;

        var path = paths[0];
        if (File.Exists(path) || Directory.Exists(path))
            vm.SetDroppedPath(path);
    }

    private void ClipRow_OnActivate(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        if (sender is FrameworkElement fe && fe.DataContext is ClipItemViewModel clip)
            vm.ToggleClipCommand.Execute(clip);
    }
}
