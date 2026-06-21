using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace NinaOtel.Core.Tests.Plugin;

public sealed class OptionsXamlTests
{
    private static readonly XNamespace PresentationNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void OptionsTemplate_DiskOnFailureCheckboxUsesTwoWayBinding()
    {
        var document = XDocument.Load(FindOptionsXamlPath());
        var checkbox = document
            .Descendants(PresentationNamespace + "CheckBox")
            .Single(element => element.Attribute("Content")?.Value == "Disk spool on collector failure");

        var binding = checkbox.Attribute("IsChecked")?.Value;

        binding.Should().Contain(
            "Mode=TwoWay",
            "the source property is writable and NINA should persist edits to profile settings");
        checkbox.Attribute("IsEnabled")?.Value.Should().NotBe("False");
    }

    [Theory]
    [InlineData("StaticHeaders")]
    [InlineData("CaCertificatePemPath")]
    [InlineData("ClientCertificatePemPath")]
    [InlineData("ClientPrivateKeyPemPath")]
    [InlineData("SpoolPath")]
    [InlineData("MaxSpoolSizeGb")]
    [InlineData("MaxSpoolAgeDays")]
    public void OptionsTemplate_TextBoxesUseTwoWayLostFocusBindings(string propertyName)
    {
        var document = XDocument.Load(FindOptionsXamlPath());

        var textbox = SingleTextBoxBoundTo(document, propertyName);
        var binding = textbox.Attribute("Text")?.Value;

        binding.Should().Contain("Mode=TwoWay");
        binding.Should().Contain("UpdateSourceTrigger=LostFocus");
    }

    [Fact]
    public void OptionsTemplate_AuthenticationModeComboBoxUsesTwoWayBinding()
    {
        var document = XDocument.Load(FindOptionsXamlPath());

        var comboBox = document
            .Descendants(PresentationNamespace + "ComboBox")
            .Single(element => element.Attribute("SelectedItem")?.Value.Contains("AuthenticationMode", StringComparison.Ordinal) == true);

        comboBox.Attribute("ItemsSource")?.Value.Should().Contain("AvailableAuthenticationModes");
        comboBox.Attribute("SelectedItem")?.Value.Should().Contain("Mode=TwoWay");
    }

    [Fact]
    public void OptionsTemplate_BasicUsernameUsesTwoWayLostFocusBinding()
    {
        var document = XDocument.Load(FindOptionsXamlPath());

        var textbox = SingleTextBoxBoundTo(document, "BasicUsername");
        textbox.Attribute("Text")?.Value.Should().Contain("Mode=TwoWay");
        textbox.Attribute("Text")?.Value.Should().Contain("UpdateSourceTrigger=LostFocus");
    }

    [Fact]
    public void OptionsTemplate_AddonsExposeEnableRawAndHealthBindings()
    {
        var document = XDocument.Load(FindOptionsXamlPath());

        var itemsControl = document
            .Descendants(PresentationNamespace + "ItemsControl")
            .Single(element => element.Attribute("ItemsSource")?.Value.Contains("NinaOtelOptionsViewModel.Addons", StringComparison.Ordinal) == true);
        var checkboxes = itemsControl
            .Descendants(PresentationNamespace + "CheckBox")
            .ToArray();

        checkboxes.Single(element => element.Attribute("IsChecked")?.Value.Contains("IsEnabled", StringComparison.Ordinal) == true)
            .Attribute("IsChecked")?.Value.Should().Contain("Mode=TwoWay");
        checkboxes.Single(element => element.Attribute("IsChecked")?.Value.Contains("RawForwardingEnabled", StringComparison.Ordinal) == true)
            .Attribute("IsChecked")?.Value.Should().Contain("Mode=TwoWay");
        var textBindings = itemsControl
            .Descendants(PresentationNamespace + "TextBlock")
            .Select(element => element.Attribute("Text")?.Value)
            .OfType<string>()
            .ToArray();

        textBindings.Should().Contain(binding => binding.Contains("Status", StringComparison.Ordinal));
        textBindings.Should().Contain(binding => binding.Contains("Message", StringComparison.Ordinal));
    }

    [Fact]
    public void OptionsTemplate_AddonSwitchesUseExplicitVisibleLabels()
    {
        var document = XDocument.Load(FindOptionsXamlPath());

        var itemsControl = document
            .Descendants(PresentationNamespace + "ItemsControl")
            .Single(element => element.Attribute("ItemsSource")?.Value.Contains("NinaOtelOptionsViewModel.Addons", StringComparison.Ordinal) == true);
        var labels = itemsControl
            .Descendants(PresentationNamespace + "TextBlock")
            .Select(element => element.Attribute("Text")?.Value)
            .OfType<string>()
            .ToArray();
        var checkboxes = itemsControl
            .Descendants(PresentationNamespace + "CheckBox")
            .ToArray();

        labels.Should().Contain("Enabled:");
        labels.Should().Contain("Raw log forwarding:");
        checkboxes.Should().NotContain(
            element => element.Attribute("Content") != null,
            "NINA renders these checkboxes as switch controls, so labels must be separate visible text");
    }

    [Theory]
    [InlineData("Phd2DebugLogPath")]
    [InlineData("Phd2GuideLogPath")]
    public void OptionsTemplate_Phd2LogPathTextBoxesUseTwoWayLostFocusBindings(string propertyName)
    {
        var document = XDocument.Load(FindOptionsXamlPath());
        var itemsControl = document
            .Descendants(PresentationNamespace + "ItemsControl")
            .Single(element => element.Attribute("ItemsSource")?.Value.Contains("NinaOtelOptionsViewModel.Addons", StringComparison.Ordinal) == true);

        var textbox = SingleTextBoxBoundTo(itemsControl, propertyName);
        var binding = textbox.Attribute("Text")?.Value;

        binding.Should().Contain("Mode=TwoWay");
        binding.Should().Contain("UpdateSourceTrigger=LostFocus");
        textbox.Attribute("Visibility")?.Value.Should().Contain("IsPhd2");
    }

    [Fact]
    public void OptionsTemplate_TargetSchedulerLogPathTextBoxUsesTwoWayLostFocusBinding()
    {
        var document = XDocument.Load(FindOptionsXamlPath());
        var itemsControl = document
            .Descendants(PresentationNamespace + "ItemsControl")
            .Single(element => element.Attribute("ItemsSource")?.Value.Contains("NinaOtelOptionsViewModel.Addons", StringComparison.Ordinal) == true);

        var textbox = SingleTextBoxBoundTo(itemsControl, "TargetSchedulerLogPath");
        var binding = textbox.Attribute("Text")?.Value;

        binding.Should().Contain("Mode=TwoWay");
        binding.Should().Contain("UpdateSourceTrigger=LostFocus");
        textbox.Attribute("Visibility")?.Value.Should().Contain("IsTargetScheduler");
    }

    [Fact]
    public void OptionsTemplate_NightSummaryLogPathTextBoxUsesTwoWayLostFocusBinding()
    {
        var document = XDocument.Load(FindOptionsXamlPath());
        var itemsControl = document
            .Descendants(PresentationNamespace + "ItemsControl")
            .Single(element => element.Attribute("ItemsSource")?.Value.Contains("NinaOtelOptionsViewModel.Addons", StringComparison.Ordinal) == true);

        var textbox = SingleTextBoxBoundTo(itemsControl, "NightSummaryLogPath");
        var binding = textbox.Attribute("Text")?.Value;

        binding.Should().Contain("Mode=TwoWay");
        binding.Should().Contain("UpdateSourceTrigger=LostFocus");
        textbox.Attribute("Visibility")?.Value.Should().Contain("IsNightSummary");
    }

    [Theory]
    [InlineData("OnStepXHost")]
    [InlineData("OnStepXPort")]
    [InlineData("OnStepXPollingIntervalSeconds")]
    [InlineData("OnStepXCommandTimeoutMilliseconds")]
    public void OptionsTemplate_OnStepXTextBoxesUseTwoWayLostFocusBindings(string propertyName)
    {
        var document = XDocument.Load(FindOptionsXamlPath());
        var itemsControl = document
            .Descendants(PresentationNamespace + "ItemsControl")
            .Single(element => element.Attribute("ItemsSource")?.Value.Contains("NinaOtelOptionsViewModel.Addons", StringComparison.Ordinal) == true);

        var textbox = SingleTextBoxBoundTo(itemsControl, propertyName);
        var binding = textbox.Attribute("Text")?.Value;

        binding.Should().Contain("Mode=TwoWay");
        binding.Should().Contain("UpdateSourceTrigger=LostFocus");
        textbox.Attribute("Visibility")?.Value.Should().Contain("IsOnStepX");
    }

    [Theory]
    [InlineData(
        "BearerTokenPasswordBox_Loaded",
        "BearerTokenPasswordBox_LostFocus",
        "BearerTokenPasswordBox_PasswordChanged")]
    [InlineData(
        "BasicPasswordBox_Loaded",
        "BasicPasswordBox_LostFocus",
        "BasicPasswordBox_PasswordChanged")]
    public void OptionsTemplate_SecretsUsePasswordBoxesWithoutPasswordBinding(
        string loadedHandler,
        string lostFocusHandler,
        string passwordChangedHandler)
    {
        var document = XDocument.Load(FindOptionsXamlPath());

        var passwordBox = document
            .Descendants(PresentationNamespace + "PasswordBox")
            .Single(element =>
                element.Attribute("Loaded")?.Value == loadedHandler &&
                element.Attribute("LostFocus")?.Value == lostFocusHandler);

        passwordBox.Attributes().Should().NotContain(attribute => attribute.Name.LocalName == "Password");
        passwordBox.Attribute("PasswordChanged")?.Value.Should().Be(passwordChangedHandler);
        passwordBox.Attribute("Unloaded")?.Value.Should().Be("SecretPasswordBox_Unloaded");
    }

    private static XElement SingleTextBoxBoundTo(XDocument document, string propertyName)
    {
        return document
            .Descendants(PresentationNamespace + "TextBox")
            .Single(element => element.Attribute("Text")?.Value.Contains(propertyName, StringComparison.Ordinal) == true);
    }

    private static XElement SingleTextBoxBoundTo(XElement element, string propertyName)
    {
        return element
            .Descendants(PresentationNamespace + "TextBox")
            .Single(candidate => candidate.Attribute("Text")?.Value.Contains(propertyName, StringComparison.Ordinal) == true);
    }

    private static string FindOptionsXamlPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NinaOtel.sln")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test should run from inside the repository");
        return Path.Combine(
            directory!.FullName,
            "src",
            "NinaOtel.Plugin",
            "Options",
            "Options.xaml");
    }
}
