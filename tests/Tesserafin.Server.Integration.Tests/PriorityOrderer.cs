using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit.Sdk;
using Xunit.v3;

namespace Tesserafin.Server.Integration.Tests;

/// <summary>
/// Orders the test methods of a class by <see cref="PriorityAttribute"/> (ascending, 0 when absent),
/// then by method name, so classes whose tests build on shared server state run deterministically.
/// Applied with <c>[TestMethodOrderer(typeof(PriorityOrderer))]</c>. Replaces the Xunit.v3.Priority
/// package, which is built against xunit.v3 3.x: in 4.x a test case orderer only orders the cases of
/// one method, and method order belongs to <see cref="ITestMethodOrderer"/>.
/// </summary>
public sealed class PriorityOrderer : ITestMethodOrderer
{
    /// <inheritdoc />
    public IReadOnlyCollection<TTestMethod?> OrderTestMethods<TTestMethod>(IReadOnlyCollection<TTestMethod?> testMethods)
        where TTestMethod : notnull, ITestMethod
        => testMethods
            .OrderBy(PriorityOf)
            .ThenBy(m => m?.MethodName, StringComparer.Ordinal)
            .ToArray();

    private static int PriorityOf<TTestMethod>(TTestMethod? testMethod)
        where TTestMethod : notnull, ITestMethod
        => testMethod is IXunitTestMethod xunitMethod
            ? xunitMethod.Method.GetCustomAttribute<PriorityAttribute>()?.Priority ?? 0
            : 0;
}
