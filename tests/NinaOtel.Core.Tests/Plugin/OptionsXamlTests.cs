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
