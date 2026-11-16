#pragma warning disable CS1591

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Model.Configuration;

namespace Tesserafin.Controller.Providers;

/// <summary>Registered sidecar exporters, eligibility policy and explicit execution.</summary>
public interface IItemMetadataExporter
{
    void AddParts(IEnumerable<IMetadataSaver> savers);

    IEnumerable<IMetadataSaver> GetSavers(BaseItem item, LibraryOptions libraryOptions, ItemUpdateType reason, bool includeDisabled);

    Task SaveAsync(BaseItem item, ItemUpdateType updateType, CancellationToken cancellationToken, IEnumerable<string>? selectedSavers = null);
}
