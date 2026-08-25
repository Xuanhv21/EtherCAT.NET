using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EtherCAT.NET
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml. Constructs and owns the single
    /// <see cref="MainWindowViewModel"/> for this milestone (no DI container, per the implementation
    /// plan); keeps the log list scrolled to its last entry via a small code-behind handler (pure-
    /// binding auto-scroll of a <see cref="System.Windows.Controls.ListBox"/> is awkward); and wires
    /// the two Jog buttons' hold-to-move/release-to-stop interaction, since that dead-man behavior
    /// needs real mouse-capture handling that a <see cref="System.Windows.Input.ICommand"/> binding
    /// cannot express on its own.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainWindowViewModel _viewModel;

        public MainWindow()
        {
            InitializeComponent();

            _viewModel = new MainWindowViewModel();
            DataContext = _viewModel;

            _viewModel.LogEntries.CollectionChanged += OnLogEntriesChanged;
        }

        private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (LogListBox.Items.Count > 0)
            {
                LogListBox.ScrollIntoView(LogListBox.Items[^1]);
            }
        }

        /// <summary>
        /// Captures the mouse to whichever Jog button was pressed (so the matching mouse-up is
        /// reliably delivered even if the pointer drifts off the button while still held) and starts
        /// jogging in the direction its <c>Tag</c> ("-1" or "1") declares.
        /// </summary>
        private void JogButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button { Tag: string tag } button && int.TryParse(tag, out var direction))
            {
                button.CaptureMouse();
                _viewModel.BeginJog(direction);
            }
        }

        /// <summary>Releases mouse capture and stops jogging on a normal button-up.</summary>
        private void JogButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button button)
            {
                button.ReleaseMouseCapture();
            }

            _viewModel.EndJog();
        }

        /// <summary>
        /// Stops jogging whenever mouse capture is lost for ANY reason -- a normal release (already
        /// also handled by <see cref="JogButton_PreviewMouseLeftButtonUp"/>, so this is a no-op then)
        /// or an abnormal one (the window loses focus, Alt-Tab, a dialog steals capture) that would
        /// otherwise never deliver a mouse-up at all. This is the UI's own belt-and-suspenders on top
        /// of the engine's independent jog-heartbeat timeout -- not the only thing standing between a
        /// dropped event and a jog that never stops.
        /// </summary>
        private void JogButton_LostMouseCapture(object sender, MouseEventArgs e) => _viewModel.EndJog();
    }
}
