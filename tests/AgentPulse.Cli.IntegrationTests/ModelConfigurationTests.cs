using System.Text;
using AgentPulse.Cli.Configuration;
using AgentPulse.Cli.Hosting;
using AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;
using Microsoft.Extensions.Configuration;

namespace AgentPulse.Cli.IntegrationTests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class ModelConfigurationTests
{
    [Fact]
    public void Json_environment_json_and_environment_variables_follow_documented_precedence()
    {
        const string baseUrlVariable = "AgentPulse__Model__BaseUrl";
        const string modelVariable = "AgentPulse__Model__Model";
        var originalBaseUrl = Environment.GetEnvironmentVariable(baseUrlVariable);
        var originalModel = Environment.GetEnvironmentVariable(modelVariable);

        try
        {
            Environment.SetEnvironmentVariable(baseUrlVariable, "https://environment.example/v1");
            Environment.SetEnvironmentVariable(modelVariable, "environment-model");
            using var baseJson = JsonStream(
                """
                {
                  "AgentPulse": {
                    "Model": {
                      "BaseUrl": "https://json.example/v1",
                      "Model": "json-model",
                      "ChatCompletionsPath": "json/chat/completions",
                      "MaxCompletionTokens": 1024
                    }
                  }
                }
                """);
            using var environmentJson = JsonStream(
                """
                {
                  "AgentPulse": {
                    "Model": {
                      "BaseUrl": "https://environment-json.example/v1",
                      "Model": "environment-json-model",
                      "ChatCompletionsPath": "environment/chat/completions"
                    }
                  }
                }
                """);
            var configuration = new ConfigurationBuilder()
                .AddJsonStream(baseJson)
                .AddJsonStream(environmentJson)
                .AddEnvironmentVariables()
                .Build();
            var options = new OpenAiCompatibleModelOptions();

            configuration.GetSection(OpenAiCompatibleModelOptions.SectionName).Bind(options);
            options.Validate();

            Assert.Equal("https://environment.example/v1", options.BaseUrl);
            Assert.Equal("environment-model", options.Model);
            Assert.Equal("environment/chat/completions", options.ChatCompletionsPath);
            Assert.Equal(1024, options.MaxCompletionTokens);
        }
        finally
        {
            Environment.SetEnvironmentVariable(baseUrlVariable, originalBaseUrl);
            Environment.SetEnvironmentVariable(modelVariable, originalModel);
        }
    }

    [Fact]
    public void Generic_model_configuration_binds_all_supported_non_secret_settings()
    {
        using var json = JsonStream(
            """
            {
              "AgentPulse": {
                "Model": {
                  "BaseUrl": "https://provider.example/v2",
                  "ChatCompletionsPath": "responses/chat/completions",
                  "Model": "provider-model",
                  "AuthenticationMode": "ApiKeyHeader",
                  "ApiKeyHeaderName": "x-provider-key",
                  "ApiKeyEnvironmentVariable": "PROVIDER_API_KEY",
                  "MaxCompletionTokens": 8192,
                  "ThinkingMode": "not-sent",
                  "IncludeThinkingConfiguration": false,
                  "FirstByteTimeout": "00:00:12",
                  "StreamIdleTimeout": "00:00:34",
                  "ApiKey": "must-not-bind"
                }
              }
            }
            """);
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(json)
            .Build();
        var options = new OpenAiCompatibleModelOptions();

        configuration.GetSection(OpenAiCompatibleModelOptions.SectionName).Bind(options);
        options.Validate();

        Assert.Equal("https://provider.example/v2", options.BaseUrl);
        Assert.Equal("responses/chat/completions", options.ChatCompletionsPath);
        Assert.Equal("provider-model", options.Model);
        Assert.Equal(OpenAiCompatibleAuthenticationMode.ApiKeyHeader, options.AuthenticationMode);
        Assert.Equal("x-provider-key", options.ApiKeyHeaderName);
        Assert.Equal("PROVIDER_API_KEY", options.ApiKeyEnvironmentVariable);
        Assert.Equal(8192, options.MaxCompletionTokens);
        Assert.False(options.IncludeThinkingConfiguration);
        Assert.Equal(TimeSpan.FromSeconds(12), options.FirstByteTimeout);
        Assert.Equal(TimeSpan.FromSeconds(34), options.StreamIdleTimeout);
        Assert.DoesNotContain(
            typeof(OpenAiCompatibleModelOptions).GetProperties(),
            property => string.Equals(property.Name, "ApiKey", StringComparison.Ordinal));
    }

    [Fact]
    public void Host_fails_fast_for_invalid_model_configuration()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AgentPulseHost.CreateBuilder(
                new TestConsole(),
                configuration => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        [$"{OpenAiCompatibleModelOptions.SectionName}:BaseUrl"] =
                            "http://provider.example/v1",
                        ["AgentPulse:Persistence:DatabasePath"] = Path.Combine(
                            Path.GetTempPath(),
                            "agentpulse-invalid-model-options",
                            Guid.NewGuid().ToString("N"),
                            "agentpulse.db"),
                    })));

        Assert.Contains("HTTPS", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cli_appsettings_uses_the_generic_model_section_and_contains_no_api_key_value()
    {
        var appSettingsPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "AgentPulse.Cli",
            "appsettings.json");
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(appSettingsPath, optional: false, reloadOnChange: false)
            .Build();
        var section = configuration.GetSection(OpenAiCompatibleModelOptions.SectionName);

        Assert.True(section.Exists());
        Assert.Equal(OpenAiCompatibleModelOptions.XiaomiDefaultBaseUrl, section["BaseUrl"]);
        Assert.Null(section["ApiKey"]);
        Assert.Null(configuration["AgentPulse:Xiaomi:ApiKey"]);
        Assert.Equal("agentpulse", configuration[$"{CliOptions.SectionName}:ApplicationName"]);
    }

    private static MemoryStream JsonStream(string json)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(json));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AgentPulse.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
