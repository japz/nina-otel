#if NINAOTEL_WPF
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using NinaOtel.Plugin;

namespace NinaOtel.Plugin.Options;

[Export(typeof(ResourceDictionary))]
public partial class Options : ResourceDictionary
{
    public Options()
    {
        InitializeComponent();
    }

    private void BearerTokenPasswordBox_Loaded(object sender, RoutedEventArgs e) =>
        LoadPassword(sender, static viewModel => viewModel.GetBearerToken());

    private void BearerTokenPasswordBox_LostFocus(object sender, RoutedEventArgs e) =>
        SavePassword(sender, static (viewModel, password) => viewModel.SetBearerToken(password));

    private void BasicPasswordBox_Loaded(object sender, RoutedEventArgs e) =>
        LoadPassword(sender, static viewModel => viewModel.GetBasicPassword());

    private void BasicPasswordBox_LostFocus(object sender, RoutedEventArgs e) =>
        SavePassword(sender, static (viewModel, password) => viewModel.SetBasicPassword(password));

    private static void LoadPassword(object sender, Func<NinaOtelOptionsViewModel, string> getPassword)
    {
        if (sender is PasswordBox passwordBox && TryGetViewModel(passwordBox, out var viewModel))
        {
            passwordBox.Password = getPassword(viewModel);
        }
    }

    private static void SavePassword(
        object sender,
        Action<NinaOtelOptionsViewModel, string> setPassword)
    {
        if (sender is PasswordBox passwordBox && TryGetViewModel(passwordBox, out var viewModel))
        {
            setPassword(viewModel, passwordBox.Password);
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
}
#else
namespace NinaOtel.Plugin.Options;

public sealed class Options
{
}
#endif
