// Unit tests for StreamPath parsing of NTFS named-stream (ADS) paths.
//
// The subtle part is the `:$DATA` type suffix. On NTFS the main stream has an EMPTY
// name, written `f::$DATA` (or `f:`), and collapses to the plain base path. A literal
// `$DATA` in the NAME position (`f:$DATA`) is an ordinary named stream called `$DATA`,
// NOT the main stream -- only a SECOND colon introduces the stream type. Confirmed
// against real NTFS: writing `f:$DATA` leaves the main stream untouched and creates a
// second stream named `$DATA`.
//
// Getting this wrong is not cosmetic: Normalize feeds every lookup/creation, so
// collapsing `f:$DATA` to `f` made a write to that stream overwrite the file's main
// content.

using FluentAssertions;
using RamDrive.Core.FileSystem;
using Xunit;

namespace RamDrive.Core.Tests;

public sealed class StreamPathTests
{
    [Theory]
    [InlineData(@"\f", @"\f")]                 // no stream at all
    [InlineData(@"\f:", @"\f")]                // explicit empty name == main stream
    [InlineData(@"\f::$DATA", @"\f")]          // explicit main stream with type
    [InlineData(@"\f:$DATA", @"\f:$DATA")]     // stream LITERALLY named $DATA
    [InlineData(@"\f:$DATA:$DATA", @"\f:$DATA")] // same stream, with the type spelled out
    [InlineData(@"\f:s1", @"\f:s1")]           // plain named stream
    [InlineData(@"\f:s1:$DATA", @"\f:s1")]     // named stream with explicit type
    [InlineData(@"\dir\a.txt::$DATA", @"\dir\a.txt")] // main stream, nested
    [InlineData(@"\dir\a.txt:", @"\dir\a.txt")]       // main stream, nested, trailing colon
    [InlineData(@"\dir\a.txt:s1:$DATA", @"\dir\a.txt:s1")]
    public void Normalize_maps_stream_types_the_NTFS_way(string input, string expected)
    {
        StreamPath.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"\f", false)]
    [InlineData(@"\f:", true)]
    [InlineData(@"\f::$DATA", true)]
    [InlineData(@"\f:$DATA", true)]            // a real named stream, not the main one
    [InlineData(@"\f:s1", true)]
    [InlineData(@"\dir\a.txt", false)]
    [InlineData(@"\dir\a.txt:s1", true)]
    public void HasStream_detects_a_colon_in_the_leaf(string path, bool expected)
    {
        StreamPath.HasStream(path).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"\f", @"\f", "")]
    [InlineData(@"\f:", @"\f", "")]                 // main stream -> empty name
    [InlineData(@"\f::$DATA", @"\f", "")]           // main stream -> empty name
    [InlineData(@"\f:$DATA", @"\f", "$DATA")]       // literal name preserved
    [InlineData(@"\f:$DATA:$DATA", @"\f", "$DATA")]
    [InlineData(@"\f:s1", @"\f", "s1")]
    [InlineData(@"\f:s1:$DATA", @"\f", "s1")]
    public void SplitLeaf_separates_base_and_stream_name(string name, string baseName, string streamName)
    {
        var (b, s) = StreamPath.SplitLeaf(name);
        b.Should().Be(baseName);
        s.Should().Be(streamName);
    }

    [Theory]
    [InlineData(@"\f", @"\f")]
    [InlineData(@"\f:$DATA", @"\f")]
    [InlineData(@"\f:s1", @"\f")]
    [InlineData(@"\f:s1:$DATA", @"\f")]
    [InlineData(@"\dir\a.txt:s1", @"\dir\a.txt")]
    public void GetBasePath_drops_the_stream_part(string path, string expected)
    {
        StreamPath.GetBasePath(path).Should().Be(expected);
    }

    [Fact]
    public void Normalize_is_idempotent()
    {
        foreach (var p in new[]
                 {
                     @"\f", @"\f:", @"\f::$DATA", @"\f:$DATA",
                     @"\f:$DATA:$DATA", @"\f:s1", @"\f:s1:$DATA",
                 })
        {
            string once = StreamPath.Normalize(p);
            StreamPath.Normalize(once).Should().Be(once, "normalizing '{0}' twice must be stable", p);
        }
    }
}
