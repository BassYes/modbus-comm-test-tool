using Comm.Plugins.Abstractions;
using Comm.Core.Errors;
using Comm.Plugins.Host;
using Xunit;

namespace Comm.Plugins.Host.Tests;

/// <summary>版本范围解析与清单校验（规划 8.2、12.8、15.11）。</summary>
public class PluginVersionRangeTests
{
    [Theory]
    [InlineData("", "1.0.0", true)]
    [InlineData("*", "1.0.0", true)]
    [InlineData(">=0.2.0", "0.2.5", true)]
    [InlineData(">=0.2.0", "0.1.9", false)]
    [InlineData(">=0.2.0 <1.0.0", "0.9.0", true)]
    [InlineData(">=0.2.0 <1.0.0", "1.0.0", false)]
    [InlineData(">1.0.0", "1.0.0", false)]
    [InlineData("<2.0.0", "1.9.9", true)]
    [InlineData("=1.2.3", "1.2.3", true)]
    [InlineData("1.2.3", "1.2.4", false)]
    public void Range_matches_expected_versions(string range, string version, bool expected)
    {
        Assert.True(PluginVersionRange.TryParse(range, out var parsed, out var error), error);
        Assert.Equal(expected, parsed!.IsSatisfiedBy(Version.Parse(version)));
    }

    [Theory]
    [InlineData(">=abc")]
    [InlineData(">=1.0.0 <")]
    [InlineData("!!1.0.0")]
    public void Invalid_range_is_rejected(string range)
    {
        Assert.False(PluginVersionRange.TryParse(range, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Any_range_satisfies_every_version()
    {
        var range = PluginVersionRange.Parse("*");
        Assert.True(range.IsAny);
        Assert.True(range.IsSatisfiedBy(new Version(0, 0, 1)));
        Assert.True(range.IsSatisfiedBy(new Version(99, 0, 0)));
    }

    [Fact]
    public void Two_part_version_is_normalized_for_comparison()
    {
        var range = PluginVersionRange.Parse(">=1.2");
        Assert.True(range.IsSatisfiedBy(new Version(1, 2, 0, 0)));
        Assert.True(range.IsSatisfiedBy(new Version(1, 2)));
        Assert.False(range.IsSatisfiedBy(new Version(1, 1, 9)));
    }
}

/// <summary>plugin.json 读取与校验（规划 8.2、15.11）。</summary>
public class PluginManifestReaderTests
{
    private static readonly Version HostVersion = new(0, 3, 0);

    [Fact]
    public void Reads_the_sample_manifest()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            var directory = PluginTestFixtures.InstallSamplePlugin(root);
            var result = PluginManifestReader.Read(directory, HostVersion);

            Assert.True(result.IsSuccess, result.Error);
            Assert.Equal("sample-plugin", result.Manifest!.Id);
            Assert.Contains(PluginCapability.FrameParser, result.Manifest.Capabilities);
            Assert.Equal("Comm.Plugin.Sample.dll", result.Manifest.EntryAssembly);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Missing_manifest_is_invalid()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            var directory = Path.Combine(root, "NoManifest");
            Directory.CreateDirectory(directory);

            var result = PluginManifestReader.Read(directory, HostVersion);
            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorCodes.PluginManifestInvalid, result.ErrorCode);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Malformed_json_is_invalid()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            var directory = PluginTestFixtures.WriteManifest(root, "Broken", "{ not json");
            var result = PluginManifestReader.Read(directory, HostVersion);

            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorCodes.PluginManifestInvalid, result.ErrorCode);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Missing_id_is_invalid()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            var directory = PluginTestFixtures.WriteManifest(root, "NoId", """{"name":"无 id","version":"1.0.0"}""");
            var result = PluginManifestReader.Read(directory, HostVersion);

            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorCodes.PluginManifestInvalid, result.ErrorCode);
            Assert.Contains("id", result.Error);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Missing_name_is_invalid()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            var directory = PluginTestFixtures.WriteManifest(root, "NoName", """{"id":"no-name","version":"1.0.0"}""");
            var result = PluginManifestReader.Read(directory, HostVersion);

            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorCodes.PluginManifestInvalid, result.ErrorCode);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Unparsable_version_is_invalid()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            var directory = PluginTestFixtures.WriteManifest(root, "BadVersion",
                """{"id":"bad-version","name":"版本非法","version":"abc"}""");
            var result = PluginManifestReader.Read(directory, HostVersion);

            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorCodes.PluginManifestInvalid, result.ErrorCode);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Incompatible_host_range_is_reported_as_incompatible()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            var directory = PluginTestFixtures.WriteManifest(root, "Future",
                """{"id":"future-plugin","name":"未来插件","version":"1.0.0","hostVersionRange":">=9.0.0"}""");
            var result = PluginManifestReader.Read(directory, HostVersion);

            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorCodes.PluginIncompatible, result.ErrorCode);
            Assert.Contains("9.0.0", result.Error);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Invalid_host_range_is_invalid_manifest()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            var directory = PluginTestFixtures.WriteManifest(root, "BadRange",
                """{"id":"bad-range","name":"范围非法","version":"1.0.0","hostVersionRange":">=x"}""");
            var result = PluginManifestReader.Read(directory, HostVersion);

            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorCodes.PluginManifestInvalid, result.ErrorCode);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Looks_like_plugin_checks_manifest_presence()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            var withManifest = PluginTestFixtures.InstallSamplePlugin(root, "WithManifest");
            var without = Path.Combine(root, "Without");
            Directory.CreateDirectory(without);

            Assert.True(PluginManifestReader.LooksLikePlugin(withManifest));
            Assert.False(PluginManifestReader.LooksLikePlugin(without));
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }
}
