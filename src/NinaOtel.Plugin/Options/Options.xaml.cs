#if NINAOTEL_WPF
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using NinaOtel.Plugin;

namespace NinaOtel.Plugin.Options;

[Export(typeof(ResourceDictionary))]
public partial class Options : ResourceDictionary
{
    private static readonly DependencyProperty PasswordBoxSecretStateProperty =
        DependencyProperty.RegisterAttached(
            "PasswordBoxSecretState",
            typeof(PasswordBoxSecretState),
            typeof(Options),
            new PropertyMetadata(null));

    public Options()
    {
        InitializeComponent();
    }

    private void BearerTokenPasswordBox_Loaded(object sender, RoutedEventArgs e) =>
        LoadPassword(sender, PasswordSecretKind.BearerToken);

    private void BearerTokenPasswordBox_LostFocus(object sender, RoutedEventArgs e) =>
        SavePassword(sender, PasswordSecretKind.BearerToken);

    private void BearerTokenPasswordBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        MarkPasswordDirty(sender);

    private void BasicPasswordBox_Loaded(object sender, RoutedEventArgs e) =>
        LoadPassword(sender, PasswordSecretKind.BasicPassword);

    private void BasicPasswordBox_LostFocus(object sender, RoutedEventArgs e) =>
        SavePassword(sender, PasswordSecretKind.BasicPassword);

    private void BasicPasswordBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        MarkPasswordDirty(sender);

    private void SecretPasswordBox_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
        {
            DetachPasswordState(passwordBox);
        }
    }

    private static void LoadPassword(object sender, PasswordSecretKind kind)
    {
        if (sender is PasswordBox passwordBox && TryGetViewModel(passwordBox, out var viewModel))
        {
            DetachPasswordState(passwordBox);
            var state = new PasswordBoxSecretState(kind, viewModel, passwordBox);
            SetPasswordBoxSecretState(passwordBox, state);
            viewModel.PropertyChanged += state.ViewModelPropertyChanged;
            RefreshPassword(passwordBox, state);
        }
    }

    private static void SavePassword(
        object sender,
        PasswordSecretKind kind)
    {
        if (sender is not PasswordBox passwordBox || !TryGetViewModel(passwordBox, out var viewModel))
        {
            return;
        }

        var state = GetPasswordBoxSecretState(passwordBox);
        if (state is null || state.Kind != kind || !ReferenceEquals(state.ViewModel, viewModel))
        {
            LoadPassword(sender, kind);
            return;
        }

        if (!state.IsDirty)
        {
            return;
        }

        if (state.LoadedSecretRevision != viewModel.SecretRevision)
        {
            RefreshPassword(passwordBox, state);
            return;
        }

        var revisionBeforeSave = viewModel.SecretRevision;
        SetPassword(viewModel, kind, passwordBox.Password);
        if (viewModel.SecretRevision == revisionBeforeSave)
        {
            state.IsDirty = false;
            state.LoadedSecretRevision = revisionBeforeSave;
        }
    }

    private static void MarkPasswordDirty(object sender)
    {
        if (sender is not PasswordBox passwordBox)
        {
            return;
        }

        var state = GetPasswordBoxSecretState(passwordBox);
        if (state is not null && !state.SuppressPasswordChanged)
        {
            state.IsDirty = true;
        }
    }

    private static void RefreshPassword(PasswordBox passwordBox, PasswordBoxSecretState state)
    {
        state.SuppressPasswordChanged = true;
        try
        {
            passwordBox.Password = GetPassword(state.ViewModel, state.Kind);
        }
        finally
        {
            state.SuppressPasswordChanged = false;
        }

        state.IsDirty = false;
        state.LoadedSecretRevision = state.ViewModel.SecretRevision;
    }

    private static string GetPassword(NinaOtelOptionsViewModel viewModel, PasswordSecretKind kind)
    {
        return kind switch
        {
            PasswordSecretKind.BearerToken => viewModel.GetBearerToken(),
            PasswordSecretKind.BasicPassword => viewModel.GetBasicPassword(),
            _ => string.Empty,
        };
    }

    private static void SetPassword(
        NinaOtelOptionsViewModel viewModel,
        PasswordSecretKind kind,
        string password)
    {
        switch (kind)
        {
            case PasswordSecretKind.BearerToken:
                viewModel.SetBearerToken(password);
                break;
            case PasswordSecretKind.BasicPassword:
                viewModel.SetBasicPassword(password);
                break;
        }
    }

    private static PasswordBoxSecretState? GetPasswordBoxSecretState(DependencyObject element) =>
        (PasswordBoxSecretState?)element.GetValue(PasswordBoxSecretStateProperty);

    private static void SetPasswordBoxSecretState(
        DependencyObject element,
        PasswordBoxSecretState? state) =>
        element.SetValue(PasswordBoxSecretStateProperty, state);

    private static void DetachPasswordState(PasswordBox passwordBox)
    {
        var state = GetPasswordBoxSecretState(passwordBox);
        if (state is not null)
        {
            state.ViewModel.PropertyChanged -= state.ViewModelPropertyChanged;
            SetPasswordBoxSecretState(passwordBox, null);
        }
    }

    private static bool TryGetViewModel(FrameworkElement element, out NinaOtelOptionsViewModel viewModel)
    {
        switch (element.DataContext)
        {
            case NinaOtelOptionsViewModel direct:
                viewModel = direct;
                return true;
            case NinaOtelPlugin plugin:
                viewModel = plugin.NinaOtelOptionsViewModel;
                return true;
            default:
                viewModel = null!;
                return false;
        }
    }

    private sealed class PasswordBoxSecretState
    {
        public PasswordBoxSecretState(
            PasswordSecretKind kind,
            NinaOtelOptionsViewModel viewModel,
            PasswordBox passwordBox)
        {
            Kind = kind;
            ViewModel = viewModel;
            ViewModelPropertyChanged = (_, args) =>
            {
                if (args.PropertyName == nameof(NinaOtelOptionsViewModel.SecretRevision))
                {
                    RefreshPassword(passwordBox, this);
                }
            };
        }

        public PasswordSecretKind Kind { get; }
        public NinaOtelOptionsViewModel ViewModel { get; }
        public PropertyChangedEventHandler ViewModelPropertyChanged { get; }
        public int LoadedSecretRevision { get; set; }
        public bool IsDirty { get; set; }
        public bool SuppressPasswordChanged { get; set; }
    }

    private enum PasswordSecretKind
    {
        BearerToken,
        BasicPassword,
    }
}
#else
namespace NinaOtel.Plugin.Options;

public sealed class Options
{
}
#endif
