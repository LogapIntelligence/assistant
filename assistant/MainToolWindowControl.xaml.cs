using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace assistant
{
    public partial class MainToolWindowControl : System.Windows.Controls.UserControl
    {
        private ObservableCollection<ConsoleMessage> _messages;
        private List<string> _commandHistory;
        private int _historyIndex;
        private OllamaService _ollamaService;
        private CommandProcessor _commandProcessor;
        private PathConfiguration _pathConfig;

        public MainToolWindowControl()
        {
            InitializeComponent();
            InitializeConsole();
        }

        private void InitializeConsole()
        {
            _messages = new ObservableCollection<ConsoleMessage>();
            _commandHistory = new List<string>();
            _historyIndex = -1;
            _ollamaService = new OllamaService();
            _pathConfig = PathConfiguration.Load();
            _commandProcessor = new CommandProcessor(_pathConfig);

            ConsoleOutput.ItemsSource = _messages;

            AddMessage("AI Assistant Console Initialized", MessageType.Success);
            AddMessage("Type 'help' for available commands", MessageType.Info);

            // Check Ollama connection
            _ = CheckOllamaConnection();
        }

        private async Task CheckOllamaConnection()
        {
            var isConnected = await _ollamaService.CheckConnection();
            await Dispatcher.InvokeAsync(() =>
            {
                ConnectionStatus.Text = isConnected ? "● Connected" : "● Disconnected";
                ConnectionStatus.Foreground = isConnected ?
                    new SolidColorBrush(Color.FromRgb(0, 255, 65)) :
                    new SolidColorBrush(Color.FromRgb(255, 65, 65));

                if (!isConnected)
                {
                    AddMessage("⚠ Ollama is not running. Please start Ollama for AI features.", MessageType.Error);
                }
            });
        }

        private void CommandInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ProcessCommand();
            }
            else if (e.Key == Key.Up)
            {
                NavigateHistory(-1);
            }
            else if (e.Key == Key.Down)
            {
                NavigateHistory(1);
            }
        }

        private void NavigateHistory(int direction)
        {
            if (_commandHistory.Count == 0) return;

            _historyIndex += direction;
            _historyIndex = Math.Max(0, Math.Min(_commandHistory.Count - 1, _historyIndex));

            CommandInput.Text = _commandHistory[_historyIndex];
            CommandInput.CaretIndex = CommandInput.Text.Length;
        }

        private async void ProcessCommand()
        {
            var command = CommandInput.Text.Trim();
            if (string.IsNullOrEmpty(command)) return;

            _commandHistory.Add(command);
            _historyIndex = _commandHistory.Count;

            AddMessage($"> {command}", MessageType.Command);
            CommandInput.Clear();

            await ExecuteCommand(command);
        }

        private async Task ExecuteCommand(string command)
        {
            StatusText.Text = "Processing...";

            try
            {
                // Check if this is a file selection response
                if (_commandProcessor.PendingFileSelection != null &&
                    int.TryParse(command, out int selection))
                {
                    if (selection > 0 && selection <= _commandProcessor.PendingFileSelection.Count)
                    {
                        var selectedFile = _commandProcessor.PendingFileSelection[selection - 1];
                        await OpenFileInEditor(selectedFile);
                        AddMessage($"Opened: {Path.GetFileName(selectedFile)}", MessageType.Success);
                        _commandProcessor.PendingFileSelection = null;
                    }
                    else
                    {
                        AddMessage($"Invalid selection. Please choose 1-{_commandProcessor.PendingFileSelection.Count}", MessageType.Error);
                    }
                    return;
                }

                var parts = command.Split(' ');
                var cmd = parts[0].ToLower();

                switch (cmd)
                {
                    case "help":
                        ShowHelp();
                        break;

                    case "goto":
                        await GotoFile(parts.Skip(1).ToArray());
                        break;

                    case "make":
                        await MakeFile(parts.Skip(1).ToArray());
                        break;

                    case "config":
                        ShowConfiguration();
                        break;

                    case "clear":
                        ClearConsole();
                        break;

                    case "ai":
                    case "prompt":
                        await ProcessAIPrompt(string.Join(" ", parts.Skip(1)));
                        break;

                    default:
                        // Try as AI prompt if not a command
                        if (!string.IsNullOrEmpty(command))
                        {
                            await ProcessAIPrompt(command);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                AddMessage($"Error: {ex.Message}", MessageType.Error);
            }
            finally
            {
                StatusText.Text = "Ready";
            }
        }

        private void ShowHelp()
        {
            AddMessage("Available Commands:", MessageType.Info);
            AddMessage("  goto <filename>     - Navigate to file", MessageType.Info);
            AddMessage("  make model <name>   - Create a model file", MessageType.Info);
            AddMessage("  make controller <name> - Create a controller", MessageType.Info);
            AddMessage("  make view <name>    - Create a view", MessageType.Info);
            AddMessage("  make service <name> - Create a service", MessageType.Info);
            AddMessage("  make hub <name>     - Create a SignalR hub", MessageType.Info);
            AddMessage("  make middleware <name> - Create middleware", MessageType.Info);
            AddMessage("  make all <name>     - Create complete MVC set", MessageType.Info);
            AddMessage("  config              - Show/edit configuration", MessageType.Info);
            AddMessage("  clear               - Clear console", MessageType.Info);
            AddMessage("  ai/prompt <text>    - Send prompt to AI", MessageType.Info);
            AddMessage("\nOr just type any text to send to AI assistant", MessageType.Info);
        }

        private async Task GotoFile(string[] args)
        {
            if (args.Length == 0)
            {
                AddMessage("Usage: goto <filename>", MessageType.Error);
                return;
            }

            var searchTerm = string.Join(" ", args).ToLower();
            var files = await _commandProcessor.FindFiles(searchTerm);

            if (files.Count == 0)
            {
                AddMessage($"No files found matching '{searchTerm}'", MessageType.Error);
            }
            else if (files.Count == 1)
            {
                await OpenFileInEditor(files[0]);
                AddMessage($"Opened: {Path.GetFileName(files[0])}", MessageType.Success);
            }
            else
            {
                AddMessage($"Multiple files found for '{searchTerm}':", MessageType.Info);
                for (int i = 0; i < files.Count; i++)
                {
                    AddMessage($"  [{i + 1}] {files[i]}", MessageType.Info);
                }
                AddMessage("Type the number to open the file", MessageType.Info);
                // Store files for selection
                _commandProcessor.PendingFileSelection = files;
            }
        }

        private async Task MakeFile(string[] args)
        {
            if (args.Length < 2)
            {
                AddMessage("Usage: make <type> <name>", MessageType.Error);
                return;
            }

            var type = args[0].ToLower();
            var name = args[1];

            var result = await _commandProcessor.CreateFile(type, name);

            if (result.Success)
            {
                AddMessage(result.Message, MessageType.Success);
                if (!string.IsNullOrEmpty(result.FilePath))
                {
                    await OpenFileInEditor(result.FilePath);
                }
            }
            else
            {
                AddMessage(result.Message, MessageType.Error);
            }
        }

        private async Task OpenFileInEditor(string filePath)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
            if (dte != null)
            {
                dte.ItemOperations.OpenFile(filePath);
            }
        }

        private async Task ProcessAIPrompt(string prompt)
        {
            if (string.IsNullOrEmpty(prompt))
            {
                AddMessage("Please provide a prompt", MessageType.Error);
                return;
            }

            StatusText.Text = "AI Processing...";
            AddMessage("🤖 Processing your request...", MessageType.AI);

            try
            {
                // Get current file context
                var context = await GetCurrentFileContext();

                // Send to Ollama
                var response = await _ollamaService.ProcessPrompt(prompt, context);

                if (response.Success)
                {
                    AddMessage(response.Message, MessageType.AI);

                    // Apply code changes if any
                    if (response.CodeChanges != null && response.CodeChanges.Any())
                    {
                        await ApplyCodeChanges(response.CodeChanges);
                    }
                }
                else
                {
                    AddMessage($"AI Error: {response.Message}", MessageType.Error);
                }
            }
            catch (Exception ex)
            {
                AddMessage($"AI Error: {ex.Message}", MessageType.Error);
            }
        }

        private async Task<FileContext> GetCurrentFileContext()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
            if (dte?.ActiveDocument != null)
            {
                var doc = dte.ActiveDocument;
                var textDoc = doc.Object("TextDocument") as TextDocument;

                if (textDoc != null)
                {
                    var editPoint = textDoc.StartPoint.CreateEditPoint();
                    var content = editPoint.GetText(textDoc.EndPoint);

                    return new FileContext
                    {
                        FilePath = doc.FullName,
                        FileName = doc.Name,
                        Content = content,
                        Language = GetLanguageFromExtension(Path.GetExtension(doc.Name))
                    };
                }
            }

            return null;
        }

        private string GetLanguageFromExtension(string extension)
        {
            switch (extension.ToLower())
            {
                case ".cs": return "csharp";
                case ".js": return "javascript";
                case ".ts": return "typescript";
                case ".html": return "html";
                case ".css": return "css";
                case ".json": return "json";
                case ".xml": return "xml";
                case ".sql": return "sql";
                default: return "text";
            }
        }

        private async Task ApplyCodeChanges(List<CodeChange> changes)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
            if (dte?.ActiveDocument != null)
            {
                var textDoc = dte.ActiveDocument.Object("TextDocument") as TextDocument;
                if (textDoc != null)
                {
                    foreach (var change in changes.OrderByDescending(c => c.StartLine))
                    {
                        var startPoint = textDoc.CreateEditPoint();
                        startPoint.MoveToLineAndOffset(change.StartLine, 1);

                        var endPoint = textDoc.CreateEditPoint();
                        endPoint.MoveToLineAndOffset(change.EndLine + 1, 1);

                        startPoint.Delete(endPoint);
                        startPoint.Insert(change.NewContent + Environment.NewLine);
                    }

                    AddMessage($"Applied {changes.Count} code changes", MessageType.Success);
                }
            }
        }

        private void ShowConfiguration()
        {
            AddMessage("Current Configuration:", MessageType.Info);
            AddMessage($"  Models Path: {_pathConfig.ModelsPath}", MessageType.Info);
            AddMessage($"  Controllers Path: {_pathConfig.ControllersPath}", MessageType.Info);
            AddMessage($"  Views Path: {_pathConfig.ViewsPath}", MessageType.Info);
            AddMessage($"  Services Path: {_pathConfig.ServicesPath}", MessageType.Info);
            AddMessage($"  Hubs Path: {_pathConfig.HubsPath}", MessageType.Info);
            AddMessage($"  Middleware Path: {_pathConfig.MiddlewarePath}", MessageType.Info);
        }

        private void ClearConsole()
        {
            _messages.Clear();
            AddMessage("Console cleared", MessageType.Success);
        }

        private void AddMessage(string message, MessageType type)
        {
            _messages.Add(new ConsoleMessage
            {
                Message = message,
                Type = type,
                Timestamp = DateTime.Now
            });

            // Auto-scroll to bottom
            Dispatcher.InvokeAsync(() =>
            {
                OutputScrollViewer.ScrollToEnd();
            }, DispatcherPriority.Background);
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            ClearConsole();
        }

        private void ConfigButton_Click(object sender, RoutedEventArgs e)
        {
            var configWindow = new ConfigurationWindow(_pathConfig);
            if (configWindow.ShowDialog() == true)
            {
                _pathConfig = configWindow.Configuration;
                _pathConfig.Save();
                _commandProcessor = new CommandProcessor(_pathConfig);
                AddMessage("Configuration updated", MessageType.Success);
            }
        }
    }

    public class ConsoleMessage : INotifyPropertyChanged
    {
        private string _message;
        private MessageType _type;
        private DateTime _timestamp;

        public string Message
        {
            get => _message;
            set { _message = value; OnPropertyChanged(); }
        }

        public MessageType Type
        {
            get => _type;
            set { _type = value; OnPropertyChanged(); }
        }

        public DateTime Timestamp
        {
            get => _timestamp;
            set { _timestamp = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public enum MessageType
    {
        Command,
        Success,
        Error,
        Info,
        AI
    }
}