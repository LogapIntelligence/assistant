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
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
        private ContextManager _contextManager;
        private StringBuilder _streamingCodeBuffer;
        private bool _isStreaming;

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
            _contextManager = new ContextManager();
            _streamingCodeBuffer = new StringBuilder();

            ConsoleOutput.ItemsSource = _messages;

            // Bind context manager events
            _contextManager.StatusMessage += (s, msg) => AddMessage(msg, MessageType.Info);
            _contextManager.PropertyChanged += ContextManager_PropertyChanged;

            // Bind context UI
            ContextList.ItemsSource = _contextManager.ContextFiles;
            UpdateContextDisplay();

            AddMessage("AI Assistant Console Initialized", MessageType.Success);
            AddMessage("Type 'help' for available commands", MessageType.Info);

            // Check Ollama connection
            _ = CheckOllamaConnection();
        }

        private void ContextManager_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            Dispatcher.Invoke(() => UpdateContextDisplay());
        }

        private void UpdateContextDisplay()
        {
            // Update primary file display
            PrimaryFileText.Text = _contextManager.PrimaryFileName;

            // Update context count
            ContextCountText.Text = $"Context Files: {_contextManager.ContextFileCount}";

            // Show/hide context panel based on whether there's context
            ContextPanel.Visibility = (_contextManager.HasPrimaryFile || _contextManager.HasContext)
                ? Visibility.Visible
                : Visibility.Collapsed;
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
            if (_isStreaming)
            {
                if (e.Key == Key.Escape)
                {
                    _isStreaming = false;
                    AddMessage("Streaming cancelled", MessageType.Info);
                }
                return; // Ignore other input while streaming
            }

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
                        await _contextManager.OpenFileInEditor(selectedFile);
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
                    case "gogo":
                        await GotoFile(parts.Skip(1).ToArray());
                        break;

                    case "back":
                        await GoBack();
                        break;

                    case "add":
                        await AddToContext();
                        break;

                    case "setprimary":
                    case "primary":
                        await SetPrimary();
                        break;

                    case "remove":
                        RemoveFromContext(parts.Skip(1).ToArray());
                        break;

                    case "clearcontext":
                    case "clear-context":
                        ClearContext();
                        break;

                    case "context":
                        ShowContext();
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
            AddMessage("  Navigation:", MessageType.Info);
            AddMessage("    goto/gogo <filename> - Navigate to file", MessageType.Info);
            AddMessage("    back                 - Go back to previous file", MessageType.Info);
            AddMessage("", MessageType.Info);
            AddMessage("  Context Management:", MessageType.Info);
            AddMessage("    add                  - Add current file to context", MessageType.Info);
            AddMessage("    primary/setprimary   - Set current file as primary", MessageType.Info);
            AddMessage("    remove <filename>    - Remove file from context", MessageType.Info);
            AddMessage("    clearcontext         - Clear all context files", MessageType.Info);
            AddMessage("    context              - Show current context", MessageType.Info);
            AddMessage("", MessageType.Info);
            AddMessage("  File Creation:", MessageType.Info);
            AddMessage("    make model <name>    - Create a model file", MessageType.Info);
            AddMessage("    make controller <name> - Create a controller", MessageType.Info);
            AddMessage("    make view <name>     - Create a view", MessageType.Info);
            AddMessage("    make service <name>  - Create a service", MessageType.Info);
            AddMessage("    make hub <name>      - Create a SignalR hub", MessageType.Info);
            AddMessage("    make middleware <name> - Create middleware", MessageType.Info);
            AddMessage("    make all <name>      - Create complete MVC set", MessageType.Info);
            AddMessage("", MessageType.Info);
            AddMessage("  Other:", MessageType.Info);
            AddMessage("    config               - Show/edit configuration", MessageType.Info);
            AddMessage("    clear                - Clear console", MessageType.Info);
            AddMessage("    ai/prompt <text>     - Send prompt to AI", MessageType.Info);
            AddMessage("", MessageType.Info);
            AddMessage("Or just type any text to send to AI assistant", MessageType.Info);
            AddMessage("Press ESC while streaming to cancel", MessageType.Info);
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
                await _contextManager.OpenFileInEditor(files[0]);
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
                _commandProcessor.PendingFileSelection = files;
            }
        }

        private async Task GoBack()
        {
            var success = await _contextManager.NavigateBack();
            if (!success)
            {
                AddMessage("No previous file in navigation history", MessageType.Info);
            }
        }

        private async Task AddToContext()
        {
            var success = await _contextManager.AddCurrentFileToContext();
            if (success)
            {
                UpdateContextDisplay();
            }
        }

        private async Task SetPrimary()
        {
            var success = await _contextManager.SetCurrentFileAsPrimary();
            if (success)
            {
                UpdateContextDisplay();
            }
        }

        private void RemoveFromContext(string[] args)
        {
            if (args.Length == 0)
            {
                AddMessage("Usage: remove <filename>", MessageType.Error);
                return;
            }

            var fileName = string.Join(" ", args);
            var success = _contextManager.RemoveFromContext(fileName);
            if (success)
            {
                UpdateContextDisplay();
            }
            else
            {
                AddMessage($"File '{fileName}' not found in context", MessageType.Error);
            }
        }

        private void ClearContext()
        {
            _contextManager.ClearContext();
            UpdateContextDisplay();
        }

        private void ShowContext()
        {
            var summary = _contextManager.GetContextSummary();
            AddMessage(summary, MessageType.Info);
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
                    await _contextManager.OpenFileInEditor(result.FilePath);
                }
            }
            else
            {
                AddMessage(result.Message, MessageType.Error);
            }
        }

        private async Task ProcessAIPrompt(string prompt)
        {
            if (string.IsNullOrEmpty(prompt))
            {
                AddMessage("Please provide a prompt", MessageType.Error);
                return;
            }

            // Get primary file or current file
            var primaryFile = _contextManager.PrimaryFile;
            if (primaryFile == null)
            {
                primaryFile = await _contextManager.GetCurrentFileContext();
                if (primaryFile == null)
                {
                    AddMessage("No active document found. Please open a file to edit.", MessageType.Error);
                    return;
                }
            }

            StatusText.Text = "AI Streaming...";
            _isStreaming = true;
            _streamingCodeBuffer.Clear();

            AddMessage("🤖 Processing your request...", MessageType.AI);

            try
            {
                var contextFiles = _contextManager.ContextFiles.ToList();

                await _ollamaService.StreamCodeCompletion(
                    prompt,
                    primaryFile,
                    contextFiles,
                    onToken: async (token) =>
                    {
                        // Buffer the streaming code
                        _streamingCodeBuffer.Append(token);

                        // Update file content in real-time (throttled)
                        if (_streamingCodeBuffer.Length % 100 == 0) // Update every 100 chars
                        {
                            await UpdateDocumentContentStreaming(_streamingCodeBuffer.ToString());
                        }
                    },
                    onIntro: (intro) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            AddMessage($"AI: {intro}", MessageType.AI);
                        });
                    },
                    onSummary: (summary) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            // Final update with complete content
                            _ = UpdateDocumentContentStreaming(_streamingCodeBuffer.ToString());

                            AddMessage($"✓ {summary}", MessageType.Success);

                            // Calculate and show diff
                            var diff = _ollamaService.GetDiffSummary(
                                primaryFile.Content,
                                _streamingCodeBuffer.ToString()
                            );

                            if (diff.LinesAdded > 0 || diff.LinesRemoved > 0 || diff.LinesChanged > 0)
                            {
                                var stats = new List<string>();
                                if (diff.LinesAdded > 0) stats.Add($"+{diff.LinesAdded}");
                                if (diff.LinesRemoved > 0) stats.Add($"-{diff.LinesRemoved}");
                                if (diff.LinesChanged > 0) stats.Add($"~{diff.LinesChanged}");

                                AddMessage($"Changes: {string.Join(" ", stats)} lines", MessageType.Info);
                            }
                        });
                    }
                );
            }
            catch (Exception ex)
            {
                AddMessage($"AI Error: {ex.Message}", MessageType.Error);
            }
            finally
            {
                _isStreaming = false;
                StatusText.Text = "Ready";
            }
        }

        private async Task UpdateDocumentContentStreaming(string newContent)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
            if (dte?.ActiveDocument != null)
            {
                var textDoc = dte.ActiveDocument.Object("TextDocument") as TextDocument;
                if (textDoc != null)
                {
                    var editPoint = textDoc.CreateEditPoint(textDoc.StartPoint);
                    editPoint.Delete(textDoc.EndPoint);
                    editPoint.Insert(newContent);
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

        private void RemoveContextButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var fileName = button?.Tag as string;
            if (!string.IsNullOrEmpty(fileName))
            {
                _contextManager.RemoveFromContext(fileName);
                UpdateContextDisplay();
            }
        }

        private void ClearPrimaryButton_Click(object sender, RoutedEventArgs e)
        {
            _contextManager.PrimaryFile = null;
            UpdateContextDisplay();
            AddMessage("Cleared primary file", MessageType.Info);
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