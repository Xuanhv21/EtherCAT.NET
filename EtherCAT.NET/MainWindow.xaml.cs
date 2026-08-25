using System.Collections.Specialized;
using System.Windows;

namespace EtherCAT.NET
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml. Constructs and owns the single
    /// <see cref="MainWindowViewModel"/> for this milestone (no DI container, per the implementation
    /// plan) and, since pure-binding auto-scroll of a <see cref="System.Windows.Controls.ListBox"/>
    /// is awkward, keeps the log list scrolled to its last entry via a small code-behind handler.
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
    }
}
