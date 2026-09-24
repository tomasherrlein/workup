using System.IO;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WorkUp.Infrastructure;

namespace WorkUp.Tests.Infrastructure;

/// <summary>
/// T12 verification enabler: the development-only DataProtection keyring must be opt-in, must live in a
/// dedicated directory under the OS temp folder, and must never disturb the shared default keyring.
/// These tests never protect or unprotect data, so no key is ever written.
/// </summary>
public sealed class DataProtectionIsolationTests : IDisposable
{
    private const string KeysPathKey = "WorkUp:Development:DataProtectionKeysPath";
    private const string AbsoluteBaseUrl = "https://reservas.example.com";

    private readonly List<string> _temporaryDirectories = [];

    public void Dispose()
    {
        // Only removes directories this test created and that no key file ever reached.
        foreach (var directory in _temporaryDirectories)
        {
            if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0)
                Directory.Delete(directory);
        }
    }

    [Fact]
    public void Development_keys_path_registers_a_file_system_repository_outside_the_shared_keyring()
    {
        var keysDirectory = NewTemporaryKeysDirectory();

        using var provider = BuildProvider(keysDirectory, Environments.Development);

        var options = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        var repository = Assert.IsType<FileSystemXmlRepository>(options.XmlRepository);
        Assert.Equal(Canonical(keysDirectory), Canonical(repository.Directory.FullName));
        Assert.Empty(KeyFilesIn(keysDirectory));
    }

    [Fact]
    public void Production_rejects_the_development_keys_path()
    {
        var keysDirectory = NewTemporaryKeysDirectory();

        var error = Assert.Throws<InvalidOperationException>(
            () => BuildProvider(keysDirectory, Environments.Production));

        Assert.Contains(KeysPathKey, error.Message, StringComparison.Ordinal);
        Assert.Empty(KeyFilesIn(keysDirectory));
    }

    [Fact]
    public void Relative_keys_path_is_rejected()
    {
        var relativePath = "WorkUpDemoT12KeysRelative";

        var error = Assert.Throws<InvalidOperationException>(
            () => BuildProvider(relativePath, Environments.Development));

        Assert.Contains(KeysPathKey, error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.GetFullPath(relativePath)));
    }

    [Fact]
    public void Keys_path_outside_the_os_temp_directory_is_rejected()
    {
        var outsideTemp = Path.Combine(
            Path.GetPathRoot(Path.GetTempPath())!,
            "WorkUpDemoT12KeysOutsideTemp");

        var error = Assert.Throws<InvalidOperationException>(
            () => BuildProvider(outsideTemp, Environments.Development));

        Assert.Contains(KeysPathKey, error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(outsideTemp));
    }

    [Fact]
    public void Keys_path_leaf_without_the_demo_prefix_is_rejected()
    {
        var foreignLeaf = Directory.CreateTempSubdirectory("not-workup-demo-keys");
        _temporaryDirectories.Add(foreignLeaf.FullName);

        var error = Assert.Throws<InvalidOperationException>(
            () => BuildProvider(foreignLeaf.FullName, Environments.Development));

        Assert.Contains(KeysPathKey, error.Message, StringComparison.Ordinal);
        Assert.Empty(KeyFilesIn(foreignLeaf.FullName));
    }

    [Fact]
    public void Shared_default_keyring_path_is_rejected()
    {
        var sharedDefaultDirectory = FileSystemXmlRepository.DefaultKeyStorageDirectory;
        if (sharedDefaultDirectory is null)
            return;

        var error = Assert.Throws<InvalidOperationException>(
            () => BuildProvider(sharedDefaultDirectory.FullName, Environments.Development));

        Assert.Contains(KeysPathKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Absent_keys_path_keeps_the_default_data_protection_configuration()
    {
        using var provider = BuildProvider(configuredKeysPath: null, Environments.Development);
        using var baseline = BuildBaselineDataProtectionProvider();

        var configured = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        var expected = baseline.GetRequiredService<IOptions<KeyManagementOptions>>().Value;

        // Without the opt-in key, WorkUp registers nothing beyond services.AddDataProtection().
        Assert.Null(configured.XmlRepository);
        Assert.Equal(expected.XmlRepository, configured.XmlRepository);
        Assert.Equal(expected.XmlEncryptor, configured.XmlEncryptor);
        Assert.Equal(expected.AutoGenerateKeys, configured.AutoGenerateKeys);
    }

    [Fact]
    public void Windows_protects_isolated_keys_with_dpapi()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var keysDirectory = NewTemporaryKeysDirectory();

        using var provider = BuildProvider(keysDirectory, Environments.Development);

        var options = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        Assert.IsType<DpapiXmlEncryptor>(options.XmlEncryptor);
    }

    private string NewTemporaryKeysDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("WorkUpDemoT12Keys");
        _temporaryDirectories.Add(directory.FullName);
        return directory.FullName;
    }

    private static ServiceProvider BuildBaselineDataProtectionProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();

        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildProvider(string? configuredKeysPath, string environmentName)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:WorkUp"] =
                @"Server=(localdb)\MSSQLLocalDB;Database=WorkUpT12DataProtection;Trusted_Connection=True;TrustServerCertificate=True",
            ["WorkUp:PublicBaseUrl"] = AbsoluteBaseUrl,
            ["WorkUp:TimeZone"] = "America/Argentina/Buenos_Aires"
        };
        if (configuredKeysPath is not null)
            settings[KeysPathKey] = configuredKeysPath;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddWorkUp(configuration, new FakeHostEnvironment { EnvironmentName = environmentName });

        return services.BuildServiceProvider();
    }

    private static string Canonical(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static IReadOnlyList<string> KeyFilesIn(string directory) =>
        Directory.Exists(directory)
            ? Directory.GetFiles(directory, "key-*.xml", SearchOption.TopDirectoryOnly)
            : [];

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "WorkUp.Tests";

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
