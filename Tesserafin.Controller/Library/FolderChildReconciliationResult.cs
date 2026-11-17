using System.Collections.Generic;
using Tesserafin.Controller.Entities;

namespace Tesserafin.Controller.Library;

/// <summary>The retained children and the accessible subset to refresh after reconciliation.</summary>
/// <param name="ValidChildren">Retained catalogue children.</param>
/// <param name="AccessibleChildren">Children whose metadata/subfolders may be refreshed.</param>
/// <param name="Skipped">Whether inaccessible storage prevented reconciliation.</param>
public sealed record FolderChildReconciliationResult(IReadOnlyList<BaseItem> ValidChildren, IReadOnlyList<BaseItem> AccessibleChildren, bool Skipped = false);
