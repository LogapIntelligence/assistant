using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace assistant
{
    public class OllamaService
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl = "http://localhost:11434";
        private readonly string _model = "phi4-mini";

        public OllamaService()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(5);
        }

        public async Task<bool> CheckConnection()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/tags");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<AIResponse> ProcessPrompt(string prompt, FileContext context)
        {
            try
            {
                var systemPrompt = BuildSystemPrompt(context);
                var fullPrompt = BuildFullPrompt(prompt, context);

                // Create structured output format for code changes
                var format = new
                {
                    response = new
                    {
                        explanation = "string",
                        changes = new[]
                        {
                            new
                            {
                                start_line = 0,
                                end_line = 0,
                                new_content = "string",
                                description = "string"
                            }
                        },
                        suggestions = new[] { "string" }
                    }
                };

                var requestBody = new
                {
                    model = _model,
                    prompt = fullPrompt,
                    system = systemPrompt,
                    format = format,
                    stream = false,
                    options = new
                    {
                        temperature = 0.7,
                        top_p = 0.9,
                        max_tokens = 2000
                    }
                };

                var json = System.Text.Json.JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync($"{_baseUrl}/api/generate", content);

                if (!response.IsSuccessStatusCode)
                {
                    return new AIResponse
                    {
                        Success = false,
                        Message = $"Ollama API error: {response.StatusCode}"
                    };
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                var ollamaResponse = System.Text.Json.JsonSerializer.Deserialize<OllamaResponse>(responseJson);

                if (ollamaResponse?.response != null)
                {
                    try
                    {
                        var structuredResponse = System.Text.Json.JsonSerializer.Deserialize<StructuredResponse>(
                            ollamaResponse.response,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        var aiResponse = new AIResponse
                        {
                            Success = true,
                            Message = structuredResponse.response.explanation
                        };

                        // Convert structured changes to CodeChange objects
                        if (structuredResponse.response.changes != null && context != null)
                        {
                            aiResponse.CodeChanges = structuredResponse.response.changes
                                .Select(c => new CodeChange
                                {
                                    StartLine = c.start_line,
                                    EndLine = c.end_line,
                                    NewContent = c.new_content,
                                    Description = c.description
                                })
                                .Where(c => c.StartLine > 0 && c.EndLine >= c.StartLine)
                                .ToList();
                        }

                        // Add suggestions to message
                        if (structuredResponse.response.suggestions != null &&
                            structuredResponse.response.suggestions.Any())
                        {
                            aiResponse.Message += "\n\n💡 Suggestions:\n" +
                                string.Join("\n", structuredResponse.response.suggestions.Select(s => $"• {s}"));
                        }

                        return aiResponse;
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        // If structured output fails, return as plain text
                        return new AIResponse
                        {
                            Success = true,
                            Message = ollamaResponse.response
                        };
                    }
                }

                return new AIResponse
                {
                    Success = false,
                    Message = "No response from Ollama"
                };
            }
            catch (HttpRequestException ex)
            {
                return new AIResponse
                {
                    Success = false,
                    Message = $"Connection error: {ex.Message}. Make sure Ollama is running."
                };
            }
            catch (Exception ex)
            {
                return new AIResponse
                {
                    Success = false,
                    Message = $"Error: {ex.Message}"
                };
            }
        }

        private string BuildSystemPrompt(FileContext context)
        {
            var prompt = @"You are an expert code assistant integrated into Visual Studio. 
You help with code generation, refactoring, and answering programming questions.
You provide structured responses with specific line-by-line code changes.

When providing code changes:
1. Specify exact line numbers for modifications
2. Provide complete replacement content
3. Include clear descriptions of changes
4. Suggest additional improvements when relevant

Focus on:
- Clean, maintainable code following best practices
- C#, .NET, ASP.NET Core, and web development
- Performance and security considerations
- Clear explanations of changes";

            if (context != null)
            {
                prompt += $"\n\nCurrent file: {context.FileName} ({context.Language})";
            }

            return prompt;
        }

        private string BuildFullPrompt(string userPrompt, FileContext context)
        {
            var prompt = userPrompt;

            if (context != null)
            {
                prompt = $@"User request: {userPrompt}

Current file: {context.FileName}
Language: {context.Language}

Current code:
```{context.Language}
{context.Content}
```

Please analyze the code and provide structured changes based on the user's request.
Include line-by-line modifications with exact line numbers.";

                // Add related files if needed
                var relatedFiles = GetRelatedFiles(context);
                if (relatedFiles.Any())
                {
                    prompt += "\n\nRelated files for context:";
                    foreach (var file in relatedFiles.Take(3))
                    {
                        prompt += $"\n- {file}";
                    }
                }
            }

            return prompt;
        }

        private List<string> GetRelatedFiles(FileContext context)
        {
            // Smart detection of related files based on naming patterns
            var relatedFiles = new List<string>();

            if (context.FileName.EndsWith("Controller.cs"))
            {
                var baseName = context.FileName.Replace("Controller.cs", "");
                relatedFiles.Add($"{baseName}Service.cs");
                relatedFiles.Add($"{baseName}.cs");
                relatedFiles.Add($"I{baseName}Service.cs");
            }
            else if (context.FileName.EndsWith("Service.cs"))
            {
                var baseName = context.FileName.Replace("Service.cs", "");
                relatedFiles.Add($"{baseName}Controller.cs");
                relatedFiles.Add($"{baseName}.cs");
                relatedFiles.Add($"I{baseName}Service.cs");
            }
            else if (context.FileName.EndsWith(".cs") && !context.FileName.Contains("Controller") && !context.FileName.Contains("Service"))
            {
                var baseName = context.FileName.Replace(".cs", "");
                relatedFiles.Add($"{baseName}Controller.cs");
                relatedFiles.Add($"{baseName}Service.cs");
                relatedFiles.Add($"{baseName}Repository.cs");
            }

            return relatedFiles;
        }
    }

    public class AIResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public List<CodeChange> CodeChanges { get; set; }
    }

    public class CodeChange
    {
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public string NewContent { get; set; }
        public string Description { get; set; }
    }

    public class FileContext
    {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string Content { get; set; }
        public string Language { get; set; }
    }

    // JSON response models
    internal class OllamaResponse
    {
        public string model { get; set; }
        public string created_at { get; set; }
        public string response { get; set; }
        public bool done { get; set; }
    }

    internal class StructuredResponse
    {
        public ResponseContent response { get; set; }
    }

    internal class ResponseContent
    {
        public string explanation { get; set; }
        public ChangeItem[] changes { get; set; }
        public string[] suggestions { get; set; }
    }

    internal class ChangeItem
    {
        public int start_line { get; set; }
        public int end_line { get; set; }
        public string new_content { get; set; }
        public string description { get; set; }
    }
}