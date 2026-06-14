#if NINAOTEL_WPF
using System.ComponentModel.Composition;
using System.Windows;

namespace NinaOtel.Plugin.Options;

[Export(typeof(ResourceDictionary))]
public partial class Options : ResourceDictionary
{
    public Options()
    {
        InitializeComponent();
    }
}
#else
namespace NinaOtel.Plugin.Options;

public sealed class Options
{
}
#endif
