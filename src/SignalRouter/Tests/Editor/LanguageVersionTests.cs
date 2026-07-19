using NUnit.Framework;

namespace SignalRouter.Tests;

public sealed class LanguageVersionTests
{
    [Test]
    public void EditorTestsSupportCSharp11LanguageFeatures()
    {
        var first = new LanguageProbe { Name = "SignalRouter" };
        var second = new LanguageProbe { Name = "SignalRouter" };

        Assert.That(first, Is.EqualTo(second));
        Assert.That(first.Name, Is.EqualTo("SignalRouter"));
    }

    private readonly record struct LanguageProbe
    {
        public required string Name { get; init; }
    }
}
