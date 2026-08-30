using System.Windows;
using Wpf.Ui.Appearance;

namespace Sling.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Applied unconditionally, even though App.xaml already merges the dark
        // ThemesDictionary. WPF-UI caches the current theme lazily, so until something
        // asks it, its cached answer is Unknown - and the DWM title-bar colour is set
        // from that cached value. Etch hit exactly this: a window whose caption stayed
        // light while its content was dark. One call at startup is the fix.
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);
    }
}
