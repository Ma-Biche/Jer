using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Jer.Interop;
using Jer.ViewModels;

namespace Jer.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        SourceInitialized += (_, _) => NativeMethods.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    /// <summary>Opens a tab by name: General, Display, Pages, Pomodoro, Water, Alerts or About.</summary>
    public void ShowTab(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        foreach (var item in Tabs.Items)
        {
            if (item is TabItem tab && tab.Name.Equals(name + "Tab", StringComparison.OrdinalIgnoreCase))
            {
                Tabs.SelectedItem = tab;
                return;
            }
        }
    }
}
