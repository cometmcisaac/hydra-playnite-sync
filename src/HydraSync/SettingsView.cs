using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace HydraSync
{
    /// <summary>
    /// Code-only WPF settings view (no XAML so the project builds without the WPF
    /// markup compiler). Playnite sets DataContext to the PluginSettings instance.
    /// </summary>
    public class SettingsView : UserControl
    {
        public SettingsView()
        {
            var panel = new StackPanel { Margin = new Thickness(12) };

            panel.Children.Add(Check(nameof(PluginSettings.SyncPlaytime),
                "Sync playtime (Hydra's total replaces Playnite's when it is larger)"));
            panel.Children.Add(Check(nameof(PluginSettings.SyncAchievements),
                "Sync achievements from local emulator/crack files"));
            panel.Children.Add(Check(nameof(PluginSettings.WriteToPlayniteAchievements),
                "Export achievements to Playnite Achievements (imports on Playnite restart)"));
            panel.Children.Add(Check(nameof(PluginSettings.FetchSteamSchema),
                "Fetch achievement names/descriptions from Steam"));
            panel.Children.Add(Check(nameof(PluginSettings.CheckForUpdates),
                "Check for Hydra Sync updates at startup (GitHub releases)"));

            panel.Children.Add(Row("Sync interval (minutes):", NumberBox()));
            panel.Children.Add(Row("Hydra data folder:", DataDirBox()));
            panel.Children.Add(Row("Steam Web API key (optional):", WebApiKeyBox()));

            var note = new TextBlock
            {
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                FontSize = 12,
                Text =
                    "Leave the Hydra data folder empty to auto-detect %APPDATA%\\Hydra\\hydra-db or %APPDATA%\\hydralauncher\\hydra-db. " +
                    "Only games already in your Playnite library are matched - Hydra-only titles are never imported. " +
                    "Playtime is never added on top: Hydra's total only replaces Playnite's value when it is higher.",
            };
            panel.Children.Add(note);

            var syncButton = new Button
            {
                Content = "Sync now",
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(12, 4, 12, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            syncButton.Click += (_, __) => HydraSyncPlugin.Instance?.StartSync(true);
            panel.Children.Add(syncButton);

            var undoButton = new Button
            {
                Content = "Undo playtime changes…",
                Margin = new Thickness(0, 6, 0, 0),
                Padding = new Thickness(12, 4, 12, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "Restore every modified game's playtime to its pre-sync value " +
                          "(achievements are not affected), then re-sync with the current rules.",
            };
            undoButton.Click += (_, __) => HydraSyncPlugin.Instance?.UndoPlaytimeChanges();
            panel.Children.Add(undoButton);

            Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
        }

        private static CheckBox Check(string property, string label)
        {
            var box = new CheckBox
            {
                Content = label,
                Margin = new Thickness(0, 5, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            box.SetBinding(CheckBox.IsCheckedProperty, new Binding(property));
            return box;
        }

        private static FrameworkElement Row(string label, FrameworkElement input)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 10, 0, 0),
            };
            row.Children.Add(new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            });
            row.Children.Add(input);
            return row;
        }

        private static TextBox NumberBox()
        {
            var box = new TextBox { Width = 60, VerticalAlignment = VerticalAlignment.Center };
            box.SetBinding(TextBox.TextProperty, new Binding(nameof(PluginSettings.SyncIntervalMinutes))
            {
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            });
            return box;
        }

        private static TextBox DataDirBox()
        {
            var box = new TextBox
            {
                Width = 320,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Path to Hydra's hydra-db folder (empty = auto-detect %APPDATA%\\Hydra or %APPDATA%\\hydralauncher)",
            };
            box.SetBinding(TextBox.TextProperty, new Binding(nameof(PluginSettings.HydraDataDir))
            {
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            });
            return box;
        }

        private static TextBox WebApiKeyBox()
        {
            var box = new TextBox
            {
                Width = 320,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Optional Steam Web API key for full achievement schemas (names, descriptions, icons). " +
                          "Get one at https://steamcommunity.com/dev/apikey — leave empty to use local definition files only.",
            };
            box.SetBinding(TextBox.TextProperty, new Binding(nameof(PluginSettings.SteamWebApiKey))
            {
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            });
            return box;
        }
    }
}
