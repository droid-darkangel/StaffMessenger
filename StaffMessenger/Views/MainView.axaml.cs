using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using StaffMessenger.ViewModels;

namespace StaffMessenger.Views;

public partial class MainView : UserControl
{
    private MainViewModel? _viewModel;
    private INotifyCollectionChanged? _messagesCollection;

    public MainView()
    {
        InitializeComponent();
        ComposerBox.AddHandler(KeyDownEvent, ComposerBox_OnKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => AttachViewModel(DataContext as MainViewModel);
        SettingsScroller.AttachedToVisualTree += (_, _) => QueueSettingsScrollReset();
    }

    private void AttachViewModel(MainViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
        }

        if (_messagesCollection is not null)
        {
            _messagesCollection.CollectionChanged -= MessagesCollection_OnCollectionChanged;
        }

        _viewModel = viewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += ViewModel_OnPropertyChanged;
            AttachMessagesCollection();
            if (_viewModel.IsSettingsVisible)
                QueueSettingsScrollReset();
        }
    }

    private void ViewModel_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveMessages)
            || e.PropertyName == nameof(MainViewModel.SelectedConversation))
        {
            AttachMessagesCollection();
            QueueMessagesScrollToEnd();
            return;
        }

        if (e.PropertyName != nameof(MainViewModel.IsSettingsVisible) || _viewModel?.IsSettingsVisible != true)
            return;

        QueueSettingsScrollReset();
    }

    private void QueueSettingsScrollReset()
    {
        Dispatcher.UIThread.Post(() => SettingsScroller.Offset = new Vector(0, 0));
        Dispatcher.UIThread.Post(() => SettingsScroller.Offset = new Vector(0, 0), DispatcherPriority.Background);
    }

    private void AttachMessagesCollection()
    {
        if (_messagesCollection is not null)
        {
            _messagesCollection.CollectionChanged -= MessagesCollection_OnCollectionChanged;
        }

        _messagesCollection = _viewModel?.ActiveMessages;
        if (_messagesCollection is not null)
        {
            _messagesCollection.CollectionChanged += MessagesCollection_OnCollectionChanged;
        }
    }

    private void MessagesCollection_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        QueueMessagesScrollToEnd();
    }

    private void QueueMessagesScrollToEnd()
    {
        Dispatcher.UIThread.Post(ScrollMessagesToEnd, DispatcherPriority.Background);
        Dispatcher.UIThread.Post(ScrollMessagesToEnd, DispatcherPriority.Render);
    }

    private void ScrollMessagesToEnd()
    {
        var y = Math.Max(0, MessagesScroller.Extent.Height - MessagesScroller.Viewport.Height);
        MessagesScroller.Offset = new Vector(MessagesScroller.Offset.X, y);
    }

    private void ComposerBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift) || !viewModel.SendByEnter)
            return;

        e.Handled = true;
        if (viewModel.SendDraftCommand.CanExecute(null))
            viewModel.SendDraftCommand.Execute(null);
    }
}
