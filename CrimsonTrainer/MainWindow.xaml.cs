using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using CrimsonTrainer.Hotkeys;
using CrimsonTrainer.Native;
using CrimsonTrainer.ViewModels;

namespace CrimsonTrainer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel(Dispatcher);
        DataContext = _viewModel;

        // Keep the newest log line in view.
        _viewModel.Log.CollectionChanged += (_, _) =>
            Dispatcher.InvokeAsync(LogScroll.ScrollToEnd, DispatcherPriority.Background);
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedSection))
            {
                BodyScroll.ScrollToTop();
                Dispatcher.InvokeAsync(LogScroll.ScrollToEnd, DispatcherPriority.Background);
            }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Dwm.ApplyDarkRoundedFrame(new WindowInteropHelper(this).Handle);
        _viewModel.AttachHotkeys(new HotkeyManager(this));
    }

    protected override void OnClosed(EventArgs e)
    {
        // Restores every patched byte while the game is still running.
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    /// <summary>Hotkey capture: the next key combination pressed anywhere in the window is bound.</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_viewModel.IsCapturing) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Escape:
                _viewModel.CancelCapture();
                return;
            case Key.Back or Key.Delete:
                _viewModel.CompleteCapture(null);
                return;
        }
        if (Hotkey.IsModifierKey(key)) return; // wait for the actual key

        _viewModel.CompleteCapture(new Hotkey(Keyboard.Modifiers, key));
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
