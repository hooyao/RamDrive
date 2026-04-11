using FluentAssertions;
using RamDrive.Core.Configuration;
using Xunit;

namespace RamDrive.Core.Tests;

public class DirectoryNodeValidationTests
{
    [Fact]
    public void ValidTree_NoErrors()
    {
        var node = new DirectoryNode
        {
            ["Temp"] = new DirectoryNode(),
            ["Cache"] = new DirectoryNode
            {
                ["App1"] = new DirectoryNode(),
                ["App2"] = new DirectoryNode()
            }
        };

        node.Validate().Should().BeEmpty();
    }

    [Fact]
    public void InvalidCharInName_ReportsError()
    {
        var node = new DirectoryNode
        {
            ["Bad:Name"] = new DirectoryNode()
        };

        var errors = node.Validate();
        errors.Should().ContainSingle()
            .Which.Should().Contain("invalid character");
    }

    [Fact]
    public void ReservedName_ReportsError()
    {
        var node = new DirectoryNode
        {
            ["CON"] = new DirectoryNode()
        };

        var errors = node.Validate();
        errors.Should().ContainSingle()
            .Which.Should().Contain("reserved name");
    }

    [Fact]
    public void ReservedNameWithTrailingDot_ReportsError()
    {
        var node = new DirectoryNode
        {
            ["NUL."] = new DirectoryNode()
        };

        var errors = node.Validate();
        errors.Should().ContainSingle()
            .Which.Should().Contain("reserved name");
    }

    [Fact]
    public void EmptyName_ReportsError()
    {
        var node = new DirectoryNode
        {
            [""] = new DirectoryNode()
        };

        var errors = node.Validate();
        errors.Should().ContainSingle()
            .Which.Should().Contain("Empty directory name");
    }

    [Fact]
    public void NameExceeds255_ReportsError()
    {
        var node = new DirectoryNode
        {
            [new string('A', 256)] = new DirectoryNode()
        };

        var errors = node.Validate();
        errors.Should().ContainSingle()
            .Which.Should().Contain("exceeds 255");
    }

    [Fact]
    public void NestedInvalidName_ReportsWithFullPath()
    {
        var node = new DirectoryNode
        {
            ["Cache"] = new DirectoryNode
            {
                ["Bad<Dir"] = new DirectoryNode()
            }
        };

        var errors = node.Validate();
        errors.Should().ContainSingle()
            .Which.Should().Contain(@"Cache\Bad<Dir");
    }

    [Fact]
    public void InvalidParent_StillValidatesChildren()
    {
        var node = new DirectoryNode
        {
            ["PRN"] = new DirectoryNode
            {
                ["Bad:Child"] = new DirectoryNode()
            }
        };

        var errors = node.Validate();
        errors.Should().HaveCount(2);
        errors.Should().Contain(e => e.Contains("reserved name"));
        errors.Should().Contain(e => e.Contains("invalid character"));
    }

    [Fact]
    public void MultipleErrors_AllReported()
    {
        var node = new DirectoryNode
        {
            ["CON"] = new DirectoryNode(),
            ["Bad:Name"] = new DirectoryNode(),
            ["Good"] = new DirectoryNode()
        };

        var errors = node.Validate();
        errors.Should().HaveCount(2);
    }
}
