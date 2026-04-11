using FluentAssertions;
using Microsoft.Extensions.Configuration;
using RamDrive.Core.Configuration;
using Xunit;

namespace RamDrive.Core.Tests;

public class DirectoryNodeBindingTests
{
    private static RamDriveOptions Bind(string json)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var config = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();

        var options = new RamDriveOptions();
        config.GetSection("RamDrive").Bind(options);
        return options;
    }

    [Fact]
    public void EmptyInitialDirectories_BindsToNullOrEmpty()
    {
        var options = Bind("""
        {
          "RamDrive": {
            "InitialDirectories": {}
          }
        }
        """);

        // .NET Configuration treats empty sections as absent — null is expected
        if (options.InitialDirectories != null)
            options.InitialDirectories.Should().BeEmpty();
    }

    [Fact]
    public void MissingInitialDirectories_BindsToNull()
    {
        var options = Bind("""
        {
          "RamDrive": {
            "CapacityMb": 128
          }
        }
        """);

        options.InitialDirectories.Should().BeNull();
    }

    [Fact]
    public void SingleLeafDirectory_BindsCorrectly()
    {
        var options = Bind("""
        {
          "RamDrive": {
            "InitialDirectories": {
              "Temp": {}
            }
          }
        }
        """);

        options.InitialDirectories.Should().ContainKey("Temp");
        options.InitialDirectories!["Temp"].Should().BeEmpty();
    }

    [Fact]
    public void NestedDirectories_BindsRecursively()
    {
        var options = Bind("""
        {
          "RamDrive": {
            "InitialDirectories": {
              "Cache": {
                "App1": {},
                "App2": {}
              }
            }
          }
        }
        """);

        options.InitialDirectories.Should().ContainKey("Cache");
        var cache = options.InitialDirectories!["Cache"];
        cache.Should().ContainKey("App1");
        cache.Should().ContainKey("App2");
        cache["App1"].Should().BeEmpty();
        cache["App2"].Should().BeEmpty();
    }

    [Fact]
    public void DeeplyNestedDirectories_BindsAllLevels()
    {
        var options = Bind("""
        {
          "RamDrive": {
            "InitialDirectories": {
              "Work": {
                "Build": {
                  "Output": {}
                }
              }
            }
          }
        }
        """);

        options.InitialDirectories.Should().ContainKey("Work");
        var work = options.InitialDirectories!["Work"];
        work.Should().ContainKey("Build");
        var build = work["Build"];
        build.Should().ContainKey("Output");
        build["Output"].Should().BeEmpty();
    }

    [Fact]
    public void MixedTopLevelAndNested_BindsCorrectly()
    {
        var options = Bind("""
        {
          "RamDrive": {
            "InitialDirectories": {
              "Temp": {},
              "Cache": {
                "App1": {}
              },
              "Work": {
                "Build": {
                  "Output": {}
                }
              }
            }
          }
        }
        """);

        options.InitialDirectories.Should().HaveCount(3);
        options.InitialDirectories!["Temp"].Should().BeEmpty();
        options.InitialDirectories["Cache"].Should().ContainKey("App1");
        options.InitialDirectories["Work"]["Build"]["Output"].Should().BeEmpty();
    }
}
