using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;

namespace assistant
{
    public class CommandProcessor
    {
        private readonly PathConfiguration _config;
        public List<string> PendingFileSelection { get; set; }

        public CommandProcessor(PathConfiguration config)
        {
            _config = config;
        }

        public async Task<List<string>> FindFiles(string searchTerm)
        {
            return await Task.Run(() =>
            {
                var solutionPath = GetSolutionPath();
                if (string.IsNullOrEmpty(solutionPath))
                    return new List<string>();

                var allFiles = Directory.GetFiles(solutionPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => !f.Contains("\\bin\\") &&
                               !f.Contains("\\obj\\") &&
                               !f.Contains("\\.vs\\") &&
                               !f.Contains("\\packages\\") &&
                               !f.Contains("\\node_modules\\"))
                    .Where(f => Path.GetFileName(f).ToLower().Contains(searchTerm.ToLower()))
                    .OrderBy(f => Path.GetFileName(f).Length)
                    .ThenBy(f => f)
                    .ToList();

                return allFiles;
            });
        }

        public async Task<CreateFileResult> CreateFile(string type, string name)
        {
            return await Task.Run(async () =>
            {
                var solutionPath = GetSolutionPath();
                if (string.IsNullOrEmpty(solutionPath))
                {
                    return new CreateFileResult
                    {
                        Success = false,
                        Message = "No solution is currently open"
                    };
                }

                switch (type.ToLower())
                {
                    case "model":
                        return await CreateModelFile(solutionPath, name);
                    case "controller":
                        return await CreateControllerFile(solutionPath, name);
                    case "view":
                        return await CreateViewFile(solutionPath, name);
                    case "service":
                        return await CreateServiceFile(solutionPath, name);
                    case "hub":
                        return await CreateHubFile(solutionPath, name);
                    case "middleware":
                        return await CreateMiddlewareFile(solutionPath, name);
                    case "all":
                        return await CreateAllFiles(solutionPath, name);
                    default:
                        return new CreateFileResult
                        {
                            Success = false,
                            Message = $"Unknown file type: {type}"
                        };
                }
            });
        }

        private async Task<CreateFileResult> CreateModelFile(string solutionPath, string name)
        {
            var directory = Path.Combine(solutionPath, _config.ModelsPath);
            Directory.CreateDirectory(directory);

            var fileName = $"{name}.cs";
            var filePath = Path.Combine(directory, fileName);

            if (File.Exists(filePath))
            {
                return new CreateFileResult
                {
                    Success = false,
                    Message = $"Model file '{fileName}' already exists"
                };
            }

            var content = GenerateModelTemplate(name);
            await Task.Run(() => File.WriteAllText(filePath, content));

            return new CreateFileResult
            {
                Success = true,
                Message = $"✓ Created model: {fileName}",
                FilePath = filePath
            };
        }

        private async Task<CreateFileResult> CreateControllerFile(string solutionPath, string name)
        {
            var directory = Path.Combine(solutionPath, _config.ControllersPath);
            Directory.CreateDirectory(directory);

            var fileName = $"{name}Controller.cs";
            var filePath = Path.Combine(directory, fileName);

            if (File.Exists(filePath))
            {
                return new CreateFileResult
                {
                    Success = false,
                    Message = $"Controller file '{fileName}' already exists"
                };
            }

            var content = GenerateControllerTemplate(name);
            await Task.Run(() => File.WriteAllText(filePath, content));

            return new CreateFileResult
            {
                Success = true,
                Message = $"✓ Created controller: {fileName}",
                FilePath = filePath
            };
        }

        private async Task<CreateFileResult> CreateViewFile(string solutionPath, string name)
        {
            var directory = Path.Combine(solutionPath, _config.ViewsPath, name);
            Directory.CreateDirectory(directory);

            var fileName = "Index.cshtml";
            var filePath = Path.Combine(directory, fileName);

            if (File.Exists(filePath))
            {
                return new CreateFileResult
                {
                    Success = false,
                    Message = $"View file '{name}/{fileName}' already exists"
                };
            }

            var content = GenerateViewTemplate(name);
            await Task.Run(() => File.WriteAllText(filePath, content));

            return new CreateFileResult
            {
                Success = true,
                Message = $"✓ Created view: {name}/{fileName}",
                FilePath = filePath
            };
        }

        private async Task<CreateFileResult> CreateServiceFile(string solutionPath, string name)
        {
            var directory = Path.Combine(solutionPath, _config.ServicesPath);
            Directory.CreateDirectory(directory);

            var results = new List<string>();
            var createdFiles = new List<string>();

            // Create interface
            var interfaceName = $"I{name}Service.cs";
            var interfacePath = Path.Combine(directory, interfaceName);
            if (!File.Exists(interfacePath))
            {
                var interfaceContent = GenerateServiceInterfaceTemplate(name);
                await Task.Run(() => File.WriteAllText(interfacePath, interfaceContent));
                results.Add($"✓ Created interface: {interfaceName}");
                createdFiles.Add(interfacePath);
            }

            // Create implementation
            var fileName = $"{name}Service.cs";
            var filePath = Path.Combine(directory, fileName);
            if (!File.Exists(filePath))
            {
                var content = GenerateServiceTemplate(name);
                await Task.Run(() => File.WriteAllText(filePath, content));
                results.Add($"✓ Created service: {fileName}");
                createdFiles.Add(filePath);
            }

            return new CreateFileResult
            {
                Success = true,
                Message = string.Join("\n", results),
                FilePath = createdFiles.LastOrDefault()
            };
        }

        private async Task<CreateFileResult> CreateHubFile(string solutionPath, string name)
        {
            var directory = Path.Combine(solutionPath, _config.HubsPath);
            Directory.CreateDirectory(directory);

            var fileName = $"{name}Hub.cs";
            var filePath = Path.Combine(directory, fileName);

            if (File.Exists(filePath))
            {
                return new CreateFileResult
                {
                    Success = false,
                    Message = $"Hub file '{fileName}' already exists"
                };
            }

            var content = GenerateHubTemplate(name);
            await Task.Run(() => File.WriteAllText(filePath, content));

            return new CreateFileResult
            {
                Success = true,
                Message = $"✓ Created hub: {fileName}",
                FilePath = filePath
            };
        }

        private async Task<CreateFileResult> CreateMiddlewareFile(string solutionPath, string name)
        {
            var directory = Path.Combine(solutionPath, _config.MiddlewarePath);
            Directory.CreateDirectory(directory);

            var fileName = $"{name}Middleware.cs";
            var filePath = Path.Combine(directory, fileName);

            if (File.Exists(filePath))
            {
                return new CreateFileResult
                {
                    Success = false,
                    Message = $"Middleware file '{fileName}' already exists"
                };
            }

            var content = GenerateMiddlewareTemplate(name);

            await Task.Run(() => File.WriteAllText(filePath, content));

            return new CreateFileResult
            {
                Success = true,
                Message = $"✓ Created middleware: {fileName}",
                FilePath = filePath
            };
        }

        private async Task<CreateFileResult> CreateAllFiles(string solutionPath, string name)
        {
            var results = new List<string>();
            var lastFilePath = "";

            var modelResult = await CreateModelFile(solutionPath, name);
            results.Add(modelResult.Message);
            if (modelResult.Success) lastFilePath = modelResult.FilePath;

            var controllerResult = await CreateControllerFile(solutionPath, name);
            results.Add(controllerResult.Message);
            if (controllerResult.Success) lastFilePath = controllerResult.FilePath;

            var viewResult = await CreateViewFile(solutionPath, name);
            results.Add(viewResult.Message);

            var serviceResult = await CreateServiceFile(solutionPath, name);
            results.Add(serviceResult.Message);

            return new CreateFileResult
            {
                Success = true,
                Message = string.Join("\n", results),
                FilePath = lastFilePath
            };
        }

        private string GetSolutionPath()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;

            if (dte?.Solution?.FullName != null)
            {
                return Path.GetDirectoryName(dte.Solution.FullName);
            }

            return null;
        }

        // Template generation methods
        private string GenerateModelTemplate(string name)
        {
            return $@"using System;
using System.ComponentModel.DataAnnotations;

namespace YourNamespace.Models
{{
    public class {name}
    {{
        public int Id {{ get; set; }}
        
        [Required]
        public string Name {{ get; set; }}
        
        public DateTime CreatedAt {{ get; set; }}
        public DateTime? UpdatedAt {{ get; set; }}
    }}
}}";
        }

        private string GenerateControllerTemplate(string name)
        {
            return $@"using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

namespace YourNamespace.Controllers
{{
    public class {name}Controller : Controller
    {{
        private readonly I{name}Service _{name.ToLower()}Service;

        public {name}Controller(I{name}Service {name.ToLower()}Service)
        {{
            _{name.ToLower()}Service = {name.ToLower()}Service;
        }}

        public async Task<IActionResult> Index()
        {{
            return View();
        }}

        public async Task<IActionResult> Details(int id)
        {{
            return View();
        }}

        [HttpPost]
        public async Task<IActionResult> Create({name} model)
        {{
            if (ModelState.IsValid)
            {{
                // Add logic here
                return RedirectToAction(nameof(Index));
            }}
            return View(model);
        }}
    }}
}}";
        }

        private string GenerateViewTemplate(string name)
        {
            return $@"@model YourNamespace.Models.{name}

@{{
    ViewData[""Title""] = ""{name}"";
}}

<h1>@ViewData[""Title""]</h1>

<div class=""container"">
    <p>This is the {name} view.</p>
</div>

@section Scripts {{
    @{{await Html.RenderPartialAsync(""_ValidationScriptsPartial"");}}
}}";
        }

        private string GenerateServiceInterfaceTemplate(string name)
        {
            return $@"using System.Collections.Generic;
using System.Threading.Tasks;

namespace YourNamespace.Services
{{
    public interface I{name}Service
    {{
        Task<IEnumerable<{name}>> GetAllAsync();
        Task<{name}> GetByIdAsync(int id);
        Task<{name}> CreateAsync({name} entity);
        Task<{name}> UpdateAsync({name} entity);
        Task<bool> DeleteAsync(int id);
    }}
}}";
        }

        private string GenerateServiceTemplate(string name)
        {
            return $@"using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace YourNamespace.Services
{{
    public class {name}Service : I{name}Service
    {{
        public async Task<IEnumerable<{name}>> GetAllAsync()
        {{
            // Implementation here
            throw new NotImplementedException();
        }}

        public async Task<{name}> GetByIdAsync(int id)
        {{
            // Implementation here
            throw new NotImplementedException();
        }}

        public async Task<{name}> CreateAsync({name} entity)
        {{
            // Implementation here
            throw new NotImplementedException();
        }}

        public async Task<{name}> UpdateAsync({name} entity)
        {{
            // Implementation here
            throw new NotImplementedException();
        }}

        public async Task<bool> DeleteAsync(int id)
        {{
            // Implementation here
            throw new NotImplementedException();
        }}
    }}
}}";
        }

        private string GenerateHubTemplate(string name)
        {
            return $@"using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

namespace YourNamespace.Hubs
{{
    public class {name}Hub : Hub
    {{
        public async Task SendMessage(string user, string message)
        {{
            await Clients.All.SendAsync(""ReceiveMessage"", user, message);
        }}

        public override async Task OnConnectedAsync()
        {{
            await base.OnConnectedAsync();
        }}

        public override async Task OnDisconnectedAsync(Exception exception)
        {{
            await base.OnDisconnectedAsync(exception);
        }}
    }}
}}";
        }

        private string GenerateMiddlewareTemplate(string name)
        {
            return $@"using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;

namespace YourNamespace.Middleware
{{
    public class {name}Middleware
    {{
        private readonly RequestDelegate _next;

        public {name}Middleware(RequestDelegate next)
        {{
            _next = next;
        }}

        public async Task InvokeAsync(HttpContext context)
        {{
            // Logic before the next middleware
            
            await _next(context);
            
            // Logic after the next middleware
        }}
    }}

    public static class {name}MiddlewareExtensions
    {{
        public static IApplicationBuilder Use{name}(this IApplicationBuilder builder)
        {{
            return builder.UseMiddleware<{name}Middleware>();
        }}
    }}
}}";
        }
    }

    public class CreateFileResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string FilePath { get; set; }
    }
}