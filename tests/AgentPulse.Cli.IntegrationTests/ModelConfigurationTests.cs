using System.Text;
using System.Xml.Linq;
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
        const string errorBodyTimeoutVariable =
            "AgentPulse__Model__ErrorBodyReadTimeout";
        var originalBaseUrl = Environment.GetEnvironmentVariable(baseUrlVariable);
        var originalModel = Environment.GetEnvironmentVariable(modelVariable);
        var originalErrorBodyTimeout = Environment.GetEnvironmentVariable(
            errorBodyTimeoutVariable);

        try
        {
            Environment.SetEnvironmentVariable(baseUrlVariable, "https://environment.example/v1");
            Environment.SetEnvironmentVariable(modelVariable, "environment-model");
            Environment.SetEnvironmentVariable(
                errorBodyTimeoutVariable,
                "00:00:07");
            using var baseJson = JsonStream(
                """
                {
                  "AgentPulse": {
                    "Model": {
                      "BaseUrl": "https://json.example/v1",
                      "Model": "json-model",
                      "ChatCompletionsPath": "json/chat/completions",
                      "MaxCompletionTokens": 1024,
                      "ErrorBodyReadTimeout": "00:00:09"
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
            Assert.Equal(TimeSpan.FromSeconds(7), options.ErrorBodyReadTimeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable(baseUrlVariable, originalBaseUrl);
            Environment.SetEnvironmentVariable(modelVariable, originalModel);
            Environment.SetEnvironmentVariable(
                errorBodyTimeoutVariable,
                originalErrorBodyTimeout);
        }
    }

    [Fact]
    public void Host_loads_environment_json_and_environment_variable_with_standard_precedence()
    {
        const string modelVariable = "AgentPulse__Model__Model";
        var originalModel = Environment.GetEnvironmentVariable(modelVariable);
        var root = Path.Combine(
            Path.GetTempPath(),
            "AgentPulse.ConfigurationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            File.WriteAllText(
                Path.Combine(root, "appsettings.json"),
                """
                {
                  "AgentPulse": {
                    "Model": {
                      "BaseUrl": "https://base-json.example/v1",
                      "Model": "base-json-model"
                    }
                  }
                }
                """);
            File.WriteAllText(
                Path.Combine(root, "appsettings.Test.json"),
                """
                {
                  "AgentPulse": {
                    "Model": {
                      "BaseUrl": "https://environment-json.example/v1",
                      "Model": "environment-json-model"
                    }
                  }
                }
                """);

            Environment.SetEnvironmentVariable(modelVariable, null);
            var environmentBuilder = AgentPulseHost.CreateBuilder(
                new TestConsole(),
                contentRootPath: root,
                environmentName: "Test");
            Assert.Equal(
                "https://environment-json.example/v1",
                environmentBuilder.Configuration[$"{OpenAiCompatibleModelOptions.SectionName}:BaseUrl"]);
            Assert.Equal(
                "environment-json-model",
                environmentBuilder.Configuration[$"{OpenAiCompatibleModelOptions.SectionName}:Model"]);

            Environment.SetEnvironmentVariable(modelVariable, "environment-variable-model");
            var variableBuilder = AgentPulseHost.CreateBuilder(
                new TestConsole(),
                contentRootPath: root,
                environmentName: "Test");
            Assert.Equal(
                "environment-variable-model",
                variableBuilder.Configuration[$"{OpenAiCompatibleModelOptions.SectionName}:Model"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(modelVariable, originalModel);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Cli_project_copies_base_and_environment_appsettings_to_build_and_publish()
    {
        var projectPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "AgentPulse.Cli",
            "AgentPulse.Cli.csproj");
        var project = XDocument.Load(projectPath);
        var noneItems = project
            .Descendants("None")
            .ToDictionary(
                item => (string?)item.Attribute("Update") ?? string.Empty,
                StringComparer.Ordinal);

        AssertCopyMetadata(noneItems, "appsettings.json");
        AssertCopyMetadata(noneItems, "appsettings.*.json");
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
                  "ErrorBodyReadTimeout": "00:00:09",
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
        Assert.Equal(TimeSpan.FromSeconds(9), options.ErrorBodyReadTimeout);
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

    private static void AssertCopyMetadata(
        IReadOnlyDictionary<string, XElement> noneItems,
        string updateValue)
    {
        Assert.True(
            noneItems.TryGetValue(updateValue, out var item),
            $"Expected CLI project item '{updateValue}' to exist.");
        Assert.Equal(
            "PreserveNewest",
            item.Element("CopyToOutputDirectory")?.Value);
        Assert.Equal(
            "PreserveNewest",
            item.Element("CopyToPublishDirectory")?.Value);
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
