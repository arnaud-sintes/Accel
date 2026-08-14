namespace Glaude.App;

using System.Windows;

/// <summary>
/// The WPF shell's main window — scaffolding only (P1-T1b). Menu bar + GridSplitter-separated
/// placeholder panels (A/B/C/D/E), no data binding, no behavior. See <see cref="MainWindow.xaml"/>
/// for the layout and <see cref="App"/>'s doc comment for why this is not (yet) wired into the
/// real `glaude` startup path.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
