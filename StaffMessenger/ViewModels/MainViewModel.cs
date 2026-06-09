using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QRCoder;
using StaffMessenger.Contracts.Attachments;
using StaffMessenger.Contracts.Auth;
using StaffMessenger.Contracts.Conversations;
using StaffMessenger.Contracts.Messages;
using StaffMessenger.Crypto.Encryption;
using StaffMessenger.Crypto.Entropy;
using StaffMessenger.Models;
using StaffMessenger.Services;

namespace StaffMessenger.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private static readonly Regex EmailRegex = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
    private static readonly Regex E164PhoneRegex = new(@"^\+[1-9]\d{7,14}$", RegexOptions.Compiled);
    private static readonly Regex UsernameRegex = new(@"^[a-zA-Z0-9_]{3,32}$", RegexOptions.Compiled);
    private static readonly string[] BirthdayFormats = ["dd.MM.yyyy", "yyyy-MM-dd"];
    private readonly QuantumInspiredEntropyGenerator _entropy = new();
    private readonly EnvelopeEncryptionService _encryption;
    private readonly DeviceKeyPair _deviceKeyPair;
    private readonly MessengerApiClient _apiClient;
    private readonly ObservableCollection<MessageItem> _emptyMessages = new();
    private bool _suppressSettingsPersistence;
    private bool _suppressServerSettingsSync;
    private string? _profileAvatarReference;
    private string? _pendingTotpSecret;
    private CancellationTokenSource? _sessionRefreshCts;
    private CancellationTokenSource? _searchCts;
    private Guid? _yandexChallengeId;

    public MainViewModel()
    {
        _encryption = new EnvelopeEncryptionService(_entropy);
        _deviceKeyPair = _encryption.CreateDeviceKeyPair();
        _apiClient = new MessengerApiClient(ServerEndpoint.ApiBaseUri);
        _apiClient.MessageCreated += OnRealtimeMessageCreatedAsync;

        Conversations = new ObservableCollection<ConversationItem>(CreateConversations());
        SelectedConversation = Conversations.FirstOrDefault();
        SecuritySignals = new ObservableCollection<SecuritySignalItem>();
        EmojiOptions = new ObservableCollection<EmojiOption>(CreateEmojiOptions());
        PendingAttachments = new ObservableCollection<AttachmentPreview>();
        SearchResults = new ObservableCollection<ProfileSearchResult>();

        ApplyClientSettings(ClientSettingsStore.Load());

        var session = LocalSessionStore.Load();
        if (session is not null)
        {
            _apiClient.SetAccessToken(session.AccessToken);
            IsAuthenticated = true;
            AuthStatus = "Сессия восстановлена";
            NetworkStatus = "Проверяем серверную сессию";
            _ = LoadServerProfileAsync();
            StartSessionRefreshLoop();
        }
        else
        {
            IsAuthenticated = false;
            AuthStatus = "Вход";
            NetworkStatus = "Ожидание входа";
        }

        RefreshSecuritySignals();
    }

    public ObservableCollection<ConversationItem> Conversations { get; }

    public ObservableCollection<SecuritySignalItem> SecuritySignals { get; }

    public ObservableCollection<EmojiOption> EmojiOptions { get; }

    public ObservableCollection<AttachmentPreview> PendingAttachments { get; }

    public ObservableCollection<ProfileSearchResult> SearchResults { get; }

    public ObservableCollection<AuthIdentityDto> LinkedIdentities { get; } = new();

    public ObservableCollection<SessionItem> ActiveSessions { get; } = new();

    public ObservableCollection<string> ChatThemeOptions { get; } = new(["Светлая", "Темная", "Системная"]);

    public ObservableCollection<string> BubbleDensityOptions { get; } = new(["Компактная", "Комфортная", "Просторная"]);

    public string CurrentUserName => ProfileDisplayName;

    public string CurrentHandle => $"@{ProfileUsername}";

    public string LinkedIdentitiesSummary => LinkedIdentities.Count == 0
        ? "Способы входа не загружены"
        : string.Join(", ", LinkedIdentities.Select(identity => identity.Provider switch
        {
            AuthProvider.Email => $"Email: {identity.Identifier}",
            AuthProvider.Phone => $"Телефон: {identity.Identifier}",
            AuthProvider.YandexId => $"ЯндексID: {identity.Identifier}",
            _ => identity.Identifier
        }));

    public Color AppGradientStart => Color.Parse(CurrentTheme.GradientStart);

    public Color AppGradientMiddle => Color.Parse(CurrentTheme.GradientMiddle);

    public Color AppGradientEnd => Color.Parse(CurrentTheme.GradientEnd);

    public IBrush ShellBrush => Brush.Parse(CurrentTheme.Shell);

    public IBrush ChatSurfaceBrush => Brush.Parse(CurrentTheme.ChatSurface);

    public IBrush ComposerSurfaceBrush => Brush.Parse(CurrentTheme.ComposerSurface);

    public IBrush IncomingMessageBrush => Brush.Parse(CurrentTheme.IncomingBubble);

    public IBrush OutgoingMessageBrush => Brush.Parse(CurrentTheme.OutgoingBubble);

    public IBrush PrimaryAccentBrush => Brush.Parse(CurrentTheme.PrimaryAccent);

    public IBrush SettingsDividerBrush => Brush.Parse(CurrentTheme.Divider);

    public bool IsDarkTheme => ChatThemeName == "Темная";

    public Thickness MessagePadding => BubbleDensity switch
    {
        "Компактная" => new Thickness(9, 6),
        "Просторная" => new Thickness(24, 18),
        _ => new Thickness(14, 11)
    };

    public double MessageMaxWidth => BubbleDensity switch
    {
        "Компактная" => 480,
        "Просторная" => 820,
        _ => 640
    };

    public double MessageTextMaxWidth => Math.Max(240, MessageMaxWidth - MessagePadding.Left - MessagePadding.Right - 24);

    public double MessageSpacing => BubbleDensity switch
    {
        "Компактная" => 4,
        "Просторная" => 28,
        _ => 12
    };

    private ThemeColors CurrentTheme => GetThemeColors(ChatThemeName);

    public string DeviceKeyFingerprint => _deviceKeyPair.PublicKey.KeyId;

    public bool HasPendingAttachments => PendingAttachments.Count > 0;

    public bool HasSearchResults => SearchResults.Count > 0;

    public bool HasProfileAvatar => ProfileAvatar is not null;

    public bool HasNoProfileAvatar => ProfileAvatar is null;

    public bool HasSelectedConversation => SelectedConversation is not null;

    public ObservableCollection<MessageItem> ActiveMessages => SelectedConversation?.Messages ?? _emptyMessages;

    public bool IsSelectedConversationOnline => SelectedConversation?.IsOnline ?? false;

    public string SelectedConversationTitle => SelectedConversation?.Title ?? "Диалог";

    public string SelectedConversationSubtitle => SelectedConversation?.Subtitle ?? "нет выбранного чата";

    public string SelectedConversationInitials => SelectedConversation?.Initials ?? "SM";

    public string SelectedConversationAccent => SelectedConversation?.Accent ?? "#236DFF";

    public string ViewedProfileTitle => SelectedConversationTitle;

    public string ViewedProfileSubtitle => SelectedConversationSubtitle;

    public string ViewedProfileInitials => SelectedConversationInitials;

    public string ViewedProfileAccent => SelectedConversationAccent;

    public string ViewedProfileStatus => IsSelectedConversationOnline ? "online" : "offline";

    public string ViewedProfileAbout => SelectedConversation is null
        ? "Выберите чат, чтобы посмотреть профиль."
        : SelectedConversation.Subtitle.StartsWith("@", StringComparison.Ordinal)
            ? "Личный диалог. Профиль синхронизирован с сервером."
            : "Групповой чат. Участники и история загружаются с сервера.";

    public string ViewedProfileMessagesInfo => SelectedConversation is null
        ? "0 сообщений"
        : $"{SelectedConversation.Messages.Count} сообщений загружено";

    public string ProfileInitials => BuildInitials(ProfileDisplayName);

    public bool HasReplyPreview => ReplyToMessage is not null;

    public string ReplyPreview => ReplyToMessage is null
        ? ""
        : $"{ReplyToMessage.Sender}: {ReplyToMessage.Text}";

    public bool HasForwardSourceMessage => ForwardSourceMessage is not null;

    public bool IsAttachmentPreviewOpen => SelectedAttachment is not null;

    public Bitmap? SelectedAttachmentPreviewImage => SelectedAttachment?.PreviewImage;

    public string SelectedAttachmentFileName => SelectedAttachment?.FileName ?? "";

    public string SelectedAttachmentSizeText => SelectedAttachment?.SizeText ?? "";

    public string SelectedAttachmentKind => SelectedAttachment?.PreviewTitle ?? "";

    public string SelectedAttachmentLocalPath => SelectedAttachment?.LocalPath ?? "Локальный путь недоступен";

    public bool SelectedAttachmentHasImage => SelectedAttachment?.HasPreviewImage ?? false;

    public bool SelectedAttachmentIsVideo => SelectedAttachment?.IsVideo ?? false;

    public bool SelectedAttachmentIsDocument => SelectedAttachment?.ShowDocumentPreview ?? false;

    public bool IsPhoneValid => ValidatePhone(ProfilePhone, false).IsValid;

    public bool IsAuthPhoneValid => ValidatePhone(AuthPhone, true).IsValid;

    public bool IsEmailValid => EmailRegex.IsMatch(AuthEmail.Trim());

    public bool HasLinkedIdentities => LinkedIdentities.Count > 0;

    public bool HasActiveSessions => ActiveSessions.Count > 0;

    public bool HasTwoFactorQrCode => TwoFactorQrCode is not null;

    public bool HasYandexChallenge => YandexQrCode is not null && _yandexChallengeId.HasValue;

    public string ProfilePhoneValidationMessage => ValidatePhone(ProfilePhone, false).Message;

    public string AuthPhoneValidationMessage => ValidatePhone(AuthPhone, true).Message;

    public bool IsAuthPhoneMode => AuthMode == "Телефон";

    public bool IsAuthEmailMode => AuthMode == "Email";

    public bool IsAuthYandexMode => AuthMode == "ЯндексID";

    public bool IsPasswordAuthMode => IsAuthPhoneMode || IsAuthEmailMode;

    public bool IsAuthVisible => !IsAuthenticated;

    public bool IsMessengerVisible => IsAuthenticated && !IsSettingsOpen;

    public bool IsSettingsVisible => IsAuthenticated && IsSettingsOpen;

    public bool CanOpenProfileViewer => IsAuthenticated && SelectedConversation is not null;

    public bool IsProfileSettingsSection => SettingsSection == "Профиль";

    public bool IsAccountSettingsSection => SettingsSection == "Аккаунт";

    public bool IsInterfaceSettingsSection => SettingsSection == "Интерфейс";

    public bool IsChatSettingsSection => SettingsSection == "Чаты";

    public bool IsPrivacySettingsSection => SettingsSection == "Приватность";

    public bool IsNotificationsSettingsSection => SettingsSection == "Уведомления";

    public bool IsSecuritySettingsSection => SettingsSection == "Безопасность";

    public bool IsAboutSettingsSection => SettingsSection == "О программе";

    public bool IsDisplayNameValid => ProfileDisplayName.Trim().Length is >= 2 and <= 64;

    public bool IsUsernameValid => UsernameRegex.IsMatch(ProfileUsername.Trim().TrimStart('@'));

    public bool IsProfileBirthdayValid => ValidateBirthday(ProfileBirthday).IsValid;

    public bool IsProfileAboutValid => ProfileAbout.Length <= 240;

    public bool IsProfileValid => IsDisplayNameValid && IsUsernameValid && IsProfileBirthdayValid && IsPhoneValid && IsProfileAboutValid;

    public string DisplayNameValidationMessage => IsDisplayNameValid
        ? "Имя корректное"
        : "Имя должно быть от 2 до 64 символов";

    public string UsernameValidationMessage => IsUsernameValid
        ? "Юзернейм корректный"
        : "Только латиница, цифры и _, от 3 до 32 символов";

    public string ProfileBirthdayValidationMessage => ValidateBirthday(ProfileBirthday).Message;

    public string ProfileAboutValidationMessage => IsProfileAboutValid
        ? "Описание корректное"
        : "Описание не длиннее 240 символов";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedConversation))]
    [NotifyPropertyChangedFor(nameof(ActiveMessages))]
    [NotifyPropertyChangedFor(nameof(IsSelectedConversationOnline))]
    [NotifyPropertyChangedFor(nameof(SelectedConversationTitle))]
    [NotifyPropertyChangedFor(nameof(SelectedConversationSubtitle))]
    [NotifyPropertyChangedFor(nameof(SelectedConversationInitials))]
    [NotifyPropertyChangedFor(nameof(SelectedConversationAccent))]
    [NotifyPropertyChangedFor(nameof(ViewedProfileTitle))]
    [NotifyPropertyChangedFor(nameof(ViewedProfileSubtitle))]
    [NotifyPropertyChangedFor(nameof(ViewedProfileInitials))]
    [NotifyPropertyChangedFor(nameof(ViewedProfileAccent))]
    [NotifyPropertyChangedFor(nameof(ViewedProfileStatus))]
    [NotifyPropertyChangedFor(nameof(ViewedProfileAbout))]
    [NotifyPropertyChangedFor(nameof(ViewedProfileMessagesInfo))]
    [NotifyPropertyChangedFor(nameof(CanOpenProfileViewer))]
    private ConversationItem? _selectedConversation;

    [ObservableProperty] private string _draftMessage = "";

    [ObservableProperty] private bool _isEmojiPaletteOpen;

    [ObservableProperty] private bool _isSearchDropdownOpen;

    [ObservableProperty] private string _newChatQuery = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAuthVisible))]
    [NotifyPropertyChangedFor(nameof(IsMessengerVisible))]
    [NotifyPropertyChangedFor(nameof(IsSettingsVisible))]
    private bool _isAuthenticated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMessengerVisible))]
    [NotifyPropertyChangedFor(nameof(IsSettingsVisible))]
    private bool _isSettingsOpen;

    [ObservableProperty] private bool _isProfileViewerOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProfileSettingsSection))]
    [NotifyPropertyChangedFor(nameof(IsAccountSettingsSection))]
    [NotifyPropertyChangedFor(nameof(IsInterfaceSettingsSection))]
    [NotifyPropertyChangedFor(nameof(IsChatSettingsSection))]
    [NotifyPropertyChangedFor(nameof(IsPrivacySettingsSection))]
    [NotifyPropertyChangedFor(nameof(IsNotificationsSettingsSection))]
    [NotifyPropertyChangedFor(nameof(IsSecuritySettingsSection))]
    [NotifyPropertyChangedFor(nameof(IsAboutSettingsSection))]
    private string _settingsSection = "Профиль";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAuthPhoneMode))]
    [NotifyPropertyChangedFor(nameof(IsAuthEmailMode))]
    [NotifyPropertyChangedFor(nameof(IsAuthYandexMode))]
    [NotifyPropertyChangedFor(nameof(IsPasswordAuthMode))]
    private string _authMode = "Телефон";

    [ObservableProperty] private string _authLogin = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmailValid))]
    private string _authEmail = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAuthPhoneValid))]
    [NotifyPropertyChangedFor(nameof(AuthPhoneValidationMessage))]
    private string _authPhone = "";

    [ObservableProperty] private string _authCode = "";

    [ObservableProperty] private string _authPassword = "";

    [ObservableProperty] private string _authTotpCode = "";

    [ObservableProperty] private string _authYandexIdentifier = "";

    [ObservableProperty] private bool _isTotpStepVisible;

    [ObservableProperty] private string _authStatus = "Введите логин и пароль";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentUserName))]
    [NotifyPropertyChangedFor(nameof(ProfileInitials))]
    [NotifyPropertyChangedFor(nameof(IsDisplayNameValid))]
    [NotifyPropertyChangedFor(nameof(DisplayNameValidationMessage))]
    [NotifyPropertyChangedFor(nameof(IsProfileValid))]
    private string _profileDisplayName = "Пользователь";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentHandle))]
    [NotifyPropertyChangedFor(nameof(IsUsernameValid))]
    [NotifyPropertyChangedFor(nameof(UsernameValidationMessage))]
    [NotifyPropertyChangedFor(nameof(IsProfileValid))]
    private string _profileUsername = "user";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProfileAboutValid))]
    [NotifyPropertyChangedFor(nameof(ProfileAboutValidationMessage))]
    [NotifyPropertyChangedFor(nameof(IsProfileValid))]
    private string _profileAbout = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProfileBirthdayValid))]
    [NotifyPropertyChangedFor(nameof(ProfileBirthdayValidationMessage))]
    [NotifyPropertyChangedFor(nameof(IsProfileValid))]
    private string _profileBirthday = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPhoneValid))]
    [NotifyPropertyChangedFor(nameof(ProfilePhoneValidationMessage))]
    [NotifyPropertyChangedFor(nameof(IsProfileValid))]
    private string _profilePhone = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfileAvatar))]
    [NotifyPropertyChangedFor(nameof(HasNoProfileAvatar))]
    private Bitmap? _profileAvatar;

    [ObservableProperty] private string _profileAvatarHint = "Фото не выбрано";

    [ObservableProperty] private string _profileStatus = "Изменения не сохранены";

    [ObservableProperty] private bool _allowProfileSearch = true;

    [ObservableProperty] private bool _allowPhoneDiscovery;

    [ObservableProperty] private bool _showBirthday = true;

    [ObservableProperty] private bool _showLastSeen = true;

    [ObservableProperty] private bool _sendReadReceipts = true;

    [ObservableProperty] private bool _requireTwoFactor = true;

    [ObservableProperty] private bool _encryptMediaMetadata = true;

    [ObservableProperty] private double _chatFontSize = 14;

    [ObservableProperty] private string _chatThemeName = "Светлая";

    [ObservableProperty] private string _bubbleDensity = "Комфортная";

    [ObservableProperty] private bool _sendByEnter = true;

    [ObservableProperty] private bool _showMessageNotifications = true;

    [ObservableProperty] private bool _playIncomingSound = true;

    [ObservableProperty] private bool _showTextPreview;

    [ObservableProperty] private bool _twoFactorEnabled;

    [ObservableProperty] private string _twoFactorStatus = "2FA не настроена";

    [ObservableProperty] private string _twoFactorSetupSecret = "";

    [ObservableProperty] private string _twoFactorOtpAuthUri = "";

    [ObservableProperty] private string _twoFactorSetupCode = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTwoFactorQrCode))]
    private Bitmap? _twoFactorQrCode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasYandexChallenge))]
    private Bitmap? _yandexQrCode;

    [ObservableProperty] private string _yandexUserCode = "";

    [ObservableProperty] private string _yandexVerificationUrl = "";

    [ObservableProperty] private string _yandexQrStatus = "YandexID привязывается через QR-код и подтверждение на стороне Яндекса.";

    [ObservableProperty] private string _appProductName = "StaffMessenger";

    [ObservableProperty] private string _appVersion = "0.1.3";

    [ObservableProperty] private string _updateStatus = "Проверка обновлений не выполнялась";

    [ObservableProperty] private bool _updateAvailable;

    [ObservableProperty] private string _updateDownloadUrl = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReplyPreview))]
    [NotifyPropertyChangedFor(nameof(ReplyPreview))]
    private MessageItem? _replyToMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasForwardSourceMessage))]
    private MessageItem? _forwardSourceMessage;

    [ObservableProperty] private bool _isForwardPickerOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAttachmentPreviewOpen))]
    [NotifyPropertyChangedFor(nameof(SelectedAttachmentPreviewImage))]
    [NotifyPropertyChangedFor(nameof(SelectedAttachmentFileName))]
    [NotifyPropertyChangedFor(nameof(SelectedAttachmentSizeText))]
    [NotifyPropertyChangedFor(nameof(SelectedAttachmentKind))]
    [NotifyPropertyChangedFor(nameof(SelectedAttachmentLocalPath))]
    [NotifyPropertyChangedFor(nameof(SelectedAttachmentHasImage))]
    [NotifyPropertyChangedFor(nameof(SelectedAttachmentIsVideo))]
    [NotifyPropertyChangedFor(nameof(SelectedAttachmentIsDocument))]
    private AttachmentPreview? _selectedAttachment;

    [ObservableProperty] private string _networkStatus = $"API: {ServerEndpoint.ApiBaseUrl}";

    [RelayCommand]
    private async Task SendDraft()
    {
        if (SelectedConversation is null)
        {
            return;
        }

        var text = DraftMessage.Trim();
        if (string.IsNullOrWhiteSpace(text) && PendingAttachments.Count == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            text = "Вложение";
        }

        if (ReplyToMessage is not null)
        {
            text = $"↪ {ReplyToMessage.Sender}: {ReplyToMessage.Text}\n{text}";
        }

        try
        {
            var uploadedAttachmentIds = await UploadPendingAttachmentsAsync();
            var envelope = _encryption.EncryptText(
                text,
                _deviceKeyPair.PublicKey,
                SelectedConversation.Id.ToString("D"));

            var dto = await _apiClient.SendMessageAsync(
                SelectedConversation.Id,
                new SendMessageRequest(
                    Guid.NewGuid(),
                    text,
                    envelope,
                    uploadedAttachmentIds,
                    ReplyToMessage?.Id));

            SelectedConversation.Append(MapMessage(dto));
            SelectedConversation.UnreadCount = 0;
            DraftMessage = "";
            ReplyToMessage = null;
            PendingAttachments.Clear();
            OnPropertyChanged(nameof(HasPendingAttachments));

            NetworkStatus = "Сообщение отправлено и сохранено";
            RefreshSecuritySignals();
        }
        catch (HttpRequestException exception)
        {
            NetworkStatus = $"Сообщение не отправлено: {exception.Message}";
        }
        catch (IOException exception)
        {
            NetworkStatus = $"Вложение не прочитано: {exception.Message}";
        }
    }

    [RelayCommand]
    private void AppendEmoji(string value)
    {
        DraftMessage += value;
        IsEmojiPaletteOpen = false;
    }

    [RelayCommand]
    private void SetAuthMode(string mode)
    {
        AuthMode = mode;
        AuthStatus = mode switch
        {
            "ЯндексID" => "Вход через ЯндексID будет открыт в браузере",
            "Телефон" => "Введите телефон в международном формате",
            "Email" => "Введите корпоративную или личную почту",
            _ => "Выберите способ входа"
        };
    }

    [RelayCommand]
    private async Task SignIn()
    {
        if (!ValidateAuthInput())
            return;

        await CompleteServerSignInAsync(false);
    }

    [RelayCommand]
    private async Task Register()
    {
        if (!ValidateAuthInput())
            return;

        await CompleteServerSignInAsync(true);
    }

    [RelayCommand]
    private void SignOut()
    {
        _sessionRefreshCts?.Cancel();
        LocalSessionStore.Clear();
        IsSettingsOpen = false;
        IsAuthenticated = false;
        ClearAuthInputs();
        AuthStatus = "Сессия завершена. Войдите снова.";
        NetworkStatus = "Ожидание входа";
    }

    [RelayCommand]
    private void OpenSettings()
    {
        IsProfileViewerOpen = false;
        SettingsSection = "Профиль";
        IsSettingsOpen = true;
        NetworkStatus = "Открыты настройки";
    }

    [RelayCommand]
    private void CloseSettings()
    {
        IsSettingsOpen = false;
        NetworkStatus = "Чат открыт";
    }

    [RelayCommand]
    private void OpenProfileViewer()
    {
        if (!CanOpenProfileViewer)
        {
            return;
        }

        IsProfileViewerOpen = true;
        NetworkStatus = $"Открыт профиль: {SelectedConversationTitle}";
    }

    [RelayCommand]
    private void CloseProfileViewer()
    {
        IsProfileViewerOpen = false;
    }

    [RelayCommand]
    private void SelectSettingsSection(string section)
    {
        SettingsSection = section;
        NetworkStatus = $"Настройки: {section}";
        if (section == "Безопасность")
        {
            _ = LoadSessions();
        }
        else if (section == "О программе")
        {
            _ = CheckForUpdates();
        }
    }

    [RelayCommand]
    private async Task SaveProfile()
    {
        if (!IsProfileValid)
        {
            ProfileStatus = "Исправьте поля профиля перед сохранением";
            return;
        }

        if (!await EnsureServerSessionAsync())
        {
            ProfileStatus = "Сначала войдите в аккаунт";
            return;
        }

        try
        {
            var profile = await _apiClient.UpdateProfileAsync(new UpdateProfileRequest(
                ProfileDisplayName.Trim(),
                ProfileUsername.Trim().TrimStart('@'),
                _profileAvatarReference,
                ProfileAbout.Trim(),
                ParseBirthdayDate(ProfileBirthday),
                NormalizePhone(ProfilePhone)));

            ApplyServerProfile(profile);
            ProfileStatus = "Профиль сохранен в PostgreSQL";
            NetworkStatus = "Профиль обновлен";
        }
        catch (HttpRequestException exception)
        {
            ProfileStatus = $"Не удалось сохранить профиль: {exception.Message}";
        }
        catch (InvalidOperationException exception)
        {
            ProfileStatus = $"Не удалось сохранить профиль: {exception.Message}";
        }
    }

    private bool ValidateAuthInput()
    {
        var login = AuthLogin.Trim();
        if (string.IsNullOrWhiteSpace(login))
        {
            AuthStatus = "Введите телефон или email";
            return false;
        }

        if (string.IsNullOrWhiteSpace(AuthPassword))
        {
            AuthStatus = "Введите пароль";
            return false;
        }

        var provider = InferAuthProvider(login);
        var identifier = NormalizeLoginIdentifier(provider, login);
        if (!IsLoginIdentifierValid(provider, identifier))
        {
            AuthStatus = "Логин должен быть email или телефоном в формате +79991234567";
            return false;
        }

        if (IsTotpStepVisible && string.IsNullOrWhiteSpace(AuthTotpCode))
        {
            AuthStatus = "Введите одноразовый PIN 2FA";
            return false;
        }

        AuthStatus = "Данные готовы к проверке";
        return true;
    }

    private async Task CompleteServerSignInAsync(bool isRegistration)
    {
        var provider = InferAuthProvider(AuthLogin);
        var identifier = NormalizeLoginIdentifier(provider, AuthLogin);

        try
        {
            AuthStatus = "Проверяем учетные данные на сервере...";
            AuthResponse auth;
            if (isRegistration)
            {
                auth = await _apiClient.RegisterAsync(new RegisterRequest(
                    string.IsNullOrWhiteSpace(ProfileDisplayName) ? "StaffMessenger user" : ProfileDisplayName.Trim(),
                    BuildUsername(identifier),
                    provider,
                    identifier,
                    AuthPassword,
                    _deviceKeyPair.PublicKey));
            }
            else
            {
                auth = await _apiClient.LoginAsync(new LoginRequest(
                    provider,
                    identifier,
                    AuthPassword,
                    IsTotpStepVisible ? AuthTotpCode.Trim() : null,
                    _deviceKeyPair.PublicKey));
            }

            SaveToken(auth.AccessToken);
            ClearAuthInputs();

            ProfileDisplayName = auth.DisplayName;
            ProfileUsername = auth.Handle;
            IsAuthenticated = true;
            AuthStatus = isRegistration
                ? "Аккаунт создан, сессия сохранена"
                : "Вход выполнен, сессия сохранена";
            NetworkStatus = "Сессия активна";

            await LoadServerProfileAsync();
            await _apiClient.ConnectRealtimeAsync();
            StartSessionRefreshLoop();
        }
        catch (HttpRequestException exception)
        {
            AuthStatus = $"Сервер авторизации недоступен: {exception.Message}";
            NetworkStatus = "Ошибка подключения к API";
        }
        catch (TwoFactorRequiredException)
        {
            IsTotpStepVisible = true;
            AuthTotpCode = "";
            AuthStatus = "Введите одноразовый PIN 2FA.";
            NetworkStatus = "Ожидание 2FA";
        }
        catch (InvalidOperationException exception)
        {
            AuthStatus = $"Ошибка авторизации: {exception.Message}";
            NetworkStatus = "Ошибка авторизации";
        }
    }

    private async Task LoadServerProfileAsync()
    {
        try
        {
            var profile = await _apiClient.GetProfileAsync();
            ApplyServerProfile(profile);
            ProfileStatus = "Профиль загружен";
            NetworkStatus = "Профиль синхронизирован";
            await LoadServerConversationsAsync();
            await _apiClient.ConnectRealtimeAsync();
        }
        catch (HttpRequestException exception)
        {
            ProfileStatus = $"Профиль не загружен: {exception.Message}";
            LocalSessionStore.Clear();
            IsAuthenticated = false;
            IsSettingsOpen = false;
            AuthStatus = "Сессия истекла, войдите снова";
        }
        catch (InvalidOperationException exception)
        {
            ProfileStatus = $"Профиль не загружен: {exception.Message}";
        }
    }

    private async Task<bool> EnsureServerSessionAsync()
    {
        if (_apiClient.AccessToken is not null)
        {
            try
            {
                await _apiClient.GetProfileAsync();
                return true;
            }
            catch (HttpRequestException)
            {
                LocalSessionStore.Clear();
            }
        }

        return false;
    }

    private void ApplyServerProfile(UserProfileDto profile)
    {
        ProfileDisplayName = profile.DisplayName;
        ProfileUsername = profile.Handle;
        ProfileAbout = profile.About ?? "";
        ProfileBirthday = FormatBirthday(profile.BirthDate);
        ProfilePhone = profile.Phone
                       ?? profile.Identities.FirstOrDefault(identity => identity.Provider == AuthProvider.Phone)?.Identifier
                       ?? "";
        _profileAvatarReference = profile.AvatarUrl;
        TwoFactorEnabled = profile.TwoFactorEnabled;
        RequireTwoFactor = profile.TwoFactorEnabled;
        TwoFactorStatus = profile.TwoFactorEnabled
            ? "2FA включена"
            : "2FA выключена";

        LinkedIdentities.Clear();
        foreach (var identity in profile.Identities)
        {
            LinkedIdentities.Add(identity);
        }

        OnPropertyChanged(nameof(LinkedIdentitiesSummary));
        OnPropertyChanged(nameof(HasLinkedIdentities));

        _suppressServerSettingsSync = true;
        try
        {
            AllowProfileSearch = profile.Privacy.AllowProfileSearch;
            AllowPhoneDiscovery = profile.Privacy.AllowPhoneDiscovery;
            ShowBirthday = profile.Privacy.ShowBirthday;
            ShowLastSeen = profile.Privacy.ShowLastSeen;
            SendReadReceipts = profile.Privacy.SendReadReceipts;
            EncryptMediaMetadata = profile.Privacy.EncryptMediaMetadata;
            ShowMessageNotifications = profile.Notifications.ShowMessageNotifications;
            PlayIncomingSound = profile.Notifications.PlayIncomingSound;
            ShowTextPreview = profile.Notifications.ShowTextPreview;
        }
        finally
        {
            _suppressServerSettingsSync = false;
        }

        if (!string.IsNullOrWhiteSpace(profile.AvatarUrl) && File.Exists(profile.AvatarUrl))
        {
            try
            {
                using var stream = File.OpenRead(profile.AvatarUrl);
                ProfileAvatar = new Bitmap(stream);
                ProfileAvatarHint = Path.GetFileName(profile.AvatarUrl);
            }
            catch (IOException)
            {
                ProfileAvatarHint = profile.AvatarUrl;
            }
        }
        else
        {
            ProfileAvatarHint = string.IsNullOrWhiteSpace(profile.AvatarUrl)
                ? "Фото не выбрано"
                : profile.AvatarUrl;
        }
    }

    private static void SaveToken(string accessToken)
        => LocalSessionStore.Save(new LocalSession(accessToken));

    private void ClearAuthInputs()
    {
        AuthLogin = "";
        AuthEmail = "";
        AuthPhone = "";
        AuthYandexIdentifier = "";
        AuthPassword = "";
        AuthCode = "";
        AuthTotpCode = "";
        IsTotpStepVisible = false;
    }

    private async Task OnRealtimeMessageCreatedAsync(StaffMessenger.Contracts.Realtime.MessageCreatedEvent payload)
    {
        var message = payload.Message;
        var conversation = Conversations.FirstOrDefault(item => item.Id == message.ConversationId);
        if (conversation is not null && message.SenderDisplayName != CurrentUserName)
        {
            conversation.Append(MapMessage(message));
            if (SelectedConversation?.Id == conversation.Id)
            {
                conversation.UnreadCount = 0;
                await _apiClient.MarkConversationReadAsync(conversation.Id);
            }
            else
                conversation.UnreadCount += 1;
        }

        if (message.SenderDisplayName == CurrentUserName)
            return;

        NetworkStatus = ShowTextPreview
            ? $"Новое сообщение: {message.PlainPreview}"
            : "Новое сообщение";

        if (ShowMessageNotifications)
        {
            var body = ShowTextPreview ? message.PlainPreview : "Новое сообщение";
            await DesktopNotificationService.ShowAsync(message.SenderDisplayName, body);
        }
    }

    private async Task LoadServerConversationsAsync()
    {
        try
        {
            var summaries = await _apiClient.GetConversationsAsync();
            Conversations.Clear();

            var saved = await _apiClient.GetSavedConversationAsync();
            Conversations.Add(MapConversation(saved));
            var announcements = await _apiClient.GetAnnouncementConversationAsync();
            Conversations.Add(MapConversation(announcements));

            foreach (var summary in summaries) 
                if (summary.Id != saved.Id && summary.Id != announcements.Id)
                    Conversations.Add(MapConversation(summary));

            SelectedConversation = Conversations.FirstOrDefault();
            if (SelectedConversation is not null)
            {
                await LoadMessagesForConversationAsync(SelectedConversation);
                await MarkSelectedConversationReadAsync(SelectedConversation);
            }

            NetworkStatus = Conversations.Count == 0
                ? "Серверные чаты не найдены"
                : "Чаты синхронизированы";
        }
        catch (HttpRequestException exception)
        {
            NetworkStatus = $"Чаты не синхронизированы: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task OpenSavedMessages()
    {
        if (_apiClient.AccessToken is null)
            return;

        try
        {
            var summary = await _apiClient.GetSavedConversationAsync();
            var existing = Conversations.FirstOrDefault(conversation => conversation.Id == summary.Id);
            if (existing is null)
            {
                existing = MapConversation(summary);
                Conversations.Insert(0, existing);
            }

            SelectedConversation = existing;
            NetworkStatus = "Открыты сохраненные сообщения";
        }
        catch (HttpRequestException exception)
        {
            NetworkStatus = $"Сохраненные сообщения не открыты: {exception.Message}";
        }
    }

    private async Task LoadMessagesForConversationAsync(ConversationItem conversation)
    {
        try
        {
            var messages = await _apiClient.GetMessagesAsync(conversation.Id);
            conversation.Messages.Clear();
            foreach (var message in messages)
                conversation.Messages.Add(MapMessage(message));

            conversation.RefreshPreview();
            await _apiClient.JoinConversationAsync(conversation.Id);
        }
        catch (HttpRequestException exception)
        {
            NetworkStatus = $"Сообщения не загружены: {exception.Message}";
        }
    }

    private async Task MarkSelectedConversationReadAsync(ConversationItem conversation)
    {
        if (_apiClient.AccessToken is null)
            return;

        conversation.UnreadCount = 0;
        await _apiClient.MarkConversationReadAsync(conversation.Id);
    }

    private async Task<IReadOnlyList<Guid>> UploadPendingAttachmentsAsync()
    {
        var ids = new List<Guid>();
        foreach (var attachment in PendingAttachments)
        {
            if (attachment.LocalPath is null || !File.Exists(attachment.LocalPath))
                continue;

            await using var stream = File.OpenRead(attachment.LocalPath);
            var uploaded = await _apiClient.UploadAttachmentAsync(
                stream,
                attachment.FileName,
                attachment.ContentType,
                attachment.IsImage ? AttachmentKind.Image : attachment.IsVideo ? AttachmentKind.Video : AttachmentKind.File);
            ids.Add(uploaded.Id);
        }

        return ids;
    }

    private ConversationItem MapConversation(ConversationSummary summary)
    {
        var peer = summary.Members.FirstOrDefault(member => member.Handle != ProfileUsername);
        var initials = BuildInitials(summary.Title);
        var subtitle = summary.Kind switch
        {
            ConversationKind.Saved => "личный приватный чат",
            ConversationKind.Announcement => "системные обновления",
            ConversationKind.Direct when peer is not null => $"@{peer.Handle}",
            _ => $"{summary.Members.Count} участников"
        };

        var title = summary.Kind switch
        {
            ConversationKind.Saved => "Сохраненные сообщения",
            ConversationKind.Announcement => "NewSunshine",
            _ => string.IsNullOrWhiteSpace(summary.Title) ? "Диалог" : summary.Title
        };

        return new ConversationItem(
            summary.Id,
            title,
            subtitle,
            summary.Kind == ConversationKind.Saved ? "SM" : summary.Kind == ConversationKind.Announcement ? "NS" : initials,
            summary.LastPreview,
            summary.UpdatedAt.ToLocalTime().ToString("HH:mm"),
            summary.UnreadCount,
            peer?.IsOnline ?? false,
            summary.Kind == ConversationKind.Saved ? "#162033" : summary.Kind == ConversationKind.Announcement ? "#D97706" : PickAccent(summary.Id.ToString("N")),
            []);
    }

    private MessageItem MapMessage(MessageDto message)
    {
        return new MessageItem(
            message.Id,
            message.SenderDisplayName,
            message.PlainPreview,
            message.SentAt,
            message.SenderDisplayName == CurrentUserName,
            message.State switch
            {
                MessageDeliveryState.Delivered => "получено",
                MessageDeliveryState.Read => "прочитано",
                MessageDeliveryState.Deleted => "удалено",
                _ => "отправлено"
            },
            message.Attachments.Select(MapAttachment));
    }

    private AttachmentPreview MapAttachment(AttachmentDto attachment)
    {
        return new AttachmentPreview(
            attachment.Id,
            attachment.Kind switch
            {
                AttachmentKind.Image => "Фото",
                AttachmentKind.Video => "Видео",
                _ => "Документ"
            },
            attachment.FileName,
            FormatSize(attachment.SizeBytes),
            attachment.Kind == AttachmentKind.Image ? "#4A8CFF" : attachment.Kind == AttachmentKind.Video ? "#FF6B8A" : "#7C5CFF",
            null,
            attachment.ContentType,
            null,
            attachment.DownloadUrl);
    }

    [RelayCommand]
    private async Task SendIdentityVerificationCode()
    {
        if (!IsAuthenticated)
        {
            AuthStatus = "Сначала войдите в аккаунт";
            return;
        }

        var provider = AuthMode switch
        {
            "Телефон" => AuthProvider.Phone,
            "Email" => AuthProvider.Email,
            _ => AuthProvider.YandexId
        };

        if (provider == AuthProvider.YandexId)
        {
            await StartYandexQrLink();
            return;
        }

        var identifier = provider == AuthProvider.Phone
            ? NormalizePhone(AuthPhone)
            : AuthEmail.Trim().ToLowerInvariant();

        if (!IsLoginIdentifierValid(provider, identifier))
        {
            AuthStatus = provider == AuthProvider.Phone
                ? "Введите телефон в формате +79991234567"
                : "Введите корректную электронную почту";
            return;
        }

        try
        {
            var verification = await _apiClient.StartIdentityVerificationAsync(
                new StartIdentityVerificationRequest(provider, identifier));
            AuthCode = verification.DevelopmentCode ?? "";
            AuthStatus = verification.DevelopmentCode is null
                ? "Одноразовый код отправлен"
                : $"Одноразовый код отправлен: {verification.DevelopmentCode}";
        }
        catch (HttpRequestException exception)
        {
            AuthStatus = $"Код не отправлен: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task LinkCurrentIdentity()
    {
        if (!IsAuthenticated)
        {
            AuthStatus = "Сначала войдите в аккаунт";
            return;
        }

        var provider = AuthMode switch
        {
            "Телефон" => AuthProvider.Phone,
            "Email" => AuthProvider.Email,
            "ЯндексID" => AuthProvider.YandexId,
            _ => AuthProvider.Email
        };
        var identifier = provider switch
        {
            AuthProvider.Phone => NormalizePhone(AuthPhone),
            AuthProvider.Email => AuthEmail.Trim().ToLowerInvariant(),
            AuthProvider.YandexId => AuthYandexIdentifier.Trim(),
            _ => AuthEmail.Trim()
        };

        if (provider is AuthProvider.Phone or AuthProvider.Email && !IsLoginIdentifierValid(provider, identifier))
        {
            AuthStatus = provider == AuthProvider.Phone
                ? "Введите телефон в формате +79991234567"
                : "Введите корректную электронную почту";
            return;
        }

        if (provider == AuthProvider.YandexId && string.IsNullOrWhiteSpace(identifier))
        {
            await StartYandexQrLink();
            return;
        }

        if (provider is AuthProvider.Phone or AuthProvider.Email && string.IsNullOrWhiteSpace(AuthCode))
        {
            AuthStatus = "Сначала подтвердите способ входа одноразовым кодом";
            return;
        }

        try
        {
            var profile = await _apiClient.LinkIdentityAsync(new LinkIdentityRequest(
                provider,
                identifier,
                provider is AuthProvider.Email or AuthProvider.Phone ? AuthPassword : null,
                AuthCode));
            ApplyServerProfile(profile);
            AuthStatus = "Способ входа привязан к текущему профилю";
            AuthPassword = "";
            AuthCode = "";
        }
        catch (HttpRequestException exception)
        {
            AuthStatus = $"Не удалось привязать способ входа: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task StartYandexQrLink()
    {
        if (!IsAuthenticated)
        {
            AuthStatus = "Сначала войдите в аккаунт";
            return;
        }

        try
        {
            var challenge = await _apiClient.StartYandexIdentityLinkAsync();
            _yandexChallengeId = challenge.ChallengeId;
            YandexUserCode = challenge.UserCode;
            YandexVerificationUrl = challenge.VerificationUrl;
            YandexQrCode = CreateQrBitmap(challenge.QrPayload);
            YandexQrStatus = "Отсканируйте QR, введите код на странице Яндекса и нажмите Проверить.";
            AuthStatus = "YandexID ожидает подтверждения через QR";
        }
        catch (HttpRequestException exception)
        {
            YandexQrStatus = $"YandexID недоступен: {exception.Message}";
            AuthStatus = YandexQrStatus;
        }
        catch (InvalidOperationException exception)
        {
            YandexQrStatus = $"YandexID не настроен: {exception.Message}";
            AuthStatus = YandexQrStatus;
        }
    }

    [RelayCommand]
    private async Task CompleteYandexQrLink()
    {
        if (_yandexChallengeId is null)
        {
            YandexQrStatus = "Сначала создайте QR для YandexID.";
            return;
        }

        try
        {
            var profile = await _apiClient.CompleteYandexIdentityLinkAsync(new YandexQrCompleteRequest(
                _yandexChallengeId.Value,
                null,
                _deviceKeyPair.PublicKey));
            if (profile is null)
            {
                YandexQrStatus = "Яндекс ещё ожидает подтверждения. Проверьте код и повторите.";
                return;
            }

            ApplyServerProfile(profile);
            _yandexChallengeId = null;
            YandexQrCode = null;
            YandexUserCode = "";
            YandexVerificationUrl = "";
            YandexQrStatus = "YandexID привязан к текущему профилю.";
            AuthStatus = "YandexID привязан";
        }
        catch (HttpRequestException exception)
        {
            YandexQrStatus = $"YandexID не привязан: {exception.Message}";
            AuthStatus = YandexQrStatus;
        }
    }

    [RelayCommand]
    private async Task UnlinkIdentity(AuthIdentityDto identity)
    {
        try
        {
            var profile = await _apiClient.UnlinkIdentityAsync(new UnlinkIdentityRequest(
                identity.Provider,
                identity.Identifier));
            ApplyServerProfile(profile);
            AuthStatus = "Способ входа отвязан";
        }
        catch (HttpRequestException exception)
        {
            AuthStatus = $"Способ входа не отвязан: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task SetupTwoFactor()
    {
        try
        {
            var setup = await _apiClient.SetupTotpAsync();
            _pendingTotpSecret = setup.Secret;
            TwoFactorSetupSecret = setup.Secret;
            TwoFactorOtpAuthUri = setup.OtpAuthUri;
            TwoFactorQrCode = CreateQrBitmap(setup.OtpAuthUri);
            TwoFactorStatus = $"Добавьте ключ в TOTP-приложение для телефона {setup.Phone}, затем введите PIN";
        }
        catch (HttpRequestException exception)
        {
            TwoFactorStatus = $"Не удалось подготовить 2FA: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task EnableTwoFactor()
    {
        if (string.IsNullOrWhiteSpace(_pendingTotpSecret))
        {
            TwoFactorStatus = "Сначала создайте одноразовый ключ";
            return;
        }

        try
        {
            var profile = await _apiClient.EnableTotpAsync(new EnableTotpRequest(
                _pendingTotpSecret,
                NormalizePhone(ProfilePhone),
                TwoFactorSetupCode.Trim()));
            ApplyServerProfile(profile);
            _pendingTotpSecret = null;
            TwoFactorSetupSecret = "";
            TwoFactorOtpAuthUri = "";
            TwoFactorSetupCode = "";
            TwoFactorQrCode = null;
            TwoFactorStatus = "2FA включена";
        }
        catch (HttpRequestException exception)
        {
            TwoFactorStatus = $"PIN не принят: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task DisableTwoFactor()
    {
        try
        {
            var profile = await _apiClient.DisableTotpAsync(new DisableTotpRequest(TwoFactorSetupCode.Trim()));
            ApplyServerProfile(profile);
            _pendingTotpSecret = null;
            TwoFactorSetupSecret = "";
            TwoFactorOtpAuthUri = "";
            TwoFactorSetupCode = "";
            TwoFactorQrCode = null;
            TwoFactorStatus = "2FA выключена";
        }
        catch (HttpRequestException exception)
        {
            TwoFactorStatus = $"Не удалось выключить 2FA: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task LoadSessions()
    {
        if (_apiClient.AccessToken is null)
            return;

        try
        {
            var sessions = await _apiClient.GetSessionsAsync();
            ActiveSessions.Clear();
            foreach (var session in sessions)
                ActiveSessions.Add(SessionItem.FromDto(session));

            OnPropertyChanged(nameof(HasActiveSessions));
            NetworkStatus = "Сессии загружены";
        }
        catch (HttpRequestException exception)
        {
            NetworkStatus = $"Сессии не загружены: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task RevokeSession(SessionItem session)
    {
        try
        {
            await _apiClient.RevokeSessionAsync(session.Id);
            if (session.IsCurrent)
            {
                SignOut();
                return;
            }

            await LoadSessions();
            NetworkStatus = "Сессия закрыта";
        }
        catch (HttpRequestException exception)
        {
            NetworkStatus = $"Сессия не закрыта: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task RevokeOtherSessions()
    {
        try
        {
            await _apiClient.RevokeOtherSessionsAsync();
            await LoadSessions();
            NetworkStatus = "Остальные сессии закрыты";
        }
        catch (HttpRequestException exception)
        {
            NetworkStatus = $"Сессии не закрыты: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        try
        {
            var info = await _apiClient.GetAppUpdateInfoAsync();
            AppProductName = info.ProductName;
            AppVersion = info.CurrentVersion;
            UpdateAvailable = info.UpdateAvailable;
            UpdateDownloadUrl = info.DownloadUrl ?? "";
            UpdateStatus = info.UpdateAvailable
                ? $"Доступна версия {info.LatestVersion}. {info.ReleaseNotes}"
                : $"Установлена актуальная версия {info.CurrentVersion}.";
        }
        catch (HttpRequestException exception)
        {
            UpdateStatus = $"Проверка обновлений недоступна: {exception.Message}";
        }
    }

    [RelayCommand]
    private void InstallUpdate()
    {
        if (string.IsNullOrWhiteSpace(UpdateDownloadUrl))
        {
            UpdateStatus = "Сервер обновлений не передал ссылку на установщик.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(UpdateDownloadUrl)
            {
                UseShellExecute = true
            });
            UpdateStatus = "Открыт установщик обновления.";
        }
        catch (InvalidOperationException exception)
        {
            UpdateStatus = $"Не удалось открыть обновление: {exception.Message}";
        }
    }

    private void StartSessionRefreshLoop()
    {
        _sessionRefreshCts?.Cancel();
        _sessionRefreshCts = new CancellationTokenSource();
        _ = RefreshSessionLoopAsync(_sessionRefreshCts.Token);
    }

    private async Task RefreshSessionLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(14), cancellationToken);
                if (!IsAuthenticated || _apiClient.AccessToken is null)
                    continue;

                var auth = await _apiClient.RefreshSessionAsync(cancellationToken);
                SaveToken(auth.AccessToken);
                NetworkStatus = "Сессия обновлена";
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (HttpRequestException)
            {
                LocalSessionStore.Clear();
                IsAuthenticated = false;
                IsSettingsOpen = false;
                NetworkStatus = "Сессия истекла";
                AuthStatus = "Войдите снова";
                return;
            }
        }
    }

    [RelayCommand]
    private void ToggleEmojiPalette()
        => IsEmojiPaletteOpen = !IsEmojiPaletteOpen;

    [RelayCommand]
    private async Task AddPhotoAttachment()
        => await PickAttachmentsAsync("Выбрать фото", [FilePickerFileTypes.ImageAll], "Фото", "#4A8CFF");

    [RelayCommand]
    private async Task AddVideoAttachment()
    {
        var videoType = new FilePickerFileType("Видео")
        {
            Patterns = ["*.mp4", "*.mov", "*.m4v", "*.webm", "*.mkv"],
            MimeTypes = ["video/*"]
        };

        await PickAttachmentsAsync("Выбрать видео", [videoType], "Видео", "#FF6B8A");
    }

    [RelayCommand]
    private async Task AddDocumentAttachment()
    {
        var documentType = new FilePickerFileType("Документы")
        {
            Patterns = ["*.pdf", "*.doc", "*.docx", "*.xls", "*.xlsx", "*.ppt", "*.pptx", "*.txt", "*.zip"],
            MimeTypes =
            [
                "application/pdf",
                "application/msword",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                "application/vnd.ms-excel",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "application/zip",
                "text/plain"
            ]
        };

        await PickAttachmentsAsync("Выбрать документ", [documentType], "Документ", "#7C5CFF");
    }

    [RelayCommand]
    private void RemovePendingAttachment(AttachmentPreview attachment)
    {
        PendingAttachments.Remove(attachment);
        OnPropertyChanged(nameof(HasPendingAttachments));
        NetworkStatus = "Вложение удалено из сообщения";
    }

    [RelayCommand]
    private async Task DeleteMessageForMe(MessageItem message)
    {
        if (SelectedConversation is null)
            return;

        message.StartRemoveForMe();
        await Task.Delay(180);

        try
        {
            await _apiClient.DeleteMessageAsync(SelectedConversation.Id, message.Id, DeleteMessageScope.ForMe);
            SelectedConversation.Messages.Remove(message);
            SelectedConversation.RefreshPreview();
            NetworkStatus = "Сообщение удалено только у вас";
        }
        catch (HttpRequestException exception)
        {
            message.IsRemoving = false;
            NetworkStatus = $"Сообщение не удалено: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteMessageForEveryone(MessageItem message)
    {
        if (SelectedConversation is null)
            return;

        try
        {
            await _apiClient.DeleteMessageAsync(SelectedConversation.Id, message.Id, DeleteMessageScope.ForEveryone);
            message.MarkDeletedForEveryone();
            SelectedConversation.RefreshPreview();
            NetworkStatus = "Сообщение удалено у всех участников";
        }
        catch (HttpRequestException exception)
        {
            NetworkStatus = $"Сообщение не удалено: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task OpenAttachmentPreview(AttachmentPreview attachment)
    {
        if (attachment.IsImage && attachment.PreviewImage is null && !string.IsNullOrWhiteSpace(attachment.RemoteUrl))
        {
            try
            {
                var bytes = await _apiClient.DownloadAttachmentAsync(attachment.RemoteUrl);
                using var stream = new MemoryStream(bytes);
                attachment = attachment with { PreviewImage = new Bitmap(stream) };
            }
            catch (HttpRequestException)
            {
            }
        }

        SelectedAttachment = attachment;
        NetworkStatus = $"Открыто вложение: {attachment.FileName}";
    }

    [RelayCommand]
    private void CloseAttachmentPreview()
        => SelectedAttachment = null;

    [RelayCommand]
    private async Task CopyMessage(MessageItem message)
    {
        var topLevel = GetTopLevel();
        if (topLevel?.Clipboard is not null)
        {
            await topLevel.Clipboard.SetTextAsync(message.Text);
            NetworkStatus = "Сообщение скопировано";
        }
    }

    [RelayCommand]
    private void ReplyTo(MessageItem message)
    {
        ReplyToMessage = message;
        NetworkStatus = $"Ответ для {message.Sender}";
    }

    [RelayCommand]
    private void CancelReply()
        => ReplyToMessage = null;

    [RelayCommand]
    private void BeginForward(MessageItem message)
    {
        ForwardSourceMessage = message;
        IsForwardPickerOpen = true;
        NetworkStatus = "Выберите чат для пересылки";
    }

    [RelayCommand]
    private void ForwardMessageToConversation(ConversationItem conversation)
    {
        if (ForwardSourceMessage is null)
            return;

        var forwarded = new MessageItem(
            Guid.NewGuid(),
            CurrentUserName,
            $"Переслано: {ForwardSourceMessage.Text}",
            DateTimeOffset.Now,
            true,
            "отправлено",
            ForwardSourceMessage.Attachments.ToArray());

        conversation.Append(forwarded);
        SelectedConversation = conversation;
        ForwardSourceMessage = null;
        IsForwardPickerOpen = false;
        NetworkStatus = "Сообщение переслано";
    }

    [RelayCommand]
    private void CancelForward()
    {
        ForwardSourceMessage = null;
        IsForwardPickerOpen = false;
    }

    [RelayCommand]
    private async Task DeleteChatForMe()
    {
        if (SelectedConversation is null)
            return;

        var current = SelectedConversation;
        try
        {
            await _apiClient.DeleteConversationAsync(current.Id, DeleteConversationScope.ForMe);
            Conversations.Remove(current);
            SelectedConversation = Conversations.FirstOrDefault();
            NetworkStatus = "Чат удален только у вас";
        }
        catch (HttpRequestException exception)
        {
            NetworkStatus = $"Чат не удален: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteChatForEveryone()
    {
        if (SelectedConversation is null)
            return;

        var current = SelectedConversation;
        try
        {
            await _apiClient.DeleteConversationAsync(current.Id, DeleteConversationScope.ForEveryone);
            Conversations.Remove(current);
            SelectedConversation = Conversations.FirstOrDefault();
            NetworkStatus = "Чат удален у всех участников";
        }
        catch (HttpRequestException exception)
        {
            NetworkStatus = $"Чат не удален: {exception.Message}";
        }
    }

    [RelayCommand]
    private void SelectConversation(ConversationItem conversation)
        => SelectedConversation = conversation;

    [RelayCommand]
    private async Task ChangeProfilePhoto()
    {
        var topLevel = GetTopLevel();
        if (topLevel is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выбрать фото профиля",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll]
        });

        var file = files.FirstOrDefault();
        if (file is null)
            return;

        await using var stream = await file.OpenReadAsync();
        ProfileAvatar = new Bitmap(stream);
        _profileAvatarReference = file.TryGetLocalPath() ?? file.Name;
        ProfileAvatarHint = file.Name;
        ProfileStatus = "Фото выбрано, сохраните профиль";
        NetworkStatus = "Фото профиля обновлено";
    }

    [RelayCommand]
    private async Task CreateChatFromSearch(ProfileSearchResult profile)
    {
        var existing = Conversations.FirstOrDefault(conversation =>
            conversation.Title.Equals(profile.DisplayName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            SelectedConversation = existing;
            IsSearchDropdownOpen = false;
            return;
        }

        try
        {
            var summary = await _apiClient.CreateDirectConversationAsync(profile.Username);
            var conversation = MapConversation(summary);
            Conversations.Insert(0, conversation);
            SelectedConversation = conversation;
            NewChatQuery = "";
            IsSearchDropdownOpen = false;
            NetworkStatus = $"Создан чат с @{profile.Username}";
        }
        catch (HttpRequestException exception)
        {
            NetworkStatus = $"Чат не создан: {exception.Message}";
        }
    }

    partial void OnNewChatQueryChanged(string value)
    {
        _searchCts?.Cancel();
        SearchResults.Clear();

        var query = value.Trim();
        if (query.Length < 2)
        {
            IsSearchDropdownOpen = false;
            OnPropertyChanged(nameof(HasSearchResults));
            return;
        }

        _searchCts = new CancellationTokenSource();
        _ = SearchUsersAsync(query, _searchCts.Token);
    }

    private async Task SearchUsersAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(260, cancellationToken);
            if (_apiClient.AccessToken is null)
                return;

            var users = await _apiClient.SearchUsersAsync(query, cancellationToken);
            SearchResults.Clear();
            foreach (var user in users.Take(5))
            {
                SearchResults.Add(new ProfileSearchResult(
                    user.Id,
                    user.DisplayName,
                    user.Handle,
                    user.About ?? (user.IsOnline ? "online" : "offline"),
                    BuildInitials(user.DisplayName),
                    PickAccent(user.Handle)));
            }

            IsSearchDropdownOpen = SearchResults.Count > 0;
            OnPropertyChanged(nameof(HasSearchResults));
        }
        catch (OperationCanceledException)
        {
        }
        catch (HttpRequestException exception)
        {
            IsSearchDropdownOpen = false;
            OnPropertyChanged(nameof(HasSearchResults));
            NetworkStatus = $"Поиск недоступен: {exception.Message}";
        }
    }

    partial void OnSelectedConversationChanged(ConversationItem? value)
    {
        if (value is null)
        {
            IsProfileViewerOpen = false;
            return;
        }

        OnPropertyChanged(nameof(IsSelectedConversationOnline));
        OnPropertyChanged(nameof(ViewedProfileTitle));
        OnPropertyChanged(nameof(ViewedProfileSubtitle));
        OnPropertyChanged(nameof(ViewedProfileInitials));
        OnPropertyChanged(nameof(ViewedProfileAccent));
        OnPropertyChanged(nameof(ViewedProfileStatus));
        OnPropertyChanged(nameof(ViewedProfileAbout));
        OnPropertyChanged(nameof(ViewedProfileMessagesInfo));
        OnPropertyChanged(nameof(CanOpenProfileViewer));

        if (_apiClient.AccessToken is not null)
        {
            _ = LoadMessagesForConversationAsync(value);
            _ = MarkSelectedConversationReadAsync(value);
        }
    }

    partial void OnProfileDisplayNameChanged(string value)
    {
        OnPropertyChanged(nameof(CurrentUserName));
        OnPropertyChanged(nameof(ProfileInitials));
        ProfileStatus = "Изменения не сохранены";
    }

    partial void OnAuthEmailChanged(string value)
        => OnPropertyChanged(nameof(IsEmailValid));

    partial void OnAuthLoginChanged(string value)
    {
        IsTotpStepVisible = false;
        AuthTotpCode = "";
    }

    partial void OnAuthPasswordChanged(string value)
    {
        IsTotpStepVisible = false;
        AuthTotpCode = "";
    }

    partial void OnAuthPhoneChanged(string value)
    {
        OnPropertyChanged(nameof(IsAuthPhoneValid));
        OnPropertyChanged(nameof(AuthPhoneValidationMessage));
    }

    partial void OnProfileUsernameChanged(string value)
    {
        var normalized = value.Trim().TrimStart('@').Replace(" ", "", StringComparison.Ordinal);
        if (!string.Equals(normalized, value, StringComparison.Ordinal))
        {
            _profileUsername = normalized;
            OnPropertyChanged(nameof(ProfileUsername));
        }

        OnPropertyChanged(nameof(CurrentHandle));
        ProfileStatus = "Изменения не сохранены";
    }

    partial void OnChatFontSizeChanged(double value)
    {
        PersistClientSettings();
        NetworkStatus = $"Размер текста: {value:0}";
    }

    partial void OnChatThemeNameChanged(string value)
    {
        if (!ChatThemeOptions.Contains(value))
        {
            _chatThemeName = ChatThemeOptions[0];
            OnPropertyChanged(nameof(ChatThemeName));
            return;
        }

        NotifyThemeChanged();
        PersistClientSettings();
        NetworkStatus = $"Тема чата: {value}";
    }

    partial void OnBubbleDensityChanged(string value)
    {
        if (!BubbleDensityOptions.Contains(value))
        {
            _bubbleDensity = BubbleDensityOptions[1];
            OnPropertyChanged(nameof(BubbleDensity));
            return;
        }

        NotifyDensityChanged();
        OnPropertyChanged(nameof(ActiveMessages));
        PersistClientSettings();
        NetworkStatus = $"Плотность сообщений: {value}";
    }

    partial void OnProfileAboutChanged(string value)
        => ProfileStatus = "Изменения не сохранены";

    partial void OnProfileBirthdayChanged(string value)
        => ProfileStatus = "Изменения не сохранены";

    partial void OnProfilePhoneChanged(string value)
        => ProfileStatus = "Изменения не сохранены";

    partial void OnAllowProfileSearchChanged(bool value)
    {
        QueueServerSettingsSync();
        NetworkStatus = value ? "Поиск по профилю включен" : "Поиск по профилю выключен";
    }

    partial void OnAllowPhoneDiscoveryChanged(bool value)
    {
        QueueServerSettingsSync();
        NetworkStatus = value ? "Поиск по телефону включен" : "Поиск по телефону выключен";
    }

    partial void OnShowBirthdayChanged(bool value)
        => QueueServerSettingsSync();

    partial void OnShowLastSeenChanged(bool value)
        => QueueServerSettingsSync();

    partial void OnSendReadReceiptsChanged(bool value)
    {
        QueueServerSettingsSync();
        NetworkStatus = value ? "Отметки прочтения включены" : "Отметки прочтения выключены";
    }

    partial void OnRequireTwoFactorChanged(bool value)
        => TwoFactorStatus = value ? "Включите 2FA через одноразовый PIN" : "2FA выключается отдельным подтверждением";


    partial void OnEncryptMediaMetadataChanged(bool value)
        => QueueServerSettingsSync();

    partial void OnSendByEnterChanged(bool value)
    {
        PersistClientSettings();
        NetworkStatus = value ? "Enter отправляет сообщение" : "Enter оставляет перенос строки";
    }

    partial void OnShowMessageNotificationsChanged(bool value)
        => QueueServerSettingsSync();

    partial void OnPlayIncomingSoundChanged(bool value)
        => QueueServerSettingsSync();

    partial void OnShowTextPreviewChanged(bool value)
        => QueueServerSettingsSync();

    partial void OnSelectedAttachmentChanged(AttachmentPreview? value)
    {
        OnPropertyChanged(nameof(IsAttachmentPreviewOpen));
        OnPropertyChanged(nameof(SelectedAttachmentPreviewImage));
        OnPropertyChanged(nameof(SelectedAttachmentFileName));
        OnPropertyChanged(nameof(SelectedAttachmentSizeText));
        OnPropertyChanged(nameof(SelectedAttachmentKind));
        OnPropertyChanged(nameof(SelectedAttachmentLocalPath));
        OnPropertyChanged(nameof(SelectedAttachmentHasImage));
        OnPropertyChanged(nameof(SelectedAttachmentIsVideo));
        OnPropertyChanged(nameof(SelectedAttachmentIsDocument));
    }

    private void ApplyClientSettings(ClientSettings settings)
    {
        _suppressSettingsPersistence = true;
        try
        {
            ChatFontSize = Math.Clamp(settings.ChatFontSize, 12, 20);
            ChatThemeName = ChatThemeOptions.Contains(settings.ChatThemeName)
                ? settings.ChatThemeName
                : ClientSettings.Default.ChatThemeName;
            BubbleDensity = BubbleDensityOptions.Contains(settings.BubbleDensity)
                ? settings.BubbleDensity
                : ClientSettings.Default.BubbleDensity;
            SendByEnter = settings.SendByEnter;
        }
        finally
        {
            _suppressSettingsPersistence = false;
        }

        NotifyThemeChanged();
        NotifyDensityChanged();
    }

    private void PersistClientSettings()
    {
        if (_suppressSettingsPersistence)
            return;

        ClientSettingsStore.Save(new ClientSettings(
            ChatFontSize,
            ChatThemeName,
            BubbleDensity,
            SendByEnter));
    }

    private void QueueServerSettingsSync()
    {
        if (_suppressServerSettingsSync || !IsAuthenticated || _apiClient.AccessToken is null)
            return;

        _ = SyncServerSettingsAsync();
    }

    private async Task SyncServerSettingsAsync()
    {
        try
        {
            var profile = await _apiClient.UpdateSettingsAsync(new UpdateUserSettingsRequest(
                new UserPrivacySettingsDto(
                    AllowProfileSearch,
                    AllowPhoneDiscovery,
                    ShowBirthday,
                    ShowLastSeen,
                    SendReadReceipts,
                    EncryptMediaMetadata),
                new UserNotificationSettingsDto(
                    ShowMessageNotifications,
                    PlayIncomingSound,
                    ShowTextPreview)));
            _suppressServerSettingsSync = true;
            try
            {
                AllowProfileSearch = profile.Privacy.AllowProfileSearch;
                AllowPhoneDiscovery = profile.Privacy.AllowPhoneDiscovery;
                ShowBirthday = profile.Privacy.ShowBirthday;
                ShowLastSeen = profile.Privacy.ShowLastSeen;
                SendReadReceipts = profile.Privacy.SendReadReceipts;
                EncryptMediaMetadata = profile.Privacy.EncryptMediaMetadata;
                ShowMessageNotifications = profile.Notifications.ShowMessageNotifications;
                PlayIncomingSound = profile.Notifications.PlayIncomingSound;
                ShowTextPreview = profile.Notifications.ShowTextPreview;
            }
            finally
            {
                _suppressServerSettingsSync = false;
            }
        }
        catch (HttpRequestException exception)
        {
            NetworkStatus = $"Настройки не синхронизированы: {exception.Message}";
        }
    }

    private void NotifyThemeChanged()
    {
        OnPropertyChanged(nameof(AppGradientStart));
        OnPropertyChanged(nameof(AppGradientMiddle));
        OnPropertyChanged(nameof(AppGradientEnd));
        OnPropertyChanged(nameof(ShellBrush));
        OnPropertyChanged(nameof(ChatSurfaceBrush));
        OnPropertyChanged(nameof(ComposerSurfaceBrush));
        OnPropertyChanged(nameof(IncomingMessageBrush));
        OnPropertyChanged(nameof(OutgoingMessageBrush));
        OnPropertyChanged(nameof(PrimaryAccentBrush));
        OnPropertyChanged(nameof(SettingsDividerBrush));
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    private void NotifyDensityChanged()
    {
        OnPropertyChanged(nameof(MessagePadding));
        OnPropertyChanged(nameof(MessageMaxWidth));
        OnPropertyChanged(nameof(MessageTextMaxWidth));
        OnPropertyChanged(nameof(MessageSpacing));
        OnPropertyChanged(nameof(ActiveMessages));
        OnPropertyChanged(nameof(ViewedProfileMessagesInfo));
    }

    private void RefreshSecuritySignals()
    {
        var snapshot = _entropy.Snapshot;
        SecuritySignals.Clear();
        SecuritySignals.Add(new SecuritySignalItem(
            "Entropy pool",
            $"{snapshot.HealthScore:P0}",
            $"{snapshot.ActiveSource}; reseed #{snapshot.ReseedCounter}",
            "#28B7A6"));
        SecuritySignals.Add(new SecuritySignalItem(
            "Device key",
            snapshot.PoolFingerprint,
            $"ECDH-P256 key {DeviceKeyFingerprint}",
            "#4A8CFF"));
        SecuritySignals.Add(new SecuritySignalItem(
            "Message crypto",
            "AES-GCM",
            "Envelope encryption, per-message nonce",
            "#7C5CFF"));
    }

    private async Task PickAttachmentsAsync(
        string title,
        IReadOnlyList<FilePickerFileType> filters,
        string kind,
        string accent)
    {
        var topLevel = GetTopLevel();
        if (topLevel is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = filters
        });

        foreach (var file in files)
        {
            var localPath = file.TryGetLocalPath();
            var size = localPath is not null && File.Exists(localPath)
                ? new FileInfo(localPath).Length
                : 0;
            var contentType = GuessContentType(file.Name, kind);

            PendingAttachments.Add(new AttachmentPreview(
                Guid.NewGuid(),
                kind,
                file.Name,
                FormatSize(size),
                accent,
                localPath,
                contentType,
                TryLoadPreviewImage(localPath, contentType)));
        }

        OnPropertyChanged(nameof(HasPendingAttachments));
        NetworkStatus = files.Count switch
        {
            0 => NetworkStatus,
            1 => $"{kind} добавлено к сообщению",
            _ => $"Добавлено вложений: {files.Count}"
        };
    }

    private static TopLevel? GetTopLevel()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;

        if (Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            return TopLevel.GetTopLevel(singleView.MainView);

        return null;
    }

    private static Bitmap? TryLoadPreviewImage(string? localPath, string contentType)
    {
        if (localPath is null
            || !File.Exists(localPath)
            || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            using var stream = File.OpenRead(localPath);
            return new Bitmap(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Bitmap? CreateQrBitmap(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        var qr = new PngByteQRCode(data);
        var bytes = qr.GetGraphic(12);
        using var stream = new MemoryStream(bytes);
        return new Bitmap(stream);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
            return "локальный файл";

        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.#} {units[unit]}";
    }

    private static string GuessContentType(string fileName, string kind)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".mp4" or ".m4v" => "video/mp4",
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".zip" => "application/zip",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ when kind == "Фото" => "image/*",
            _ when kind == "Видео" => "video/*",
            _ when kind == "Документ" => "application/octet-stream",
            _ => "application/octet-stream"
        };
    }

    private static string BuildInitials(string displayName)
    {
        var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return "U";

        return string.Concat(parts.Take(2).Select(part => char.ToUpperInvariant(part[0])));
    }

    private static (bool IsValid, string Message) ValidatePhone(string phone, bool required)
    {
        var normalized = NormalizePhone(phone);
        if (string.IsNullOrWhiteSpace(normalized))
            return required ? (false, "Телефон обязателен") : (true, "Телефон не указан");

        if (!E164PhoneRegex.IsMatch(normalized))
            return (false, "Введите номер в формате +79991234567");

        return (true, "Номер корректный");
    }

    private static AuthProvider InferAuthProvider(string login)
    {
        var value = login.Trim();
        return EmailRegex.IsMatch(value)
            ? AuthProvider.Email
            : AuthProvider.Phone;
    }

    private static string NormalizeLoginIdentifier(AuthProvider provider, string login)
    {
        return provider == AuthProvider.Email
            ? login.Trim().ToLowerInvariant()
            : NormalizePhone(login);
    }

    private static bool IsLoginIdentifierValid(AuthProvider provider, string identifier)
    {
        return provider == AuthProvider.Email
            ? EmailRegex.IsMatch(identifier)
            : E164PhoneRegex.IsMatch(identifier);
    }

    private static (bool IsValid, string Message) ValidateBirthday(string birthday)
    {
        var value = birthday.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return (true, "Дата не указана");
        if (!DateOnly.TryParseExact(
                value,
                BirthdayFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
            return (false, "Введите дату в формате 04.06.1992");

        var today = DateOnly.FromDateTime(DateTime.Today);
        if (date > today)
            return (false, "Дата рождения не может быть в будущем");

        if (date.Year < 1900)
            return (false, "Проверьте год рождения");

        return (true, "Дата корректная");
    }

    private static DateOnly? ParseBirthdayDate(string birthday)
    {
        return DateOnly.TryParseExact(
            birthday.Trim(),
            BirthdayFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date)
            ? date
            : null;
    }

    private static string FormatBirthday(DateOnly? date)
        => date?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "";

    private static string NormalizePhone(string phone)
    {
        var cleaned = phone.Trim()
            .Replace(" ", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Replace("(", "", StringComparison.Ordinal)
            .Replace(")", "", StringComparison.Ordinal);

        if (cleaned.StartsWith('8') && cleaned.Length == 11)
            cleaned = $"+7{cleaned[1..]}";

        return cleaned;
    }

    private static string PickAccent(string value)
    {
        string[] accents = ["#4A8CFF", "#28B7A6", "#FFB44A", "#EF6C8E", "#7C5CFF"];
        var hash = Math.Abs(value.GetHashCode(StringComparison.Ordinal));
        return accents[hash % accents.Length];
    }

    private static ThemeColors GetThemeColors(string theme)
    {
        return theme switch
        {
            "Темная" => new ThemeColors(
                "#0B111B",
                "#111827",
                "#1F2937",
                "#F0121824",
                "#101722",
                "#151D2A",
                "#1C2635",
                "#24354A",
                "#60A5FA",
                "#334155"),
            "Системная" => new ThemeColors(
                "#F5F8FA",
                "#EAF5EE",
                "#F6F2FA",
                "#DAFFFFFF",
                "#F8FAFC",
                "#EFFFFFFF",
                "#FFFFFF",
                "#E6F2FF",
                "#177DDC",
                "#DDE6EF"),
            _ => new ThemeColors(
                "#F4F8FB",
                "#EDF5F1",
                "#F4F2FA",
                "#D8FFFFFF",
                "#F9FBFD",
                "#EFFFFFFF",
                "#DFFFFFFF",
                "#DCEBFFFF",
                "#2563EB",
                "#D9E3EC")
        };
    }

    private static string BuildUsername(string identity)
    {
        var normalized = identity.Trim().TrimStart('@').ToLowerInvariant();
        if (EmailRegex.IsMatch(normalized))
            normalized = normalized.Split('@')[0];

        normalized = Regex.Replace(normalized, "[^a-z0-9_]+", "_").Trim('_');
        return string.IsNullOrWhiteSpace(normalized) ? $"user_{Guid.NewGuid():N}"[..13] : normalized;
    }

    private static IEnumerable<ConversationItem> CreateConversations()
    {
        var now = DateTimeOffset.Now;

        yield return new ConversationItem(
            Guid.Parse("9bd86d5a-50d3-43aa-b5ea-b3a6d2b99d0b"),
            "Security Operations",
            "12 участников",
            "SO",
            "Ключи устройств обновлены, можно отправлять отчет.",
            "12:48",
            2,
            true,
            "#4A8CFF",
            new[]
            {
                new MessageItem(Guid.NewGuid(), "Anna", "Проверила список устройств. У троих ключи старше 30 дней.", now.AddMinutes(-42), false, "доставлено"),
                new MessageItem(Guid.NewGuid(), "Vladimir", "Ок, отправлю им запрос на ротацию и приложу инструкцию.", now.AddMinutes(-28), true, "прочитано"),
                new MessageItem(Guid.NewGuid(), "Anna", "Добавила скрин с проблемными сертификатами.", now.AddMinutes(-18), false, "доставлено", new[]
                {
                    new AttachmentPreview(Guid.NewGuid(), "Фото", "certificates.png", "1.8 MB", "#4A8CFF", null, "image/png")
                })
            });

        yield return new ConversationItem(
            Guid.Parse("a742b91e-f9f5-4f6f-af6a-7e46c341a6ed"),
            "Dmitry Volkov",
            "online",
            "DV",
            "Видео с демонстрацией уже в чате.",
            "11:32",
            0,
            true,
            "#28B7A6",
            new[]
            {
                new MessageItem(Guid.NewGuid(), "Dmitry", "Снял короткую демонстрацию по API для ботов.", now.AddHours(-2), false, "доставлено", new[]
                {
                    new AttachmentPreview(Guid.NewGuid(), "Видео", "bot-api-demo.mp4", "24 MB", "#FF6B8A", null, "video/mp4")
                }),
                new MessageItem(Guid.NewGuid(), "Vladimir", "Отлично, добавлю это в onboarding для команды.", now.AddHours(-1).AddMinutes(-48), true, "прочитано")
            });

        yield return new ConversationItem(
            Guid.Parse("cf3d23f3-f2dd-4b69-bcff-0cb62ebd1e94"),
            "Release Room",
            "5 участников",
            "RR",
            "Bot проверил чеклист перед релизом.",
            "09:14",
            4,
            false,
            "#FFB44A",
            new[]
            {
                new MessageItem(Guid.NewGuid(), "Release Bot", "Чеклист релиза: миграции, smoke tests, backup snapshot.", now.AddHours(-4), false, "bot"),
                new MessageItem(Guid.NewGuid(), "Vladimir", "Подтверждаю. После сборки прогоним desktop smoke на macOS.", now.AddHours(-3).AddMinutes(-36), true, "отправлено")
            });
    }

    private static IEnumerable<EmojiOption> CreateEmojiOptions()
    {
        yield return new EmojiOption("🙂", "smile");
        yield return new EmojiOption("🔥", "fire");
        yield return new EmojiOption("✅", "done");
        yield return new EmojiOption("🔐", "secure");
        yield return new EmojiOption("🚀", "launch");
        yield return new EmojiOption("👀", "eyes");
        yield return new EmojiOption("🤝", "team");
        yield return new EmojiOption("💡", "idea");
    }

    private sealed record ThemeColors(
        string GradientStart,
        string GradientMiddle,
        string GradientEnd,
        string Shell,
        string ChatSurface,
        string ComposerSurface,
        string IncomingBubble,
        string OutgoingBubble,
        string PrimaryAccent,
        string Divider);
}
