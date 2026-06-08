using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StaffMessenger.Models;

public sealed partial class MessageItem : ObservableObject
{
    public MessageItem(
        Guid id,
        string sender,
        string text,
        DateTimeOffset sentAt,
        bool isOutgoing,
        string state,
        IEnumerable<AttachmentPreview>? attachments = null)
    {
        Id = id;
        Sender = sender;
        _text = text;
        SentAt = sentAt;
        IsOutgoing = isOutgoing;
        _state = state;
        Attachments = new ObservableCollection<AttachmentPreview>(attachments ?? []);
        Attachments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAttachments));
    }

    public Guid Id { get; }

    public string Sender { get; }

    public DateTimeOffset SentAt { get; }

    public bool IsOutgoing { get; }

    public bool IsIncoming => !IsOutgoing;

    public string TimeText => SentAt.ToLocalTime().ToString("HH:mm");

    public ObservableCollection<AttachmentPreview> Attachments { get; }

    public bool HasAttachments => Attachments.Count > 0;

    public bool CanShowActions => !IsDeleted && !IsRemoving;

    public double VisualOpacity => IsRemoving ? 0 : IsDeleted ? 0.58 : 1;

    [ObservableProperty] private string _text;

    [ObservableProperty] private string _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowActions))]
    [NotifyPropertyChangedFor(nameof(VisualOpacity))]
    private bool _isDeleted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowActions))]
    [NotifyPropertyChangedFor(nameof(VisualOpacity))]
    private bool _isRemoving;

    public void MarkDeletedForEveryone()
    {
        Text = "Сообщение удалено";
        State = "удалено";
        IsDeleted = true;
        Attachments.Clear();
    }

    public void StartRemoveForMe()
    {
        IsRemoving = true;
    }
}
