using Newtonsoft.Json;
using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace assistant
{
    public class PathConfiguration
    {
        public string ModelsPath { get; set; } = "Models";
        public string ControllersPath { get; set; } = "Controllers";
        public string ViewsPath { get; set; } = "Views";
        public string ServicesPath { get; set; } = "Services";
        public string HubsPath { get; set; } = "Hubs";
        public string MiddlewarePath { get; set; } = "Middleware";

        private static string ConfigFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VSAssistant",
            "config.json");

        public static PathConfiguration Load()
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    var json = File.ReadAllText(ConfigFilePath);
                    return System.Text.Json.JsonSerializer.Deserialize<PathConfiguration>(json);
                }
            }
            catch { }

            return new PathConfiguration();
        }

        public void Save()
        {
            try
            {
                var directory = Path.GetDirectoryName(ConfigFilePath);
                Directory.CreateDirectory(directory);

                var json = System.Text.Json.JsonSerializer.Serialize(this, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(ConfigFilePath, json);
            }
            catch { }
        }
    }

    public partial class ConfigurationWindow : Window
    {
        public PathConfiguration Configuration { get; set; }

        public ConfigurationWindow(PathConfiguration config)
        {
            Configuration = new PathConfiguration
            {
                ModelsPath = config.ModelsPath,
                ControllersPath = config.ControllersPath,
                ViewsPath = config.ViewsPath,
                ServicesPath = config.ServicesPath,
                HubsPath = config.HubsPath,
                MiddlewarePath = config.MiddlewarePath
            };

            InitializeComponent();
            DataContext = Configuration;
        }

        private void InitializeComponent()
        {
            Title = "Configure Paths";
            Width = 500;
            Height = 400;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;

            var mainGrid = new Grid();
            mainGrid.Margin = new Thickness(20);

            // Define rows
            for (int i = 0; i < 8; i++)
            {
                mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            // Style for labels
            var labelStyle = new Style(typeof(TextBlock));
            labelStyle.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(0, 10, 0, 5)));
            labelStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));

            // Style for textboxes
            var textBoxStyle = new Style(typeof(TextBox));
            textBoxStyle.Setters.Add(new Setter(TextBox.PaddingProperty, new Thickness(5)));
            textBoxStyle.Setters.Add(new Setter(TextBox.FontSizeProperty, 12.0));

            // Models Path
            AddConfigRow(mainGrid, 0, "Models Path:", nameof(PathConfiguration.ModelsPath),
                labelStyle, textBoxStyle);

            // Controllers Path
            AddConfigRow(mainGrid, 1, "Controllers Path:", nameof(PathConfiguration.ControllersPath),
                labelStyle, textBoxStyle);

            // Views Path
            AddConfigRow(mainGrid, 2, "Views Path:", nameof(PathConfiguration.ViewsPath),
                labelStyle, textBoxStyle);

            // Services Path
            AddConfigRow(mainGrid, 3, "Services Path:", nameof(PathConfiguration.ServicesPath),
                labelStyle, textBoxStyle);

            // Hubs Path
            AddConfigRow(mainGrid, 4, "Hubs Path:", nameof(PathConfiguration.HubsPath),
                labelStyle, textBoxStyle);

            // Middleware Path
            AddConfigRow(mainGrid, 5, "Middleware Path:", nameof(PathConfiguration.MiddlewarePath),
                labelStyle, textBoxStyle);

            // Buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };

            var saveButton = new Button
            {
                Content = "Save",
                Width = 80,
                Height = 30,
                Margin = new Thickness(0, 0, 10, 0),
                IsDefault = true
            };
            saveButton.Click += SaveButton_Click;

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 80,
                Height = 30,
                IsCancel = true
            };
            cancelButton.Click += CancelButton_Click;

            buttonPanel.Children.Add(saveButton);
            buttonPanel.Children.Add(cancelButton);

            Grid.SetRow(buttonPanel, 7);
            mainGrid.Children.Add(buttonPanel);

            Content = mainGrid;
        }

        private void AddConfigRow(Grid grid, int row, string labelText, string bindingPath,
            Style labelStyle, Style textBoxStyle)
        {
            var label = new TextBlock
            {
                Text = labelText,
                Style = labelStyle
            };
            Grid.SetRow(label, row);
            grid.Children.Add(label);

            var textBox = new TextBox
            {
                Style = textBoxStyle
            };
            textBox.SetBinding(TextBox.TextProperty, bindingPath);
            Grid.SetRow(textBox, row);
            grid.Children.Add(textBox);

            grid.RowDefinitions[row].Height = new GridLength(60);
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}