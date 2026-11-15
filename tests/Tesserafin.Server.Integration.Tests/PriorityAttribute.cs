using System;

namespace Tesserafin.Server.Integration.Tests;

/// <summary>
/// Gives a test method its position in a <see cref="PriorityOrderer"/>-ordered class: lower runs first.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PriorityAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PriorityAttribute"/> class.
    /// </summary>
    /// <param name="priority">The position; lower runs first.</param>
    public PriorityAttribute(int priority) => Priority = priority;

    /// <summary>
    /// Gets the position; lower runs first.
    /// </summary>
    public int Priority { get; }
}
