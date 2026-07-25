using System;
using NUnit.Framework;

namespace SignalRouter.V2.Contracts.Tests;

/// <summary>
/// Disambiguates NUnit's <c>Assert.Throws</c> overloads (TestDelegate vs Action)
/// for lambda arguments — the same shim v1 keeps in <c>NUnitCompat</c>.
/// </summary>
internal static class AssertEx
{
    internal static TException Throws<TException>(Action action)
        where TException : Exception =>
        Assert.Throws<TException>(action)!;
}
