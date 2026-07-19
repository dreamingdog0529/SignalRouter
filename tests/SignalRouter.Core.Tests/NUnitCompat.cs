using NUnit.Framework;
using NUnit.Framework.Constraints;

namespace SignalRouter.Core.Tests;

internal static class NUnitCompat
{
    public static void Multiple(Action assertions)
    {
        Assert.Multiple(assertions);
    }

    public static void ThatThrows(Action code, IResolveConstraint constraint)
    {
        Assert.That(code, constraint);
    }

    public static T? Throws<T>(Action code)
        where T : Exception
    {
        return Assert.Throws<T>(code);
    }
}
