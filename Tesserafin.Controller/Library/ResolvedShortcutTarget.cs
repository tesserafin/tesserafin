namespace Tesserafin.Controller.Library;

/// <summary>A shortcut's source file and expanded absolute target, without catalogue writes.</summary>
/// <param name="SourcePath">The shortcut file read during discovery.</param>
/// <param name="TargetPath">The expanded absolute target.</param>
public sealed record ResolvedShortcutTarget(string SourcePath, string TargetPath);
