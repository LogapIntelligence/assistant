using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;

namespace assistant
{
    public class ContextManager : INotifyPropertyChanged
    {
        private readonly ObservableCollection<FileContext> _contextFiles;
        private FileContext _primaryFile;
        private readonly Stack<string> _navigationHistory;
        private string _currentFilePath;

        public ObservableCollection<FileContext> ContextFiles => _contextFiles;

        public FileContext PrimaryFile
        {
            get => _primaryFile;
            set
            {
                _primaryFile = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasPrimaryFile));
                OnPropertyChanged(nameof(PrimaryFileName));
            }
        }

        public bool HasPrimaryFile => PrimaryFile != null;
        public string PrimaryFileName => PrimaryFile?.FileName ?? "None";

        public int ContextFileCount => _contextFiles.Count;
        public bool HasContext => _contextFiles.Count > 0;
        public bool CanGoBack => _navigationHistory.Count > 1;

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<string> StatusMessage;

        public ContextManager()
        {
            _contextFiles = new ObservableCollection<FileContext>();
            _navigationHistory = new Stack<string>();

            _contextFiles.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(ContextFileCount));
                OnPropertyChanged(nameof(HasContext));
            };
        }

        public async Task<bool> AddCurrentFileToContext()
        {
            var fileContext = await GetCurrentFileContext();
            if (fileContext == null)
            {
                RaiseStatusMessage("No active document to add to context");
                return false;
            }

            // Check if already in context
            if (_contextFiles.Any(f => f.FilePath == fileContext.FilePath))
            {
                RaiseStatusMessage($"{fileContext.FileName} is already in context");
                return false;
            }

            _contextFiles.Add(fileContext);
            RaiseStatusMessage($"Added {fileContext.FileName} to context");
            return true;
        }

        public async Task<bool> SetCurrentFileAsPrimary()
        {
            var fileContext = await GetCurrentFileContext();
            if (fileContext == null)
            {
                RaiseStatusMessage("No active document to set as primary");
                return false;
            }

            // Remove from context if it's there
            var existing = _contextFiles.FirstOrDefault(f => f.FilePath == fileContext.FilePath);
            if (existing != null)
            {
                _contextFiles.Remove(existing);
            }

            fileContext.IsPrimary = true;
            PrimaryFile = fileContext;
            RaiseStatusMessage($"Set {fileContext.FileName} as primary file");
            return true;
        }

        public bool RemoveFromContext(string fileName)
        {
            var file = _contextFiles.FirstOrDefault(f => f.FileName == fileName);
            if (file != null)
            {
                _contextFiles.Remove(file);
                RaiseStatusMessage($"Removed {fileName} from context");
                return true;
            }
            return false;
        }

        public void ClearContext()
        {
            _contextFiles.Clear();
            RaiseStatusMessage("Cleared all context files");
        }

        public void ClearAll()
        {
            _contextFiles.Clear();
            PrimaryFile = null;
            RaiseStatusMessage("Cleared all context and primary file");
        }

        public async Task<bool> NavigateBack()
        {
            if (_navigationHistory.Count <= 1)
            {
                RaiseStatusMessage("No previous file in history");
                return false;
            }

            // Pop current file
            _navigationHistory.Pop();

            // Get previous file
            var previousFile = _navigationHistory.Peek();

            if (File.Exists(previousFile))
            {
                await OpenFileInEditor(previousFile);
                RaiseStatusMessage($"Navigated back to {Path.GetFileName(previousFile)}");
                return true;
            }
            else
            {
                RaiseStatusMessage($"Previous file no longer exists: {Path.GetFileName(previousFile)}");
                return false;
            }
        }

        public void RecordFileNavigation(string filePath)
        {
            if (!string.IsNullOrEmpty(filePath) && filePath != _currentFilePath)
            {
                _navigationHistory.Push(filePath);
                _currentFilePath = filePath;
                OnPropertyChanged(nameof(CanGoBack));
            }
        }

        public async Task<FileContext> GetCurrentFileContext()
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

                    // Record navigation
                    RecordFileNavigation(doc.FullName);

                    return new FileContext
                    {
                        FilePath = doc.FullName,
                        FileName = doc.Name,
                        Content = content,
                        Language = GetLanguageFromExtension(Path.GetExtension(doc.Name)),
                        IsPrimary = false
                    };
                }
            }

            return null;
        }

        public async Task<bool> OpenFileInEditor(string filePath)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
            if (dte != null && File.Exists(filePath))
            {
                try
                {
                    dte.ItemOperations.OpenFile(filePath);
                    RecordFileNavigation(filePath);
                    return true;
                }
                catch (Exception ex)
                {
                    RaiseStatusMessage($"Error opening file: {ex.Message}");
                    return false;
                }
            }
            return false;
        }

        public string GetContextSummary()
        {
            var lines = new List<string>();

            if (PrimaryFile != null)
            {
                lines.Add($"Primary: {PrimaryFile.FileName}");
            }

            if (_contextFiles.Any())
            {
                lines.Add($"Context files ({_contextFiles.Count}):");
                foreach (var file in _contextFiles)
                {
                    lines.Add($"  • {file.FileName}");
                }
            }

            return lines.Any() ? string.Join("\n", lines) : "No files in context";
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
                case ".py": return "python";
                case ".java": return "java";
                case ".cpp":
                case ".cc":
                case ".cxx": return "cpp";
                case ".h":
                case ".hpp": return "cpp";
                case ".rb": return "ruby";
                case ".go": return "go";
                case ".rs": return "rust";
                case ".php": return "php";
                case ".swift": return "swift";
                case ".kt": return "kotlin";
                case ".scala": return "scala";
                case ".r": return "r";
                default: return "text";
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void RaiseStatusMessage(string message)
        {
            StatusMessage?.Invoke(this, message);
        }
    }

    public class ContextFileViewModel : INotifyPropertyChanged
    {
        private FileContext _file;
        private bool _isSelected;

        public FileContext File
        {
            get => _file;
            set
            {
                _file = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FileName));
                OnPropertyChanged(nameof(FilePath));
            }
        }

        public string FileName => _file?.FileName ?? "";
        public string FilePath => _file?.FilePath ?? "";

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}