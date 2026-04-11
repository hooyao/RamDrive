using FluentAssertions;

namespace RamDrive.IntegrationTests;

[Collection("RamDrive")]
public class InitialDirectoryTests : IDisposable
{
    private readonly string _root;

    public InitialDirectoryTests(RamDriveFixture fx)
    {
        _root = Path.Combine(fx.Root, $"initdir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void SingleLeafDirectory_CreatedOnDisk()
    {
        var temp = Path.Combine(_root, "Temp");
        Directory.CreateDirectory(temp);

        Directory.Exists(temp).Should().BeTrue();
    }

    [Fact]
    public void NestedDirectories_AllCreatedOnDisk()
    {
        // Simulate: { "Cache": { "App1": {}, "App2": {} } }
        var cache = Path.Combine(_root, "Cache");
        var app1 = Path.Combine(cache, "App1");
        var app2 = Path.Combine(cache, "App2");

        Directory.CreateDirectory(app1);
        Directory.CreateDirectory(app2);

        Directory.Exists(cache).Should().BeTrue();
        Directory.Exists(app1).Should().BeTrue();
        Directory.Exists(app2).Should().BeTrue();
    }

    [Fact]
    public void DeeplyNestedDirectories_AllLevelsExist()
    {
        // Simulate: { "Work": { "Build": { "Output": {} } } }
        var output = Path.Combine(_root, "Work", "Build", "Output");
        Directory.CreateDirectory(output);

        Directory.Exists(Path.Combine(_root, "Work")).Should().BeTrue();
        Directory.Exists(Path.Combine(_root, "Work", "Build")).Should().BeTrue();
        Directory.Exists(output).Should().BeTrue();
    }

    [Fact]
    public void MixedTree_AllDirectoriesCreated()
    {
        // Simulate: { "Temp": {}, "Cache": { "App1": {} }, "Work": { "Build": { "Output": {} } } }
        Directory.CreateDirectory(Path.Combine(_root, "Temp"));
        Directory.CreateDirectory(Path.Combine(_root, "Cache", "App1"));
        Directory.CreateDirectory(Path.Combine(_root, "Work", "Build", "Output"));

        Directory.GetDirectories(_root).Length.Should().Be(3);
        Directory.Exists(Path.Combine(_root, "Temp")).Should().BeTrue();
        Directory.Exists(Path.Combine(_root, "Cache", "App1")).Should().BeTrue();
        Directory.Exists(Path.Combine(_root, "Work", "Build", "Output")).Should().BeTrue();
    }

    [Fact]
    public void DuplicateCreate_DoesNotThrow()
    {
        var temp = Path.Combine(_root, "Temp");
        Directory.CreateDirectory(temp);
        // Creating again should not error
        Directory.CreateDirectory(temp);

        Directory.Exists(temp).Should().BeTrue();
    }

    [Fact]
    public void CreatedDirectories_AreWritable()
    {
        var temp = Path.Combine(_root, "Temp");
        Directory.CreateDirectory(temp);

        var file = Path.Combine(temp, "test.txt");
        File.WriteAllText(file, "hello");

        File.Exists(file).Should().BeTrue();
        File.ReadAllText(file).Should().Be("hello");
    }
}
