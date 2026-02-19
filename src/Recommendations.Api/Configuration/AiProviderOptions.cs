namespace Recommendations.Api.Configuration;

public class AiProviderOptions
{
    public OpenAiOptions OpenAi { get; set; } = new();
    public AnthropicOptions Anthropic { get; set; } = new();
    public GeminiOptions Gemini { get; set; } = new();
    public AzureOpenAiOptions AzureOpenAi { get; set; } = new();
    public OpenRouterOptions OpenRouter { get; set; } = new();
}

public class OpenAiOptions
{
    public bool Enabled { get; set; } = true;
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-4o";
    public int MaxTokens { get; set; } = 2000;
    public int TimeoutSeconds { get; set; } = 30;
}

public class AnthropicOptions
{
    public bool Enabled { get; set; } = true;
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "claude-opus-4-6";
    public int MaxTokens { get; set; } = 2000;
    public int TimeoutSeconds { get; set; } = 30;
}

public class GeminiOptions
{
    public bool Enabled { get; set; } = true;
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gemini-2.0-flash";
    public int MaxTokens { get; set; } = 2000;
    public int TimeoutSeconds { get; set; } = 30;
}

public class AzureOpenAiOptions
{
    public bool Enabled { get; set; } = true;
    public string ApiKey { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string DeploymentName { get; set; } = "gpt-4o";
    public int TimeoutSeconds { get; set; } = 30;
}

public class OpenRouterOptions
{
    public bool Enabled { get; set; } = true;
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "openai/gpt-4o";
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    public int MaxTokens { get; set; } = 2000;
    public int TimeoutSeconds { get; set; } = 120;
    public string AppReferer { get; set; } = "https://recommendations.app";
    public string AppTitle { get; set; } = "Recommendations";
}
