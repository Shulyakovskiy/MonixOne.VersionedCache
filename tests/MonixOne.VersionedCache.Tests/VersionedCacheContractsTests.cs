using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MonixOne.VersionedCache;
using Xunit;

namespace MonixOne.VersionedCache.Tests;

public sealed class VersionedCacheContractsTests
{
    [Fact]
    public void CacheWriteResult_PreservesStructuredOutcome()
    {
        var result = new CacheWriteResult(CacheWriteStatus.IgnoredOlderVersion, 42);

        Assert.Equal(CacheWriteStatus.IgnoredOlderVersion, result.Status);
        Assert.Equal(42, result.CurrentVersion);
    }

    [Fact]
    public void VersionedCacheEntry_RepresentsTombstoneWithoutPayload()
    {
        var entry = new VersionedCacheEntry<string>(43, true, null);

        Assert.True(entry.IsDeleted);
        Assert.Equal(43, entry.Version);
        Assert.Null(entry.Value);
    }

    [Fact]
    public void AddVersionedCache_RegistersSingletonImplementation()
    {
        var services = new ServiceCollection();

        services.AddVersionedCache();

        var registration = Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IVersionedCache));
        Assert.Equal(ServiceLifetime.Singleton, registration.Lifetime);
        Assert.Equal(typeof(RedisVersionedCache), registration.ImplementationType);
    }

    [Fact]
    public void AddVersionedCache_RejectsMissingHostEnvironmentAtStartup()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(CreateConfiguration());
        services.AddVersionedCache();
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains(nameof(IHostEnvironment), exception.Message);
    }

    [Theory]
    [InlineData("Development", "identity-service")]
    [InlineData("Production", "profile-service")]
    public void AddVersionedCache_UsesHostEnvironmentAndConfiguredServiceName(
        string environmentName, string serviceName)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(environmentName));
        services.AddSingleton<IConfiguration>(CreateConfiguration(serviceName));
        services.AddVersionedCache();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IStartupValidator>().Validate();
        var options = provider.GetRequiredService<IOptions<VersionedCacheOptions>>().Value;

        Assert.Equal(environmentName, options.EnvironmentName);
        Assert.Equal(serviceName, options.ServiceName);
    }

    [Fact]
    public void AddVersionedCache_PreservesCustomSerializationAndHostEnvironment()
    {
        var services = new ServiceCollection();
        var serializerOptions = new JsonSerializerOptions();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment("staging"));
        services.AddSingleton<IConfiguration>(CreateConfiguration());
        services.AddVersionedCache(options => options.SerializerOptions = serializerOptions);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<VersionedCacheOptions>>().Value;

        Assert.Equal("staging", options.EnvironmentName);
        Assert.Equal("identity-service", options.ServiceName);
        Assert.Same(serializerOptions, options.SerializerOptions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("dev:profile")]
    [InlineData("dev{profile")]
    [InlineData("dev}profile")]
    [InlineData(" dev")]
    [InlineData("dev ")]
    [InlineData("dev\tprofile")]
    public void AddVersionedCache_RejectsInvalidHostEnvironment(string? environmentName)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(environmentName!));
        services.AddSingleton<IConfiguration>(CreateConfiguration());
        services.AddVersionedCache();
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<VersionedCacheOptions>>().Value);
    }

    [Fact]
    public void AddVersionedCache_RejectsMissingConfigurationAtStartup()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment("dev"));
        services.AddVersionedCache();
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains(nameof(IConfiguration), exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("identity:service")]
    [InlineData("{identity}")]
    [InlineData("identity}")]
    [InlineData(" identity-service")]
    [InlineData("identity-service ")]
    public void AddVersionedCache_RejectsMissingOrInvalidServiceNameAtStartup(string? serviceName)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment("dev"));
        services.AddSingleton<IConfiguration>(serviceName is null
            ? new ConfigurationBuilder().Build()
            : CreateConfiguration(serviceName));
        services.AddVersionedCache();
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("Service:Name", exception.Message);
    }

    [Fact]
    public void PublicApi_UsesSingleNamespace()
    {
        var namespaces = typeof(IVersionedCache).Assembly.ExportedTypes
            .Select(static type => type.Namespace)
            .Distinct(StringComparer.Ordinal);

        Assert.Equal(["MonixOne.VersionedCache"], namespaces);
    }

    private static IConfiguration CreateConfiguration(string serviceName = "identity-service") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Service:Name"] = serviceName })
            .Build();

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "VersionedCache.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
