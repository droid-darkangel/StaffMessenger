using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StaffMessenger.Models;

public sealed partial class ConversationItem : ObservableObject
{
    public ConversationItem(
        Guid id,
        string title,
        string subtitle,
        string initials,
        string lastPreview,
        string timeText,
        int unreadCount,
        bool isOnline,
        string accent,
        IEnumerable<MessageItem> messages)
    {
        Id = id;
        Title = title;
        Subtitle = subtitle;
        Initials = initials;
        LastPreview = lastPreview;
        TimeText = timeText;
        UnreadCount = unreadCount;
        IsOnline = isOnline;
        Accent = accent;
        Messages = new ObservableCollection<MessageItem>(messages);
    }

    public Guid Id { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public string Initials { get; }

    public string TimeText { get; private set; }

    public bool IsOnline { get; }

    public string Accent { get; }

    public ObservableCollection<MessageItem> Messages { get; }

    [ObservableProperty] private string _lastPreview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    private int _unreadCount;

    public bool HasUnread => UnreadCount > 0;

    public void Append(MessageItem message)
    {
        Messages.Add(message);
        LastPreview = message.Text;
        TimeText = message.TimeText;
        OnPropertyChanged(nameof(TimeText));
    }

    public void RefreshPreview()
    {
        var last = Messages.LastOrDefault();
        LastPreview = last?.Text ?? "Нет сообщений";
        TimeText = last?.TimeText ?? "";
        OnPropertyChanged(nameof(TimeText));
    }
}
