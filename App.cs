using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;

namespace Glimpse;

public static class Program
{
    private static App? _app;

    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _app = new App(args);
        });
    }
}

/// <summary>The UI is built entirely in C# (no .xaml files), so the app supplies the control metadata itself.</summary>
public sealed class App : Application, IXamlMetadataProvider
{
    private XamlControlsXamlMetaDataProvider? _lazyProvider;
    private XamlControlsXamlMetaDataProvider _provider => _lazyProvider ??= new();
    private readonly string[] _args;
    private MainWindow? _window;

    public App(string[] args)
    {
        _args = args;
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            _window?.ReportError(e.Exception);
        };
    }

    public IXamlType GetXamlType(Type type) => _provider.GetXamlType(type);
    public IXamlType GetXamlType(string fullName) => _provider.GetXamlType(fullName);
    public XmlnsDefinition[] GetXmlnsDefinitions() => _provider.GetXmlnsDefinitions();

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        _window = new MainWindow(_args);
        _window.Activate();
    }
}
