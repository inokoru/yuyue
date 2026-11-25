using System.Text.Json;
using Xunit;
using Yuyue.Reader;

namespace Yuyue.Tests;

/// <summary>
/// Layer 3: Validates Windows <see cref="ReaderMessage"/> parsing against the canonical
/// <c>fixtures/conformance/reader-messages.json</c> fixtures and generated JSON Schema
/// (<c>reader/schema/bridge-messages.schema.json</c>).
/// </summary>
public sealed class ReaderBridgeContractTests
{
    [Fact]
    public void WindowsReaderMessageParser_AcceptsAllCanonicalBridgeMessagesFromSchema()
    {
        string schemaPath = Path.Combine(TestPaths.RepoRoot, "reader", "schema", "bridge-messages.schema.json");
        string fixturePath = TestPaths.Fixture("conformance", "reader-messages.json");
        Assert.True(File.Exists(schemaPath), $"Missing {schemaPath}");
        Assert.True(File.Exists(fixturePath), $"Missing {fixturePath}");

        using var schemaDoc = JsonDocument.Parse(File.ReadAllText(schemaPath));
        string[] schemaTypes = schemaDoc.RootElement
            .GetProperty("x-message-types")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();
        Assert.True(schemaTypes.Length >= 11);

        using var fixtureDoc = JsonDocument.Parse(File.ReadAllText(fixturePath));
        var seenTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawMsg in fixtureDoc.RootElement.GetProperty("valid").EnumerateArray())
        {
            string json = rawMsg.GetRawText();
            var parsed = ReaderMessage.Parse(json);
            Assert.NotNull(parsed);
            Assert.False(string.IsNullOrWhiteSpace(parsed!.Type));
            seenTypes.Add(parsed.Type);

            switch (parsed.Type)
            {
                case "ready":
                case "layout":
                    Assert.NotNull(parsed.Section);
                    Assert.True(parsed.PageCount > 0);
                    Assert.NotNull(parsed.SentencePages);
                    Assert.NotNull(parsed.PageFirstSentence);
                    break;
                case "tap":
                    Assert.NotNull(parsed.X);
                    Assert.NotNull(parsed.Y);
                    Assert.NotNull(parsed.Width);
                    Assert.NotNull(parsed.Height);
                    if (parsed.Word is not null)
                    {
                        Assert.False(string.IsNullOrWhiteSpace(parsed.Word.Text));
                        Assert.NotNull(parsed.Word.Rect);
                    }
                    if (parsed.Link?.Note is not null)
                    {
                        Assert.False(string.IsNullOrWhiteSpace(parsed.Link.Note.Label));
                    }
                    break;
                case "doubleTap":
                    Assert.NotNull(parsed.X);
                    Assert.NotNull(parsed.Y);
                    break;
                case "context":
                    Assert.NotNull(parsed.X);
                    Assert.NotNull(parsed.Y);
                    break;
                case "selection":
                    if (parsed.Text is not null)
                    {
                        Assert.NotNull(parsed.Start);
                        Assert.NotNull(parsed.End);
                        Assert.NotNull(parsed.Rect);
                    }
                    break;
                case "activePage":
                    Assert.NotNull(parsed.Sentence);
                    Assert.NotNull(parsed.Page);
                    break;
                case "bubble":
                    Assert.NotNull(parsed.Line);
                    Assert.NotNull(parsed.X);
                    Assert.NotNull(parsed.Y);
                    break;
                case "page":
                    Assert.NotNull(parsed.Page);
                    break;
                case "scroll":
                    Assert.NotNull(parsed.Progress);
                    break;
                case "log":
                    Assert.False(string.IsNullOrWhiteSpace(parsed.Level));
                    Assert.NotNull(parsed.Message);
                    break;
                default:
                    Assert.Fail($"Unexpected reader bridge message type '{parsed.Type}'");
                    break;
            }
        }

        foreach (string expectedType in schemaTypes)
        {
            Assert.Contains(expectedType, seenTypes);
        }
    }
}
