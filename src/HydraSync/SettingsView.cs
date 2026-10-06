using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using HydraSync.Sync;

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
                "Sync achievements from achievement files on disk"));
            panel.Children.Add(Check(nameof(PluginSettings.WriteToPlayniteAchievements),
                "Export achievements to Playnite Achievements"));
            panel.Children.Add(Check(nameof(PluginSettings.ImportAchievementsImmediately),
                "Show new achievements without restarting Playnite (needs Playnite Achievements)"));
            panel.Children.Add(Check(nameof(PluginSettings.MergeWithPlayniteAchievements),
                "Merge with existing Playnite Achievements data instead of replacing it"));
            panel.Children.Add(Check(nameof(PluginSettings.FetchSteamSchema),
                "Fetch achievement names/descriptions from Steam (by AppID, or by title when unknown)"));
            panel.Children.Add(Check(nameof(PluginSettings.CheckForUpdates),
                "Check for Hydra Sync updates at startup (GitHub releases)"));
            panel.Children.Add(Check(nameof(PluginSettings.PushPlaytimeToHowLongToBeat),
                "After raising a game's playtime, have HowLongToBeat submit the new total"));

            panel.Children.Add(Row("Playtime:", PlaytimeModeBox()));
            panel.Children.Add(Check(nameof(PluginSettings.AutoSync),
                "Auto-sync (once shortly after Playnite starts, then on the interval below)"));
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
                    "By default playtime is never added on top: Hydra's total only replaces Playnite's value when it " +
                    "is higher. Pick the other playtime mode above to add Hydra's new time instead. " +
                    "Auto-sync is off by default - until you enable it, syncing only runs when you pick " +
                    "\"Sync now\" from the @Hydra Sync menu. " +
                    "The HowLongToBeat option is off by default too; it needs HowLongToBeat installed and " +
                    "logged in, and only pushes games whose playtime the sync actually raised. " +
                    "Achievements need Playnite Achievements; without it they are written but not shown.",
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

        private static ComboBox PlaytimeModeBox()
        {
            var box = new ComboBox
            {
                Width = 320,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Hydra wins: Playnite's playtime is replaced only when Hydra's total is higher. " +
                          "Add Hydra's playtime: only the time Hydra gained since the last sync is added, " +
                          "leaving Playnite's own sessions alone (the first sync records a baseline).",
            };

            box.Items.Add(new PlaytimeModeItem
            {
                Mode = PlaytimeMode.HydraWins,
                Label = "Hydra wins (replace when larger)",
            });
            box.Items.Add(new PlaytimeModeItem
            {
                Mode = PlaytimeMode.AddHydraIncrements,
                Label = "Add Hydra's playtime since the last sync",
            });

            box.DisplayMemberPath = "Label";
            box.SelectedValuePath = "Mode";
            box.SetBinding(ComboBox.SelectedValueProperty,
                new Binding(nameof(PluginSettings.PlaytimeMode))
                {
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                });

            return box;
        }

        private static TextBox NumberBox()
        {
            var box = new TextBox { Width = 60, VerticalAlignment = VerticalAlignment.Center };
            box.ToolTip = "Only used when auto-sync is enabled.";
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

        private class PlaytimeModeItem
        {
            public PlaytimeMode Mode { get; set; }
            public string Label { get; set; }

            public override string ToString()
            {
                return Label;
            }
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
