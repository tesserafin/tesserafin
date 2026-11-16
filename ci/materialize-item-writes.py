#!/usr/bin/env python3
"""Materialize the approved item-write extraction on an isolated CI checkout."""
from pathlib import Path
import json
import re
import subprocess

root = Path.cwd()
def read(path): return (root / path).read_text()
def put(path, text):
    target = root / path
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(text)

def bounds(text, signature, docs=True):
    start = text.index(signature)
    pos = text.index("{", start)
    depth, state = 0, "code"
    while pos < len(text):
        char, pair = text[pos], text[pos:pos + 2]
        if state == "line":
            if char == "\n": state = "code"
        elif state == "comment":
            if pair == "*/": state = "code"; pos += 1
        elif state == "string":
            if char == "\\": pos += 1
            elif char == '"': state = "code"
        elif state == "char":
            if char == "\\": pos += 1
            elif char == "'": state = "code"
        elif state == "verbatim":
            if pair == '""': pos += 1
            elif char == '"': state = "code"
        else:
            if pair == "//": state = "line"; pos += 1
            elif pair == "/*": state = "comment"; pos += 1
            elif pair == '@"': state = "verbatim"; pos += 1
            elif char == '"': state = "string"
            elif char == "'": state = "char"
            elif char == "{": depth += 1
            elif char == "}":
                depth -= 1
                if depth == 0: break
        pos += 1
    if depth: raise RuntimeError("Unclosed method: " + signature)
    if docs:
        while start:
            previous = text.rfind("\n", 0, start - 1) + 1
            line = text[previous:start].strip()
            if line.startswith("///") or not line: start = previous
            else: break
    return start, pos + 1

def remove_method(text, signature):
    first, last = bounds(text, signature)
    return text[:first] + text[last:]

def delegate(text, signature, expression):
    first, last = bounds(text, signature, False)
    header = text[first:text.index("{", first)].rstrip()
    return text[:first] + header + "\n            => " + expression + ";" + text[last:]

def inject(text, name):
    if re.search(r"\bIItemWriteCommands\s+_writes\b", text): return text
    # A derived metadata service receives the shared port from its base.
    if re.search(r"class\s+\w+\s*:\s*MetadataService<", text): return text.replace("_writes", "Writes")
    if "using Tesserafin.Controller.Library;" not in text:
        text = "using Tesserafin.Controller.Library;\n" + text
    match = re.search(r"(?:(?:public|internal|protected)\s+" + re.escape(name) + r"|class\s+" + re.escape(name) + r")\(", text)
    if not match: raise RuntimeError("Constructor not found: " + name)
    primary = match.group().startswith("class ")
    pos = match.end()
    multiline = text[pos:pos + 1] == "\n"
    indent = "    " if primary else re.search(r"(?m)^( *)[^\n]*" + re.escape(name) + r"\(", text[:pos]).group(1) + "    "
    text = text[:pos] + ("\n" + indent + "IItemWriteCommands writes," if multiline else "IItemWriteCommands writes, ") + text[pos:]
    if primary:
        body = text.index("\n{", pos) + 2
        text = text[:body] + "\n    private readonly IItemWriteCommands _writes = writes;\n" + text[body:]
    else:
        body = text.index("{", text.index("\n", pos))
        text = text[:body + 1] + "\n" + indent + "_writes = writes;" + text[body + 1:]
        first = text.index("private readonly ")
        line = text.rfind("\n", 0, first) + 1
        text = text[:line] + text[line:first] + "private readonly IItemWriteCommands _writes;\n" + text[line:]
    return text

manager_path = "Tesserafin.Server.Core/Library/LibraryManager.cs"
original = read(manager_path)
a, b = bounds(original, "        private Guid GetNewItemIdInternal(", False)
method = original[a:b]
identity_body = method[method.index("{") + 1:method.rfind("}")].replace("_configurationManager", "configuration")
put("Tesserafin.Server.Core/Library/ItemIdentity.cs", """using System;
using Tesserafin.Common.Extensions;
using Tesserafin.Controller.Configuration;

namespace Tesserafin.Server.Core.Library;

/// <summary>One deterministic identity policy for item creation and media resolution.</summary>
internal static class ItemIdentity
{
    public static Guid Create(IServerConfigurationManager configuration, string key, Type type, bool forceCaseInsensitive = false)
    {""" + identity_body + """    }
}
""")

put("Tesserafin.Controller/Library/IItemResolutionPolicy.cs", """#pragma warning disable CS1591

using System;
using Tesserafin.Controller.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.IO;

namespace Tesserafin.Controller.Library;

/// <summary>Explicit read context for filesystem resolution and ancestor lookup.</summary>
public interface IItemResolutionPolicy : IItemLookupService
{
    Guid GetNewItemId(string key, Type type);

    LibraryOptions GetLibraryOptions(BaseItem item);

    CollectionType? GetConfiguredContentType(string path);

    bool IgnoreFile(FileSystemMetadata file, BaseItem? parent);

    bool AreEqualPaths(string first, string second);
}
""")
put("Tesserafin.Controller/Library/ILibraryItemOptions.cs", """#pragma warning disable CS1591

using Tesserafin.Controller.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Configuration;

namespace Tesserafin.Controller.Library;

/// <summary>Library options and content type policy without a manager dependency.</summary>
public interface ILibraryItemOptions
{
    LibraryOptions GetLibraryOptions(BaseItem item);

    CollectionType? GetTopFolderContentType(BaseItem? item);

    CollectionType? GetConfiguredContentType(string path, bool inherit = false);
}
""")
a1,b1=bounds(original,"        public LibraryOptions GetLibraryOptions(",False)
a2,b2=bounds(original,"        private CollectionType? GetContentTypeOverride(",False)
a3,b3=bounds(original,"        private CollectionType? GetTopFolderContentType(",False)
options="\n\n".join((original[a1:b1],original[a2:b2],original[a3:b3]))
options=options.replace("GetCollectionFolders(item)","collections.GetCollectionFolders(item)").replace("private CollectionType? GetContentTypeOverride(string path, bool inherit)","public CollectionType? GetConfiguredContentType(string path, bool inherit = false)").replace("_configurationManager","configuration").replace("_fileSystem","files").replace("private CollectionType? GetTopFolderContentType(BaseItem item)","public CollectionType? GetTopFolderContentType(BaseItem? item)").replace("item.GetParent(this)","item.GetParent(lookup)").replace("GetUserRootFolder().Children","CollectionFolderResolver.GetLibraries(roots.GetUserRootFolder(), lookup)")
put("Tesserafin.Server.Core/Library/LibraryItemOptions.cs", """using System;
using System.Linq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.IO;

namespace Tesserafin.Server.Core.Library;

/// <summary>Reads effective item options and collection type from explicit hierarchy inputs.</summary>
internal sealed class LibraryItemOptions(
    ICollectionFolderResolver collections,
    IItemLookupService lookup,
    IUserRootFolderProvider roots,
    IServerConfigurationManager configuration,
    IFileSystem files) : ILibraryItemOptions
{
""" + options + "\n}\n")

put("Tesserafin.Controller/Library/IItemResolutionService.cs", """#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Resolvers;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.IO;

namespace Tesserafin.Controller.Library;

/// <summary>Resolves filesystem candidates without saving items or invoking metadata exporters.</summary>
public interface IItemResolutionService : IItemResolutionPolicy
{
    void AddParts(IEnumerable<IResolverIgnoreRule> rules, IEnumerable<IItemResolver> resolvers);

    BaseItem? ResolvePath(FileSystemMetadata fileInfo, Folder? parent = null, IDirectoryService? directoryService = null, CollectionType? collectionType = null);

    BaseItem? ResolvePath(FileSystemMetadata fileInfo, IDirectoryService directoryService, IItemResolver[]? resolvers, Folder? parent = null, CollectionType? collectionType = null, LibraryOptions? libraryOptions = null);

    IEnumerable<BaseItem> ResolvePaths(IEnumerable<FileSystemMetadata> files, IDirectoryService directoryService, Folder parent, LibraryOptions libraryOptions, CollectionType? collectionType = null);

    IEnumerable<BaseItem> ResolvePaths(IEnumerable<FileSystemMetadata> files, IDirectoryService directoryService, Folder parent, LibraryOptions libraryOptions, CollectionType? collectionType, IItemResolver[] resolvers);

    Video? ResolveAlternateVersion(string path, Type expectedVideoType, Folder? parent, CollectionType? collectionType);

    List<FileSystemMetadata> NormalizeRootPathList(IEnumerable<FileSystemMetadata> paths);
}
""")
p="Tesserafin.Controller/Library/ItemResolveArgs.cs"
s=read(p).replace("ILibraryManager _libraryManager","IItemResolutionPolicy _policy").replace("ILibraryManager libraryManager","IItemResolutionPolicy policy").replace("_libraryManager = libraryManager","_policy = policy").replace("_libraryManager.","_policy.").replace('name="libraryManager"','name="policy"').replace("The library manager.","The explicit resolution policy.").replace("BaseItem.FileSystem.AreEqual","_policy.AreEqualPaths").replace("parent.GetParents()","parent.GetParents(_policy)")
put(p,s)
p="Tesserafin.Controller/Library/ILibraryManager.cs"
s=read(p).replace("public interface ILibraryManager","public interface ILibraryManager : IItemResolutionPolicy")
for signature in (r"Guid GetNewItemId\(string key, Type type\);",r"LibraryOptions GetLibraryOptions\(BaseItem item\);",r"CollectionType\? GetConfiguredContentType\(string path\);",r"bool IgnoreFile\(FileSystemMetadata file, BaseItem\?? parent\);",r"BaseItem\? GetItemById\(Guid id\);",r"T\? GetItemById<T>\(Guid id\)\s*where T : BaseItem;"):
    s,n=re.subn(r"(?:        ///[^\n]*\n)*        "+signature+r"\n","",s)
    if n != 1: raise RuntimeError("Read interface declaration mismatch: " + signature)
put(p,s)

a,b=bounds(original,"        private BaseItem? ResolveItem(",False)
c,d=bounds(original,"        private BaseItem? Resolve(",False)
first=original[a:b]+"\n\n"+original[c:d]
a,b=bounds(original,"        public BaseItem? ResolvePath(",False)
c,d=bounds(original,"        private IEnumerable<BaseItem> ResolveFileList(",False)
rest=original[a:d].replace("private BaseItem? ResolvePath(","public BaseItem? ResolvePath(").replace("            return resolved;","            SetAdditionalPartsFromStack(resolved, path);\n            return resolved;")
methods=(first+"\n\n"+rest).replace("ResolverHelper.SetInitialItemValues(item, args, _fileSystem, this)","ResolverHelper.SetInitialItemValues(item, args, _fileSystem, this, _configurationManager.GetMetadataConfiguration().UseFileCreationTimeForDateAdded)").replace("ResolverHelper.SetInitialItemValues(item, parent, this, directoryService)","ResolverHelper.SetInitialItemValues(item, parent, this, directoryService, _configurationManager.GetMetadataConfiguration().UseFileCreationTimeForDateAdded)")
usings=original[:original.index("namespace Tesserafin.Server.Core.Library")]
put("Tesserafin.Server.Core/Library/ItemResolutionService.cs",usings+"""namespace Tesserafin.Server.Core.Library;

/// <summary>Filesystem resolution and typed alternate construction without write callbacks.</summary>
internal sealed class ItemResolutionService(
    IServerConfigurationManager configuration,
    IFileSystem files,
    IServerApplicationHost appHost,
    ILibraryItemOptions options,
    IItemLookupService lookup,
    NamingOptions namingOptions,
    ILogger<ItemResolutionService> logger) : IItemResolutionService
{
    private readonly IServerConfigurationManager _configurationManager = configuration;
    private readonly IFileSystem _fileSystem = files;
    private readonly IServerApplicationHost _appHost = appHost;
    private readonly NamingOptions _namingOptions = namingOptions;
    private readonly ILogger<ItemResolutionService> _logger = logger;

    private IResolverIgnoreRule[] EntityResolutionIgnoreRules { get; set; } = [];

    private IItemResolver[] EntityResolvers { get; set; } = [];

    private IMultiItemResolver[] MultiItemResolvers { get; set; } = [];

    public void AddParts(IEnumerable<IResolverIgnoreRule> rules, IEnumerable<IItemResolver> resolvers)
    {
        EntityResolutionIgnoreRules = rules.ToArray();
        EntityResolvers = resolvers.OrderBy(i => i.Priority).ToArray();
        MultiItemResolvers = EntityResolvers.OfType<IMultiItemResolver>().ToArray();
    }

    public Guid GetNewItemId(string key, Type type) => ItemIdentity.Create(_configurationManager, key, type);

    public BaseItem? GetItemById(Guid id) => lookup.GetItemById(id);

    public T? GetItemById<T>(Guid id) where T : BaseItem => lookup.GetItemById<T>(id);

    public bool AreEqualPaths(string first, string second) => _fileSystem.AreEqual(first, second);

    public LibraryOptions GetLibraryOptions(BaseItem item) => options.GetLibraryOptions(item);

    public CollectionType? GetConfiguredContentType(string path) => options.GetConfiguredContentType(path);

    private CollectionType? GetContentTypeOverride(string path, bool inherit) => options.GetConfiguredContentType(path, inherit);

"""+methods+"\n}\n")

s=original
for signature in ("        private BaseItem? ResolveItem(","        private BaseItem? Resolve(","        private void SetAdditionalPartsFromStack(","        public Video? ResolveAlternateVersion(","        private BaseItem? ResolvePath(","        public List<FileSystemMetadata> NormalizeRootPathList(","        private IEnumerable<BaseItem> ResolveFileList("):
    s=remove_method(s,signature)
s=s.replace("=> ResolvePath(fileInfo, directoryService ?? new DirectoryService(_fileSystem), null, parent, collectionType);","=> _resolution.ResolvePath(fileInfo, parent, directoryService, collectionType);")
for signature,args in (("        public IEnumerable<BaseItem> ResolvePaths(IEnumerable<FileSystemMetadata> files, IDirectoryService directoryService, Folder parent, LibraryOptions libraryOptions, CollectionType? collectionType = null)","files, directoryService, parent, libraryOptions, collectionType"),("        public IEnumerable<BaseItem> ResolvePaths(\n","files, directoryService, parent, libraryOptions, collectionType, resolvers")):
    s=delegate(s,signature,"_resolution.ResolvePaths("+args+")")
s=s.replace("=> EntityResolutionIgnoreRules.Any(r => r.ShouldIgnore(file, parent));","=> _resolution.IgnoreFile(file, parent);")
s=s.replace("            EntityResolutionIgnoreRules = rules.ToArray();\n            EntityResolvers = resolvers.OrderBy(i => i.Priority).ToArray();\n            MultiItemResolvers = EntityResolvers.OfType<IMultiItemResolver>().ToArray();","            _resolution.AddParts(rules, resolvers);")
for signature in ("        private IResolverIgnoreRule[] EntityResolutionIgnoreRules","        private IItemResolver[] EntityResolvers","        private IMultiItemResolver[] MultiItemResolvers"):
    pos=s.index(signature);last=s.index("\n",pos)
    while pos:
        prev=s.rfind("\n",0,pos-1)+1
        if s[prev:pos].strip().startswith("///") or not s[prev:pos].strip(): pos=prev
        else: break
    s=s[:pos]+s[last:]
pos=s.index("        public void RegisterItem(")
s=s[:pos]+"""        public Video? ResolveAlternateVersion(string path, Type expectedVideoType, Folder? parent, CollectionType? collectionType)
            => _resolution.ResolveAlternateVersion(path, expectedVideoType, parent, collectionType);

        public List<FileSystemMetadata> NormalizeRootPathList(IEnumerable<FileSystemMetadata> paths)
            => _resolution.NormalizeRootPathList(paths);

        private BaseItem? ResolvePath(FileSystemMetadata fileInfo, IDirectoryService directoryService, IItemResolver[]? resolvers, Folder? parent = null, CollectionType? collectionType = null, LibraryOptions? libraryOptions = null)
            => _resolution.ResolvePath(fileInfo, directoryService, resolvers, parent, collectionType, libraryOptions);

        public bool AreEqualPaths(string first, string second) => _fileSystem.AreEqual(first, second);

"""+s[pos:]
s=s.replace("                                SetAdditionalPartsFromStack(altVideo, path);","")
s=s.replace("        private readonly IItemImageService _images;","        private readonly IItemImageService _images;\n        private readonly IItemResolutionService _resolution;\n        private readonly ILibraryItemOptions _options;")
s=s.replace("        public LibraryManager(\n","        public LibraryManager(\n            IItemResolutionService resolution,\n            ILibraryItemOptions options,\n")
s=s.replace("            _images = images;","            _resolution = resolution;\n            _options = options;\n            _images = images;")
s=s.replace('        /// <param name="images">','        /// <param name="resolution">Filesystem resolution.</param>\n        /// <param name="options">Library options and content policy.</param>\n        /// <param name="images">',1)
s=delegate(s,"        private Guid GetNewItemIdInternal(","ItemIdentity.Create(_configurationManager, key, type, forceCaseInsensitive)")
for signature,expr in (("        public LibraryOptions GetLibraryOptions(","_options.GetLibraryOptions(item)"),("        private CollectionType? GetContentTypeOverride(","_options.GetConfiguredContentType(path, inherit)"),("        private CollectionType? GetTopFolderContentType(","_options.GetTopFolderContentType(item)")):
    s=delegate(s,signature,expr)
put(manager_path,s)
p="Tesserafin.Server.Core/Library/ItemStore.cs"
put(p,delegate(read(p),"        public Guid GetNewItemId(","ItemIdentity.Create(_configurationManager, key, type)"))
p="Tesserafin.Server.Core/Library/ResolverHelper.cs"
s=read(p).replace("ILibraryManager libraryManager","IItemResolutionPolicy policy").replace("libraryManager.GetNewItemId","policy.GetNewItemId").replace('name="libraryManager"','name="policy"').replace("The library manager.","The resolution policy.").replace("item.GetParents()","item.GetParents(policy)")
s=s.replace("IDirectoryService directoryService)","IDirectoryService directoryService, bool useFileCreationTime)").replace("IItemResolutionPolicy policy)","IItemResolutionPolicy policy, bool useFileCreationTime)")
s=s.replace("SetDateCreated(item, fileInfo);","SetDateCreated(item, fileInfo, useFileCreationTime);").replace("EnsureDates(fileSystem, item, args);","EnsureDates(fileSystem, item, args, useFileCreationTime);").replace("ItemResolveArgs args)\n        {","ItemResolveArgs args, bool useFileCreationTime)\n        {").replace("SetDateCreated(item, childData);","SetDateCreated(item, childData, useFileCreationTime);").replace("SetDateCreated(item, fileData);","SetDateCreated(item, fileData, useFileCreationTime);").replace("SetDateCreated(item, args.FileInfo);","SetDateCreated(item, args.FileInfo, useFileCreationTime);").replace("FileSystemMetadata? info)","FileSystemMetadata? info, bool useFileCreationTime)").replace("            var config = BaseItem.ConfigurationManager.GetMetadataConfiguration();\n\n            if (config.UseFileCreationTimeForDateAdded)","            if (useFileCreationTime)")
s=s.replace("        /// <returns>True if initializing was successful.</returns>",'        /// <param name="useFileCreationTime">Whether the added date uses file creation time.</param>\n        /// <returns>True if initializing was successful.</returns>').replace('        /// <param name="policy">The resolution policy.</param>\n        public static void','        /// <param name="policy">The resolution policy.</param>\n        /// <param name="useFileCreationTime">Whether the added date uses file creation time.</param>\n        public static void').replace('        /// <param name="args">The args.</param>\n        private static void EnsureDates','        /// <param name="args">The args.</param>\n        /// <param name="useFileCreationTime">Whether the added date uses file creation time.</param>\n        private static void EnsureDates')
put(p,s)

# Metadata export is independent of provider orchestration.
put("Tesserafin.Controller/Providers/IItemMetadataExporter.cs","""#pragma warning disable CS1591

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
""")
p="Tesserafin.Providers/Manager/ProviderManager.cs"
s=read(p)
a,b=bounds(s,"        private async Task SaveMetadataAsync(",False);save=s[a:b]
a,b=bounds(s,"        private bool IsSaverEnabledForItem(",False);predicate=s[a:b]
save=save.replace("private async Task SaveMetadataAsync(BaseItem item, ItemUpdateType updateType, IEnumerable<IMetadataSaver> savers)","public async Task SaveAsync(BaseItem item, ItemUpdateType updateType, CancellationToken cancellationToken, IEnumerable<string>? selectedSavers = null)").replace("            var libraryOptions = _libraryManager.GetLibraryOptions(item);","            cancellationToken.ThrowIfCancellationRequested();\n            var libraryOptions = options.GetLibraryOptions(item);\n            var savers = selectedSavers is null ? _savers : _savers.Where(i => selectedSavers.Contains(i.Name, StringComparer.OrdinalIgnoreCase));").replace("IsSaverEnabledForItem","IsEnabled").replace("_libraryMonitor.","_monitor.Value.").replace("saver.SaveAsync(item, CancellationToken.None)","saver.SaveAsync(item, cancellationToken)").replace("            foreach (var saver in applicableSavers)\n            {","            foreach (var saver in applicableSavers)\n            {\n                cancellationToken.ThrowIfCancellationRequested();").replace('                    catch (Exception ex)\n                    {\n                        _logger.LogError(ex, "Error in metadata saver");','                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)\n                    {\n                        throw;\n                    }\n                    catch (Exception ex)\n                    {\n                        _logger.LogError(ex, "Error in metadata saver");')
predicate=predicate.replace("IsSaverEnabledForItem","IsEnabled").replace("GetMetadataOptions(item)","configuration.GetMetadataOptionsForType(item.GetType().Name) ?? new MetadataOptions()")
put("Tesserafin.Providers/Manager/ItemMetadataExporter.cs","""using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Extensions;
using Tesserafin.Model.Configuration;

namespace Tesserafin.Providers.Manager;

/// <summary>Owns sidecar export selection and file watcher coordination.</summary>
/// <param name="configuration">Metadata configuration.</param>
/// <param name="options">Effective library options.</param>
/// <param name="monitor">Watcher coordination, evaluated only for an actual file export.</param>
/// <param name="logger">The logger.</param>
public sealed class ItemMetadataExporter(
    IServerConfigurationManager configuration,
    ILibraryItemOptions options,
    Lazy<ILibraryMonitor> monitor,
    ILogger<ItemMetadataExporter> logger) : IItemMetadataExporter
{
    private readonly Lazy<ILibraryMonitor> _monitor = monitor;
    private readonly ILogger<ItemMetadataExporter> _logger = logger;
    private IMetadataSaver[] _savers = [];

    /// <inheritdoc />
    public void AddParts(IEnumerable<IMetadataSaver> savers) => _savers = savers.ToArray();

    /// <inheritdoc />
    public IEnumerable<IMetadataSaver> GetSavers(BaseItem item, LibraryOptions libraryOptions, ItemUpdateType reason, bool includeDisabled)
        => _savers.Where(s => IsEnabled(s, item, libraryOptions, reason, includeDisabled));

    /// <inheritdoc />
"""+save+"\n\n"+predicate+"\n}\n")
a=s.index("        /// <inheritdoc/>\n        public Task SaveMetadataAsync(")
b=s.index("        /// <inheritdoc/>\n        public Task<IEnumerable<RemoteSearchResult>>",a)
s=s[:a]+s[b:]
s=s.replace("        private IMetadataSaver[] _savers = [];","        private readonly IItemMetadataExporter _metadataExporter;").replace("            IEnumerable<IMetadataSaver> metadataSavers,\n","").replace("            _savers = metadataSavers.ToArray();","")
s=s.replace("return _savers.Where(i => IsSaverEnabledForItem(i, item, libraryOptions, ItemUpdateType.MetadataEdit, false));","return _metadataExporter.GetSavers(item, libraryOptions, ItemUpdateType.MetadataEdit, false);").replace("_savers.Where(i => IsSaverEnabledForItem(i, item, libraryOptions, ItemUpdateType.MetadataEdit, true))","_metadataExporter.GetSavers(item, libraryOptions, ItemUpdateType.MetadataEdit, true)")
s=s.replace("        public ProviderManager(\n","        public ProviderManager(\n            IItemMetadataExporter metadataExporter,\n")
body=s.index("{",s.index("        public ProviderManager("))+1
s=s[:body]+"\n            _metadataExporter = metadataExporter;"+s[body:]
s=re.sub(r'        /// <param name="metadataSavers">[^\n]*\n',"",s)
put(p,s)
p="Tesserafin.Controller/Providers/IProviderManager.cs"
s=read(p);a=s.index("        /// <summary>\n        /// Saves the metadata.");b=s.index("        /// <summary>\n        /// Gets the metadata options.",a)
s=s[:a]+s[b:]
s=s.replace("            IEnumerable<IMetadataSaver> metadataSavers,\n","")
s=re.sub(r'        /// <param name="metadataSavers">[^\n]*\n',"",s);put(p,s)
p="Tesserafin.XbmcMetadata/NfoUserDataSaver.cs"
s=read(p).replace("IProviderManager","IItemMetadataExporter").replace("_providerManager","_metadataExporter").replace("providerManager","metadataExporter").replace(".SaveMetadataAsync(item, ItemUpdateType.MetadataDownload, [BaseNfoSaver.SaverName])",".SaveAsync(item, ItemUpdateType.MetadataDownload, CancellationToken.None, [BaseNfoSaver.SaverName])")
put(p,s)

put("Tesserafin.Controller/Library/IItemWriteCommands.cs","""#pragma warning disable CS1591

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Entities;

namespace Tesserafin.Controller.Library;

/// <summary>Item graph writes, staged metadata and post-persistence publication.</summary>
public interface IItemWriteCommands
{
    void CreateItem(BaseItem item, BaseItem? parent);

    void CreateItems(IReadOnlyList<BaseItem> items, BaseItem? parent, CancellationToken cancellationToken);

    Task UpdateItemAsync(BaseItem item, BaseItem? parent, ItemUpdateType reason, CancellationToken cancellationToken);

    Task UpdateItemsAsync(IReadOnlyList<BaseItem> items, BaseItem? parent, ItemUpdateType reason, CancellationToken cancellationToken);

    Task ReattachUserDataAsync(BaseItem item, CancellationToken cancellationToken);
}
""")
put("Tesserafin.Server.Core/Library/ItemVersionGraphPlanner.cs","""using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Extensions;

namespace Tesserafin.Server.Core.Library;

/// <summary>Prepares the typed local-version graph without saving or publishing anything.</summary>
internal sealed class ItemVersionGraphPlanner(IItemResolutionService resolution, ILibraryItemOptions options, IItemLookupService lookup)
{
    public IEnumerable<Guid> GetLocalVersionIds(Video video)
        => video.LocalAlternateVersions.Where(path => !string.IsNullOrEmpty(path)).Select(path => resolution.GetNewItemId(path, video.GetType()));

    public List<BaseItem> Prepare(IReadOnlyList<BaseItem> items, BaseItem? parent, CancellationToken cancellationToken)
    {
        var ids = new HashSet<Guid>();
        var result = new List<BaseItem>(items.Count);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(item);
            if (item.Id.IsEmpty() || !ids.Add(item.Id))
            {
                throw new ArgumentException("Write items must have distinct, nonempty identities.", nameof(items));
            }

            result.Add(item);
        }

        foreach (var item in items)
        {
            if (item is not Video video || video.LocalAlternateVersions.Length == 0)
            {
                continue;
            }

            var collectionType = options.GetTopFolderContentType(parent);
            foreach (var path in video.LocalAlternateVersions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                var id = resolution.GetNewItemId(path, video.GetType());
                if (ids.Contains(id) || lookup.GetItemById(id) is not null)
                {
                    continue;
                }

                var alternate = resolution.ResolveAlternateVersion(path, video.GetType(), parent as Folder, collectionType);
                if (alternate is not null)
                {
                    if (!alternate.Id.Equals(id) || alternate.GetType() != video.GetType())
                    {
                        throw new InvalidOperationException("Resolved alternate identity/type does not match the primary.");
                    }

                    alternate.OwnerId = video.Id;
                    alternate.SetPrimaryVersionId(video.Id);
                    ids.Add(id);
                    result.Add(alternate);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
""")
put("Tesserafin.Server.Core/Library/ItemWriteCommands.cs","""using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;

namespace Tesserafin.Server.Core.Library;

/// <summary>Prepares item/version writes, saves their graph, then publishes cache and notifications.</summary>
internal sealed class ItemWriteCommands(
    ItemVersionGraphPlanner versions,
    IItemPersistenceService persistence,
    IItemCacheStore cache,
    IItemLookupService lookup,
    ILinkedChildrenService linkedChildren,
    IItemMetadataExporter metadata,
    IItemChangePublisher changes,
    ActiveRecordings recordings,
    ILogger<ItemWriteCommands> logger) : IItemWriteCommands
{
    public void CreateItem(BaseItem item, BaseItem? parent) => CreateItems([item], parent, CancellationToken.None);

    public void CreateItems(IReadOnlyList<BaseItem> items, BaseItem? parent, CancellationToken cancellationToken)
    {
        var graph = versions.Prepare(items, parent, cancellationToken);
        persistence.SaveItems(graph, cancellationToken);
        PublishCache(graph, parent);
        foreach (var item in items)
        {
            if (IsLibraryItem(item))
            {
                try
                {
                    changes.Added(item, parent ?? item.GetParent(lookup));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unable to prepare creation notification for item {ItemId}", item.Id);
                }
            }
        }
    }

    public Task UpdateItemAsync(BaseItem item, BaseItem? parent, ItemUpdateType reason, CancellationToken cancellationToken)
        => UpdateItemsCoreAsync([item], parent, reason, cancellationToken, true);

    public Task UpdateItemsAsync(IReadOnlyList<BaseItem> items, BaseItem? parent, ItemUpdateType reason, CancellationToken cancellationToken)
        => UpdateItemsCoreAsync(items, parent, reason, cancellationToken, false);

    private async Task UpdateItemsCoreAsync(IReadOnlyList<BaseItem> items, BaseItem? parent, ItemUpdateType reason, CancellationToken cancellationToken, bool propagateVersions)
    {
        var prepared = versions.Prepare(items, parent, cancellationToken);
        var originals = prepared.ToDictionary(i => i.Id);
        var drafts = prepared.ToDictionary(i => i.Id, i => i.CopyForWrite());
        var propagated = new HashSet<Guid>();
        var queue = new Queue<Video>(propagateVersions ? drafts.Values.OfType<Video>() : []);
        var visited = new HashSet<Guid>();
        while (queue.TryDequeue(out var video))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(video.Id))
            {
                continue;
            }

            var ids = linkedChildren.GetLinkedChildrenIds(video.Id, (int)LinkedChildType.LocalAlternateVersion)
                .Concat(versions.GetLocalVersionIds(video))
                .Concat(drafts.Values.OfType<Video>().Where(v => v.OwnerId.Equals(video.Id)).Select(v => v.Id)).Distinct().ToArray();
            foreach (var id in ids)
            {
                if (visited.Contains(id))
                {
                    continue;
                }

                if (!drafts.TryGetValue(id, out var alternate))
                {
                    var original = lookup.GetItemById(id);
                    if (original is null)
                    {
                        continue;
                    }

                    originals.Add(id, original);
                    alternate = original.CopyForWrite();
                    drafts.Add(id, alternate);
                }

                CopyVersionMetadata(video, alternate);
                propagated.Add(id);
                if (alternate is Video next)
                {
                    queue.Enqueue(next);
                }
            }
        }

        var parents = originals.ToDictionary(p => p.Key, p => items.Contains(p.Value) && parent is not null ? parent : p.Value.GetParent(lookup));
        foreach (var draft in drafts.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            draft.DateLastSaved = DateTime.UtcNow;
            if (draft.IsFileProtocol)
            {
                await metadata.SaveAsync(draft, propagated.Contains(draft.Id) ? ItemUpdateType.MetadataDownload : reason, cancellationToken).ConfigureAwait(false);
            }

            draft.DateLastSaved = DateTime.UtcNow;
        }

        cancellationToken.ThrowIfCancellationRequested();
        persistence.SaveItems(drafts.Values.ToArray(), cancellationToken);
        foreach (var (id, draft) in drafts)
        {
            var original = originals[id];
            original.DateLastSaved = draft.DateLastSaved;
            if (propagated.Contains(id))
            {
                CopyVersionMetadata(draft, original);
            }
        }

        PublishCache(originals.Values.ToArray(), parent);
        foreach (var item in originals.Values)
        {
            if (IsLibraryItem(item))
            {
                changes.Updated(item, parents[item.Id], propagated.Contains(item.Id) ? ItemUpdateType.MetadataDownload : reason);
            }
        }
    }

    public Task ReattachUserDataAsync(BaseItem item, CancellationToken cancellationToken)
        => persistence.ReattachUserDataAsync(item, cancellationToken);

    private static void CopyVersionMetadata(BaseItem source, BaseItem target)
    {
        target.ImageInfos = source.ImageInfos;
        target.Overview = source.Overview;
        target.ProductionYear = source.ProductionYear;
        target.PremiereDate = source.PremiereDate;
        target.CommunityRating = source.CommunityRating;
        target.OfficialRating = source.OfficialRating;
        target.Genres = source.Genres;
        target.ProviderIds = source.ProviderIds;
    }

    private void PublishCache(IReadOnlyCollection<BaseItem> graph, BaseItem? parent)
    {
        if (parent is Folder folder)
        {
            folder.Children = null;
            folder.UserData = null;
        }

        try
        {
            foreach (var item in graph)
            {
                cache.Register(item);
            }
        }
        catch
        {
            cache.RemoveRange(graph.Select(i => i.Id));
            throw;
        }
    }

    private bool IsLibraryItem(BaseItem item)
        => item is not LiveTvProgram and not LiveTvChannel
            && item.ChannelId.Equals(Guid.Empty)
            && (item is not Video || recordings.GetActiveRecordingInfo(item.Path) is null);
}
""")

p=manager_path;s=read(p)
a=s.index("        /// <inheritdoc />\n        public void CreateItem(");b=s.index("        /// <summary>\n        /// Retrieves the item.",a);s=s[:a]+s[b:]
s=re.sub(r"\bCreateItems\(","_writes.CreateItems(",s);s=re.sub(r"\bCreateItem\(","_writes.CreateItem(",s)
s=s.replace("RunMetadataSavers(","_metadata.SaveAsync(")
s=s.replace("        private readonly IItemImageService _images;","        private readonly IItemMetadataExporter _metadata;").replace("IItemImageService images,","IItemMetadataExporter metadata,").replace("_images = images;","_metadata = metadata;")
s=s.replace("        private readonly IItemPersistenceService _persistenceService;","        private readonly IItemWriteCommands _writes;").replace("IItemPersistenceService persistenceService,","IItemWriteCommands writes,").replace("_persistenceService = persistenceService;","_writes = writes;")
s=re.sub(r"        private readonly IItemChangePublisher _changes;\n","",s).replace("            IItemChangePublisher changes,\n","").replace("            _changes = changes;\n","")
s=re.sub(r'        /// <param name="changes">[^\n]*\n',"",s)
s=s.replace('name="images">Explicit image preparation and commit lifecycle.','name="metadata">Explicit metadata export.').replace('name="persistenceService">The item persistence service.','name="writes">Item graph write ownership.')
put(p,s)
p="Tesserafin.Controller/Library/ILibraryManager.cs";s=read(p)
for name in ("CreateItem","CreateItems","UpdateItemAsync","UpdateItemsAsync","ReattachUserDataAsync"):
    s,n=re.subn(r"(?:        ///[^\n]*\n)*        (?:void|Task) "+name+r"\([^;]*;\n","",s)
    if n != 1: raise RuntimeError("Write facade mismatch: " + name)
put(p,s)
p="Tesserafin.Controller/Entities/BaseItem.cs";s=read(p)
pos=s.index("        public virtual async Task UpdateToRepositoryAsync(")
s=s[:pos]+"""        /// <summary>Copies item state for write preparation without changing the published instance.</summary>
        /// <returns>A shallow copy; mutable child objects remain shared.</returns>
        public BaseItem CopyForWrite() => (BaseItem)MemberwiseClone();

"""+s[pos:]
s=s.replace("UpdateToRepositoryAsync(ItemUpdateType updateReason, CancellationToken cancellationToken)","UpdateToRepositoryAsync(IItemWriteCommands writes, ItemUpdateType updateReason, CancellationToken cancellationToken)").replace("await LibraryManager.UpdateItemAsync(this, GetParent(), updateReason, cancellationToken)","await writes.UpdateItemAsync(this, null, updateReason, cancellationToken)").replace("ReattachUserDataAsync(CancellationToken cancellationToken)","ReattachUserDataAsync(IItemWriteCommands writes, CancellationToken cancellationToken)").replace("await LibraryManager.ReattachUserDataAsync(this, cancellationToken)","await writes.ReattachUserDataAsync(this, cancellationToken)")
put(p,s)
p="Tesserafin.Controller/Entities/Video.cs";s=read(p);put(p,remove_method(s,"        public override async Task UpdateToRepositoryAsync("))

p="Tesserafin.Server.Core/ApplicationHost.cs";s=read(p)
s=s.replace("            serviceCollection.AddSingleton<IItemStore, ItemStore>();","""            serviceCollection.AddSingleton<IItemStore, ItemStore>();
            serviceCollection.AddSingleton<ILibraryItemOptions, LibraryItemOptions>();
            serviceCollection.AddSingleton<IItemResolutionService, ItemResolutionService>();
            serviceCollection.AddSingleton<IItemMetadataExporter, ItemMetadataExporter>();
            serviceCollection.AddSingleton<ItemVersionGraphPlanner>();
            serviceCollection.AddSingleton<IItemWriteCommands, ItemWriteCommands>();""")
s=s.replace("            Resolve<IProviderManager>().AddParts(","            Resolve<IItemMetadataExporter>().AddParts(GetExports<IMetadataSaver>());\n\n            Resolve<IProviderManager>().AddParts(").replace("                GetExports<IMetadataSaver>(),\n","")
put(p,s)

# Migrate explicit command scope through refresh, scan, API and maintenance callers.
paths = [p for p in root.rglob("*.cs") if not any(part in ("obj","bin") for part in p.parts)]
method_names=set()
for path in (root / "Tesserafin.Controller").rglob("*.cs"):
    method_names.update(re.findall(r"(\w+)\(\s*IItemDeletionCommands deletions,",path.read_text()))
needs_injection=set()
for path in paths:
    text=path.read_text(); before=text
    is_test="tests" in path.relative_to(root).parts
    derived=bool(re.search(r"class\s+\w+\s*:\s*MetadataService<",text))
    owner="Writes" if derived else "_writes"
    for method in method_names:
        text=re.sub(r"\b"+method+r"\(\s*IItemDeletionCommands deletions,",method+"(IItemDeletionCommands deletions, IItemWriteCommands writes,",text)
        text=re.sub(r"\b"+method+r"\(deletions,(?!\s*writes\s*,)",method+"(deletions, writes,",text)
        for field in ("_deletions","_itemDeletionCommands","_deletionCommands"):
            text,n=re.subn(r"(\b"+method+r"\(\s*"+field+r",)(?!\s*(?:_writes|Writes)\s*,)",r"\1 "+owner+",",text)
            if n and not is_test: needs_injection.add(path)
    if before!=text:
        if "using Tesserafin.Controller.Library;" not in text: text="using Tesserafin.Controller.Library;\n"+text
        put(str(path.relative_to(root)),text)

for rel in ("Tesserafin.Controller/Entities/BaseItem.cs","Tesserafin.Controller/Entities/Folder.cs","Tesserafin.Controller/Entities/Video.cs"):
    text=read(rel).replace("DeleteImageAsync(ImageType type, int index)","DeleteImageAsync(IItemWriteCommands writes, ImageType type, int index)").replace("SwapImagesAsync(ImageType type, int index1, int index2)","SwapImagesAsync(IItemWriteCommands writes, ImageType type, int index1, int index2)").replace("AddChild(BaseItem item)","AddChild(IItemWriteCommands writes, BaseItem item)")
    text=re.sub(r"(?<!Task )\bUpdateToRepositoryAsync\((?!IItemWriteCommands)","UpdateToRepositoryAsync(writes, ",text)
    text=text.replace("LibraryManager.CreateItem(","writes.CreateItem(").replace("LibraryManager.CreateItems(","writes.CreateItems(").replace(".ReattachUserDataAsync(cancellationToken)",".ReattachUserDataAsync(writes, cancellationToken)")
    put(rel,text)

for path in paths:
    rel=str(path.relative_to(root))
    if "Tesserafin.Controller/Entities/" in rel or "tests" in path.relative_to(root).parts: continue
    text=path.read_text(); before=text
    derived=bool(re.search(r"class\s+\w+\s*:\s*MetadataService<",text))
    owner="Writes" if derived or path.name=="MetadataService.cs" else "_writes"
    text,n=re.subn(r"\.UpdateToRepositoryAsync\((?!IItemWriteCommands)",".UpdateToRepositoryAsync("+owner+", ",text)
    if n: needs_injection.add(path)
    for field in ("_libraryManager","LibraryManager"):
        for method in ("CreateItem","CreateItems","UpdateItemAsync","UpdateItemsAsync","ReattachUserDataAsync"):
            text,n=re.subn(r"\b"+field+r"\."+method+r"\(",owner+"."+method+"(",text)
            if n: needs_injection.add(path)
    for method in ("DeleteImageAsync","SwapImagesAsync"):
        text,n=re.subn(r"\."+method+r"\(","."+method+"("+owner+", ",text)
        if n: needs_injection.add(path)
    text,n=re.subn(r"\.ReattachUserDataAsync\(cancellationToken\)",".ReattachUserDataAsync("+owner+", cancellationToken)",text)
    if n: needs_injection.add(path)
    if path.name in ("CollectionManager.cs","PlaylistManager.cs"):
        text=text.replace("parentFolder.AddChild(","parentFolder.AddChild(_writes, ");needs_injection.add(path)
    if path.name=="SeriesMetadataService.cs": text=text.replace("series.AddChild(","series.AddChild(Writes, ")
    if path.name.endswith("PostScanTask.cs"):
        text,n=re.subn(r"(new (?:Studios|Genres|Artists|MusicGenres)Validator\()",r"\1_writes, ",text)
        if n: needs_injection.add(path)
    if before!=text: put(rel,text)

p="Tesserafin.Providers/Manager/MetadataService.cs";s=read(p)
s=s.replace("        protected MetadataService(\n","        protected MetadataService(\n            IItemWriteCommands writes,\n").replace("            ServerConfigurationManager = serverConfigurationManager;","            Writes = writes;\n            ServerConfigurationManager = serverConfigurationManager;")
s=s.replace("        protected ItemImageProvider ImageProvider { get; }","        protected IItemWriteCommands Writes { get; }\n\n        protected ItemImageProvider ImageProvider { get; }")
put(p,s.replace("_writes","Writes"))
needs_injection.discard(root/p)
for path in (root/"Tesserafin.Providers").rglob("*.cs"):
    text=path.read_text()
    if re.search(r"class\s+\w+\s*:\s*MetadataService<",text):
        name=re.search(r"class\s+(\w+)\s*:",text).group(1)
        text=re.sub(r"((?:public|protected) "+name+r"\(\s*)",r"\1IItemWriteCommands writes,\n            ",text,1)
        text=text.replace(": base(",": base(writes, ",1).replace("_writes","Writes")
        if "using Tesserafin.Controller.Library;" not in text: text="using Tesserafin.Controller.Library;\n"+text
        put(str(path.relative_to(root)),text);needs_injection.discard(path)
for path in sorted(needs_injection):
    text=path.read_text()
    names=re.findall(r"(?:class|record)\s+(\w+)",text)
    name=next((name for name in names if re.search(r"(?:(?:public|internal|protected)\s+"+name+r"|class\s+"+name+r")\(",text)),None)
    if not name: raise RuntimeError("Write constructor needs explicit migration: "+str(path.relative_to(root)))
    put(str(path.relative_to(root)),inject(text,name))

p=manager_path;s=read(p).replace("new PeopleValidator(deletions, this,","new PeopleValidator(writes, deletions, this,");put(p,s)
for path in paths:
    if "tests" not in path.relative_to(root).parts: continue
    text=path.read_text().replace("override Task UpdateToRepositoryAsync(ItemUpdateType","override Task UpdateToRepositoryAsync(IItemWriteCommands writes, ItemUpdateType")
    put(str(path.relative_to(root)),text)

# A real shared composition for the manager/command characterization fixtures.
put("tests/Tesserafin.Server.Implementations.Tests/Library/LibraryWriteFixture.cs","""using AutoFixture;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.IO;
using Tesserafin.Naming.Common;
using Tesserafin.Server.Core.Library;

namespace Tesserafin.Server.Implementations.Tests.Library;

internal static class LibraryWriteFixture
{
    public static IItemWriteCommands Register(IFixture fixture)
    {
        var configuration = fixture.Create<IServerConfigurationManager>();
        if (configuration.GetConfiguration("metadata") is null)
        {
            Mock.Get(configuration).Setup(c => c.GetConfiguration("metadata")).Returns(new MetadataConfiguration());
        }

        var options = new LibraryItemOptions(fixture.Create<ICollectionFolderResolver>(), fixture.Create<IItemLookupService>(), fixture.Create<IUserRootFolderProvider>(), configuration, fixture.Create<IFileSystem>());
        fixture.Inject<ILibraryItemOptions>(options);
        var resolution = new ItemResolutionService(configuration, fixture.Create<IFileSystem>(), fixture.Create<IServerApplicationHost>(), options, fixture.Create<IItemLookupService>(), fixture.Create<NamingOptions>(), NullLogger<ItemResolutionService>.Instance);
        fixture.Inject<IItemResolutionService>(resolution);
        var versions = new ItemVersionGraphPlanner(resolution, options, fixture.Create<IItemLookupService>());
        fixture.Inject(versions);
        var writes = new ItemWriteCommands(versions, fixture.Create<IItemPersistenceService>(), fixture.Create<IItemCacheStore>(), fixture.Create<IItemLookupService>(), fixture.Create<ILinkedChildrenService>(), fixture.Create<IItemMetadataExporter>(), fixture.Create<IItemChangePublisher>(), new ActiveRecordings(), NullLogger<ItemWriteCommands>.Instance);
        fixture.Inject<IItemWriteCommands>(writes);
        return writes;
    }
}
""")
for path in (root/"tests").rglob("*.cs"):
    text=path.read_text();before=text
    if "fixture.Build<Tesserafin.Server.Core.Library.LibraryManager>()" in text:
        needs_write=bool(re.search(r"_libraryManager\.(?:CreateItems|CreateItem|UpdateItemAsync|UpdateItemsAsync)\(",text))
        if needs_write:
            match=re.search(r"(?m)^( *)private readonly .* _libraryManager;",text)
            if not match: raise RuntimeError("Write fixture field not found: "+str(path))
            text=text[:match.start()]+match.group(1)+"private readonly IItemWriteCommands _writes;\n"+text[match.start():]
        prefix="_writes = " if needs_write else ""
        text=text.replace("        _libraryManager = fixture.Build<Tesserafin.Server.Core.Library.LibraryManager>()","        "+prefix+"Tesserafin.Server.Implementations.Tests.Library.LibraryWriteFixture.Register(fixture);\n\n        _libraryManager = fixture.Build<Tesserafin.Server.Core.Library.LibraryManager>()")
        for method in ("CreateItem","CreateItems","UpdateItemAsync","UpdateItemsAsync"): text=text.replace("_libraryManager."+method+"(","_writes."+method+"(")
    if path.name in ("ItemImageServiceTests.cs","ItemDeletionCommandsTests.cs"):
        text=text.replace("        Assert.Equal(29, dependencies.Length);","        Assert.Contains(typeof(IItemWriteCommands), dependencies);")
    if path.name=="LibraryManagerItemLookupTests.cs":
        text=text.replace("new Tesserafin.Server.Core.Library.LibraryScanTraversal(Mock.Of<IItemImageService>()","new Tesserafin.Server.Core.Library.LibraryScanTraversal(_writes, Mock.Of<IItemImageService>()")
    if before!=text: put(str(path.relative_to(root)),text)

p="tests/Tesserafin.Server.Implementations.Tests/Library/LibraryManager/LibraryManagerUserViewFactoryTests.cs"
s=read(p).replace("    private readonly Tesserafin.Server.Core.Library.UserViewFactory _userViewFactory;","    private readonly Tesserafin.Server.Core.Library.UserViewFactory _userViewFactory;\n    private readonly IItemWriteCommands _writes;")
s=s.replace("        Tesserafin.Server.Implementations.Tests.Library.LibraryWriteFixture.Register(fixture);\n\n","")
s=s.replace("        _userViewFactory = new Tesserafin.Server.Core.Library.UserViewFactory(","        _writes = Tesserafin.Server.Implementations.Tests.Library.LibraryWriteFixture.Register(fixture);\n        _userViewFactory = new Tesserafin.Server.Core.Library.UserViewFactory(")
s=s.replace("new Tesserafin.Server.Core.Library.UserViewFactory(\n","new Tesserafin.Server.Core.Library.UserViewFactory(\n            _writes,\n")
put(p,s)
p="tests/Tesserafin.Server.Implementations.Tests/Library/UserViewFactoryTests.cs"
s=read(p).replace("new UserViewFactory(\n","new UserViewFactory(\n            Mock.Of<IItemWriteCommands>(),\n").replace("        services.AddSingleton<IUserViewFactory, UserViewFactory>();","        services.AddSingleton<IItemWriteCommands>(Mock.Of<IItemWriteCommands>());\n        services.AddSingleton<IUserViewFactory, UserViewFactory>();");put(p,s)
p="tests/Tesserafin.Server.Implementations.Tests/Library/UserViewCatalogParityTests.cs"
s=read(p).replace("new Tesserafin.Server.Core.Library.UserViewFactory(\n","new Tesserafin.Server.Core.Library.UserViewFactory(\n                Mock.Of<IItemWriteCommands>(),\n");put(p,s)
p="tests/Tesserafin.Server.Implementations.Tests/Library/ChannelCatalogParityTests.cs"
s=read(p).replace("new ChannelManager(\n","new ChannelManager(\n                Mock.Of<IItemWriteCommands>(),\n");put(p,s)
p="tests/Tesserafin.Providers.Tests/Manager/ProviderManagerTests.cs"
s=read(p).replace("var providerManager = new ProviderManager(\n","var providerManager = new ProviderManager(\n                Mock.Of<IItemWriteCommands>(),\n                Mock.Of<IItemMetadataExporter>(),\n").replace("providerManager.AddParts(imageProviders, metadataServices, metadataProviders, metadataSavers, externalIds, externalUrlProviders);","providerManager.AddParts(imageProviders, metadataServices, metadataProviders, externalIds, externalUrlProviders);");put(p,s)
p="tests/Tesserafin.Providers.Tests/TV/EpisodeMetadataServiceTests.cs"
s=read(p).replace(": base(\n",": base(\n                Mock.Of<IItemWriteCommands>(),");put(p,s)

# Integration seeders use the same application command port as production.
p="tests/Tesserafin.Server.Integration.Tests/EndToEnd/LibraryItemSeeder.cs"
s=read(p).replace("ILibraryManager libraryManager","IItemWriteCommands writes").replace("nameof(libraryManager)","nameof(writes)").replace("ThrowIfNull(libraryManager)","ThrowIfNull(writes)").replace("libraryManager.CreateItems(","writes.CreateItems(").replace('name="libraryManager"','name="writes"').replace("ILibraryManager.CreateItems","IItemWriteCommands.CreateItems");put(p,s)
p="tests/Tesserafin.Server.Integration.Tests/PlaybackCredentials/MintingFixture.cs"
s=read(p).replace("ILibraryManager libraryManager,","IItemWriteCommands writes,").replace("libraryManager.CreateItems(","writes.CreateItems(").replace("Seed(libraryManager,","Seed(Factory.Services.GetRequiredService<IItemWriteCommands>(),");put(p,s)
for path in (root/"tests/Tesserafin.Server.Integration.Tests").rglob("*.cs"):
    text=path.read_text();before=text
    text=re.sub(r"(LibraryItemSeeder\.SeedVideo\(\s*)((?:[A-Za-z_][\w.]*\.)?Services\.GetRequiredService)<ILibraryManager>\(\)",r"\1\2<IItemWriteCommands>()",text)
    text=re.sub(r"(LibraryItemSeeder\.SeedVideo\(\s*)_libraryManager\b",r"\1_factory.Services.GetRequiredService<IItemWriteCommands>()",text)
    # First identify all local readers before changing calls in any method.
    locals_found=list(re.finditer(r"(?m)^([ \t]*)var (\w+) = ([^;\n]+)\.GetRequiredService<ILibraryManager>\(\);",text))
    relevant=set()
    for match in locals_found:
        name=match.group(2)
        if re.search(r"\b"+name+r"\.(?:CreateItems|CreateItem|UpdateItemAsync|UpdateItemsAsync)\(",text) or re.search(r"LibraryItemSeeder\.SeedVideo\(\s*"+name+r"\b",text):
            relevant.add(name)
    for name in relevant:
        for method in ("CreateItem","CreateItems","UpdateItemAsync","UpdateItemsAsync"): text=re.sub(r"\b"+name+r"\."+method+r"\(",name+"Writes."+method+"(",text)
        text=re.sub(r"(LibraryItemSeeder\.SeedVideo\(\s*)"+name+r"\b",r"\1"+name+"Writes",text)
    def add_local(match):
        indent,name,provider=match.groups()
        if name not in relevant: return match.group()
        return match.group()+"\n"+indent+"var "+name+"Writes = "+provider+".GetRequiredService<IItemWriteCommands>();"
    text=re.sub(r"(?m)^([ \t]*)var (\w+) = ([^;\n]+)\.GetRequiredService<ILibraryManager>\(\);",add_local,text)
    text=re.sub(r"GetRequiredService<ILibraryManager>\(\)(\.(?:CreateItems|CreateItem|UpdateItemAsync|UpdateItemsAsync)\()",r"GetRequiredService<IItemWriteCommands>()\1",text)
    if ".UpdateToRepositoryAsync(" in text:
        host="server" if "private sealed class Boot" in text else "_factory" if re.search(r"\b_factory\b",text) else "factory" if "factory.Services" in text else "Factory"
        text=text.replace(".UpdateToRepositoryAsync(",".UpdateToRepositoryAsync("+host+".Services.GetRequiredService<IItemWriteCommands>(), ")
    if "private static Guid AddFolder(ILibraryManager library, string directory)" in text:
        text=text.replace("private static Guid AddFolder(ILibraryManager library, string directory)","private static Guid AddFolder(ILibraryManager library, IItemWriteCommands libraryWrites, string directory)")
        text=re.sub(r"\bAddFolder\(library,\s*","AddFolder(library, libraryWrites, ",text)
    if path.name=="MediaBoundaryFixture.cs":
        text=text.replace("private Guid SeedItem(ILibraryManager libraryManager,","private Guid SeedItem(IItemWriteCommands libraryManagerWrites,").replace("SeedItem(libraryManager,","SeedItem(Factory.Services.GetRequiredService<IItemWriteCommands>(),")
    if before!=text: put(str(path.relative_to(root)),text)

# Complete XML documentation for the intentionally broken .NET signatures.
for path in [p for p in root.rglob("*.cs") if not any(part in ("obj","bin") for part in p.parts)]:
    text=path.read_text();before=text
    pattern=r"((?:^[ \t]*///[^\n]*\n)+)([ \t]*(?:(?:public|protected|private|internal|static|override|async|virtual|sealed|new|partial)\s+)*[\w<>,\[\]? .]+\([^)]*\))"
    def documentation(match):
        doc,signature=match.groups()
        additions=[("IItemWriteCommands","writes"),("IItemResolutionService","resolution"),("ILibraryItemOptions","options"),("IItemMetadataExporter","metadataExporter")]
        for kind,name in additions:
            if not re.search(r"\b"+kind+" "+name+r"\b",signature) or "<param " not in doc or 'name="'+name+'"' in doc: continue
            pos=signature.index(kind+" "+name);tags=list(re.finditer(r'(?m)^([ \t]*)/// <param name="([^"]+)"',doc))
            next_tag=None
            for tag in tags:
                token=re.search(r"\b"+re.escape(tag.group(2))+r"\s*[,)]",signature)
                if token and token.start()>pos: next_tag=tag;break
            if next_tag:
                point=next_tag.start();indent=next_tag.group(1)
                doc=doc[:point]+indent+'/// <param name="'+name+'">Explicit item write/resolution ownership.</param>\n'+doc[point:]
            elif tags:
                tag=tags[-1];end=doc.find("</param>",tag.end())+len("</param>")
                doc=doc[:end]+"\n"+tag.group(1)+'/// <param name="'+name+'">Explicit item write ownership.</param>'+doc[end:]
        return doc+signature
    text=re.sub(pattern,documentation,text,flags=re.M)
    if before!=text: put(str(path.relative_to(root)),text)

# Only remove measured disappearances from the static-service ratchet. Never add exceptions.
baseline_path="tests/Tesserafin.Server.Tests/Architecture/consolidation-ratchet-baseline.json"
baseline=json.loads(read(baseline_path))
if "entityStaticServices" in baseline and "entityStaticServiceAccessFiles" in baseline:
    pattern=re.compile(r"\b("+ "|".join(re.escape(x) for x in baseline["entityStaticServices"])+r")\b")
    baseline["entityStaticServiceAccessFiles"]=[path for path in baseline["entityStaticServiceAccessFiles"] if any(not line.lstrip().startswith("//") and pattern.search(line) for line in read(path).splitlines())]
    put(baseline_path,json.dumps(baseline,indent=2,ensure_ascii=False)+"\n")

# Canonical root using order, restricted to changed C# files.
changed=subprocess.check_output(["git","diff","--name-only"],text=True).splitlines()
changed += subprocess.check_output(["git","ls-files","--others","--exclude-standard"],text=True).splitlines()
for path in sorted(set(changed)):
    if not path.endswith(".cs"): continue
    text=read(path)
    matches=list(re.finditer(r"(?m)^using [^\n]+;\n",text))
    if matches:
        first,last=matches[0].start(),matches[-1].end()
        block=text[first:last]
        if all(not line.strip() or line.startswith("using ") for line in block.splitlines()):
            directives=sorted(set(m.group().rstrip("\n") for m in matches),key=lambda line:(not line.startswith("using System")," = " in line,line))
            text=text[:first]+"\n".join(directives)+"\n"+text[last:]
            put(path,text)
print("Approved write extraction materialized; changed files:",len(set(changed)),flush=True)

put("tests/Tesserafin.Server.Implementations.Tests/Library/ItemWriteCommandsTests.cs","""using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Data.Enums;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

public sealed class ItemWriteCommandsTests
{
    [Fact]
    public void Creation_PreparesOneTypedMultipartAlternate_ThenSavesCachesAndNotifies()
    {
        var context = new Context();
        var primary = context.Movie();
        var alternate = context.Alternate(primary);
        primary.LocalAlternateVersions = [alternate.Path, alternate.Path, string.Empty];
        var saved = false;
        context.Persistence.Setup(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>())).Callback((IReadOnlyList<BaseItem> graph, CancellationToken _) =>
        {
            Assert.Empty(context.Cache.Registered);
            Assert.Equal(2, graph.Count);
            Assert.Equal(primary.Id, graph[1].OwnerId);
            Assert.Equal(primary.Id, Assert.IsType<Movie>(graph[1]).PrimaryVersionId);
            Assert.Single(Assert.IsType<Movie>(graph[1]).AdditionalParts);
            saved = true;
        });
        context.Cache.BeforeRegister = () => Assert.True(saved);
        var notifications = 0;
        context.Events.ItemAdded += (_, args) =>
        {
            if (saved && context.Cache.Registered.Count == 2 && ReferenceEquals(primary, args.Item))
            {
                notifications++;
            }
        };

        context.Commands.CreateItems([primary], null, TestContext.Current.CancellationToken);

        Assert.Equal(1, notifications);
        context.Resolution.Verify(r => r.ResolveAlternateVersion(alternate.Path, typeof(Movie), null, null), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationFailure_DoesNotExportSaveOrPublish(bool wrongType)
    {
        var context = new Context();
        var primary = context.Movie();
        var alternate = context.Alternate(primary);
        primary.LocalAlternateVersions = [alternate.Path];
        if (wrongType)
        {
            context.Resolution.Setup(r => r.ResolveAlternateVersion(alternate.Path, typeof(Movie), null, null)).Returns(new Video { Id = alternate.Id });
        }
        else
        {
            context.Resolution.Setup(r => r.ResolveAlternateVersion(alternate.Path, typeof(Movie), null, null)).Throws(new IOException("resolution failed"));
        }

        await Assert.ThrowsAnyAsync<Exception>(() => context.Commands.UpdateItemAsync(primary, null, ItemUpdateType.MetadataEdit, TestContext.Current.CancellationToken));

        Assert.Equal(DateTime.UnixEpoch, primary.DateLastSaved);
        Assert.Empty(context.Cache.Registered);
        context.Persistence.Verify(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()), Times.Never);
        context.Metadata.Verify(m => m.SaveAsync(It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Fact]
    public async Task FailedUpdate_KeepsOwnedDatesAndPropagatedVersionMetadataUnpublished_ThenRetries()
    {
        var context = new Context();
        var primary = context.Movie();
        var alternate = context.Movie();
        primary.Overview = "new description";
        alternate.Overview = "old description";
        context.Lookup.Setup(l => l.GetItemById(alternate.Id)).Returns(alternate);
        context.Linked.Setup(l => l.GetLinkedChildrenIds(primary.Id, (int)Tesserafin.Controller.Entities.LinkedChildType.LocalAlternateVersion)).Returns([alternate.Id]);
        var notifications = 0;
        context.Events.ItemUpdated += (_, _) => notifications++;
        context.Persistence.Setup(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>())).Callback((IReadOnlyList<BaseItem> graph, CancellationToken _) =>
        {
            Assert.Equal(2, graph.Count);
            Assert.All(graph, item => Assert.Equal("new description", item.Overview));
            Assert.Equal("old description", alternate.Overview);
            Assert.Equal(DateTime.UnixEpoch, primary.DateLastSaved);
            Assert.Equal(DateTime.UnixEpoch, alternate.DateLastSaved);
            throw new InvalidOperationException("SQL write rejected");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Commands.UpdateItemAsync(primary, null, ItemUpdateType.MetadataEdit, TestContext.Current.CancellationToken));
        Assert.Equal("old description", alternate.Overview);
        Assert.Equal(DateTime.UnixEpoch, primary.DateLastSaved);
        Assert.Empty(context.Cache.Registered);
        Assert.Equal(0, notifications);

        context.Persistence.Setup(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()));
        await context.Commands.UpdateItemAsync(primary, null, ItemUpdateType.MetadataEdit, TestContext.Current.CancellationToken);
        Assert.Equal("new description", alternate.Overview);
        Assert.NotEqual(DateTime.UnixEpoch, primary.DateLastSaved);
        Assert.Equal(2, context.Cache.Registered.Count);
        Assert.Equal(2, notifications);
    }

    [Fact]
    public async Task Update_PreparesAnExistingPathVersionBeforeItsFirstLinkedRowExists()
    {
        var context = new Context();
        var primary = context.Movie();
        var alternate = context.Alternate(primary);
        primary.LocalAlternateVersions = [alternate.Path];
        primary.Overview = "new metadata";
        context.Lookup.Setup(l => l.GetItemById(alternate.Id)).Returns(alternate);

        await context.Commands.UpdateItemAsync(primary, null, ItemUpdateType.MetadataEdit, TestContext.Current.CancellationToken);

        Assert.Equal("new metadata", alternate.Overview);
        context.Persistence.Verify(p => p.SaveItems(It.Is<IReadOnlyList<BaseItem>>(graph => graph.Count == 2), It.IsAny<CancellationToken>()), Times.Once);
        context.Resolution.Verify(r => r.ResolveAlternateVersion(It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<Folder>(), It.IsAny<CollectionType?>()), Times.Never);
    }

    [Fact]
    public async Task BulkGuideRefresh_DoesNotIssueAPerProgramVersionQuery()
    {
        var context = new Context();

        await context.Commands.UpdateItemsAsync([new LiveTvProgram { Id = Guid.NewGuid(), Name = "guide program" }], null, ItemUpdateType.MetadataImport, TestContext.Current.CancellationToken);

        context.Linked.Verify(l => l.GetLinkedChildrenIds(It.IsAny<Guid>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public void CacheFailure_EvictsTheSavedGraph_WithoutNotification()
    {
        var context = new Context();
        var primary = context.Movie();
        var alternate = context.Alternate(primary);
        primary.LocalAlternateVersions = [alternate.Path];
        context.Cache.BeforeRegister = () => throw new InvalidOperationException("cache failure");
        var notifications = 0;
        context.Events.ItemAdded += (_, _) => notifications++;

        Assert.Throws<InvalidOperationException>(() => context.Commands.CreateItems([primary], null, TestContext.Current.CancellationToken));

        Assert.Equal(0, notifications);
        Assert.Equal(new[] { primary.Id, alternate.Id }, context.Cache.Removed);
        context.Persistence.Verify(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void DuplicateIdsAndCancelledAdmission_DoNotWrite()
    {
        var context = new Context();
        var primary = context.Movie();
        Assert.Throws<ArgumentException>(() => context.Commands.CreateItems([primary, primary], null, TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => context.Commands.CreateItems([primary], null, cancelled.Token));

        context.Persistence.Verify(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(context.Cache.Registered);
    }

    [Fact]
    public void Ownership_HasNoManagerOrProviderDependency_AndOldFacadesAreRemoved()
    {
        foreach (var owner in new[] { typeof(ItemWriteCommands), typeof(ItemVersionGraphPlanner), typeof(ItemResolutionService), typeof(LibraryItemOptions) })
        {
            var dependencies = owner.GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
            Assert.DoesNotContain(typeof(ILibraryManager), dependencies);
            Assert.DoesNotContain(typeof(Lazy<ILibraryManager>), dependencies);
            Assert.DoesNotContain(typeof(IProviderManager), dependencies);
            Assert.DoesNotContain(typeof(Lazy<IProviderManager>), dependencies);
        }

        foreach (var name in new[] { "CreateItem", "CreateItems", "UpdateItemAsync", "UpdateItemsAsync", "ReattachUserDataAsync", "RunMetadataSavers" })
        {
            Assert.Null(typeof(ILibraryManager).GetMethod(name));
            Assert.Null(typeof(Tesserafin.Server.Core.Library.LibraryManager).GetMethod(name));
        }

        Assert.Null(typeof(IProviderManager).GetMethod("SaveMetadataAsync", [typeof(BaseItem), typeof(ItemUpdateType)]));
    }

    private sealed class Context
    {
        public Context()
        {
            Linked.Setup(l => l.GetLinkedChildrenIds(It.IsAny<Guid>(), It.IsAny<int?>())).Returns([]);
            Metadata.Setup(m => m.SaveAsync(It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>>())).Returns(Task.CompletedTask);
            Commands = new ItemWriteCommands(new ItemVersionGraphPlanner(Resolution.Object, Mock.Of<ILibraryItemOptions>(), Lookup.Object), Persistence.Object, Cache, Lookup.Object, Linked.Object, Metadata.Object, Events, new ActiveRecordings(), NullLogger<ItemWriteCommands>.Instance);
        }

        public Mock<IItemResolutionService> Resolution { get; } = new();

        public Mock<IItemLookupService> Lookup { get; } = new();

        public Mock<ILinkedChildrenService> Linked { get; } = new();

        public Mock<IItemPersistenceService> Persistence { get; } = new();

        public Mock<IItemMetadataExporter> Metadata { get; } = new();

        public RecordingCache Cache { get; } = new();

        public ItemChangeEvents Events { get; } = new(NullLogger<ItemChangeEvents>.Instance);

        public ItemWriteCommands Commands { get; }

        public Movie Movie() => new() { Id = Guid.NewGuid(), Name = "write test", Path = "/media/" + Guid.NewGuid() + ".mkv", DateLastSaved = DateTime.UnixEpoch };

        public Movie Alternate(Movie primary)
        {
            var alternate = Movie();
            alternate.AdditionalParts = [alternate.Path + ".part2.mkv"];
            Resolution.Setup(r => r.GetNewItemId(alternate.Path, primary.GetType())).Returns(alternate.Id);
            Resolution.Setup(r => r.ResolveAlternateVersion(alternate.Path, primary.GetType(), null, null)).Returns(alternate);
            return alternate;
        }
    }

    private sealed class RecordingCache : IItemCacheStore
    {
        public List<BaseItem> Registered { get; } = [];

        public List<Guid> Removed { get; } = [];

        public Action? BeforeRegister { get; set; }

        public void Register(BaseItem item)
        {
            BeforeRegister?.Invoke();
            Registered.Add(item);
        }

        public void Remove(Guid id) => Removed.Add(id);

        public void RemoveRange(IEnumerable<Guid> ids) => Removed.AddRange(ids);
    }
}
""")

put("tests/Tesserafin.Providers.Tests/Manager/ItemMetadataExporterTests.cs","""using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Model.Configuration;
using Tesserafin.Providers.Manager;
using Xunit;

namespace Tesserafin.Providers.Tests.Manager;

public sealed class ItemMetadataExporterTests
{
    [Fact]
    public async Task SelectedNames_IgnoreCase_AndNonFileExportsLeaveTheMonitorDeferred()
    {
        var item = new Folder { Name = "metadata", Path = "/media/movie.mkv" };
        var chosen = Saver("chosen");
        var other = Saver("other");
        var monitor = new Lazy<ILibraryMonitor>(() => throw new InvalidOperationException("monitor must remain deferred"));
        var service = Service(monitor, "chosen", "other");
        service.AddParts([chosen.Object, other.Object]);

        await service.SaveAsync(item, ItemUpdateType.MetadataEdit, TestContext.Current.CancellationToken, ["CHOSEN"]);

        chosen.Verify(s => s.SaveAsync(item, TestContext.Current.CancellationToken), Times.Once);
        other.Verify(s => s.SaveAsync(It.IsAny<BaseItem>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(monitor.IsValueCreated);
        Assert.NotEqual(default, item.DateLastSaved);
    }

    [Fact]
    public async Task FailedFileExport_CompletesMonitoring_AndContinuesWithTheNextSaver()
    {
        var calls = new List<string>();
        var monitor = new Mock<ILibraryMonitor>();
        monitor.Setup(m => m.ReportFileSystemChangeBeginning("/media/item.nfo")).Callback(() => calls.Add("begin"));
        monitor.Setup(m => m.ReportFileSystemChangeComplete("/media/item.nfo", false)).Callback(() => calls.Add("complete"));
        var file = new Mock<IMetadataFileSaver>();
        file.SetupGet(s => s.Name).Returns("file");
        file.Setup(s => s.IsEnabledFor(It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>())).Returns(true);
        file.Setup(s => s.GetSavePath(It.IsAny<BaseItem>())).Returns("/media/item.nfo");
        file.Setup(s => s.SaveAsync(It.IsAny<BaseItem>(), It.IsAny<CancellationToken>())).Callback(() => calls.Add("file")).ThrowsAsync(new IOException("sidecar is locked"));
        var next = Saver("next");
        next.Setup(s => s.SaveAsync(It.IsAny<BaseItem>(), It.IsAny<CancellationToken>())).Callback(() => calls.Add("next")).Returns(Task.CompletedTask);
        var service = Service(new Lazy<ILibraryMonitor>(() => monitor.Object), "file", "next");
        service.AddParts([file.Object, next.Object]);

        await service.SaveAsync(new Folder { Name = "metadata" }, ItemUpdateType.MetadataEdit, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "begin", "file", "complete", "next" }, calls);
    }

    [Fact]
    public async Task Cancellation_StopsTheBatch_AndCompletesFileMonitoring()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var monitor = new Mock<ILibraryMonitor>();
        var file = new Mock<IMetadataFileSaver>();
        file.SetupGet(s => s.Name).Returns("file");
        file.Setup(s => s.IsEnabledFor(It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>())).Returns(true);
        file.Setup(s => s.GetSavePath(It.IsAny<BaseItem>())).Returns("/media/item.nfo");
        file.Setup(s => s.SaveAsync(It.IsAny<BaseItem>(), It.IsAny<CancellationToken>())).Returns((BaseItem _, CancellationToken token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(token);
        });
        var next = Saver("next");
        var service = Service(new Lazy<ILibraryMonitor>(() => monitor.Object), "file", "next");
        service.AddParts([file.Object, next.Object]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveAsync(new Folder { Name = "metadata" }, ItemUpdateType.MetadataEdit, cancellation.Token));

        monitor.Verify(m => m.ReportFileSystemChangeComplete("/media/item.nfo", false), Times.Once);
        next.Verify(s => s.SaveAsync(It.IsAny<BaseItem>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static ItemMetadataExporter Service(Lazy<ILibraryMonitor> monitor, params string[] enabled)
    {
        var configuration = new Mock<IServerConfigurationManager>();
        configuration.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        var options = new Mock<ILibraryItemOptions>();
        options.Setup(o => o.GetLibraryOptions(It.IsAny<BaseItem>())).Returns(new LibraryOptions { MetadataSavers = enabled });
        return new ItemMetadataExporter(configuration.Object, options.Object, monitor, NullLogger<ItemMetadataExporter>.Instance);
    }

    private static Mock<IMetadataSaver> Saver(string name)
    {
        var saver = new Mock<IMetadataSaver>();
        saver.SetupGet(s => s.Name).Returns(name);
        saver.Setup(s => s.IsEnabledFor(It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>())).Returns(true);
        saver.Setup(s => s.SaveAsync(It.IsAny<BaseItem>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return saver;
    }
}
""")

put("tests/Tesserafin.Server.Integration.Tests/ItemWriteCommandsEndToEndTests.cs","""using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Database.Implementations;
using Tesserafin.Model.Entities;
using Xunit;

namespace Tesserafin.Server.Integration.Tests;

public sealed class ItemWriteCommandsEndToEndTests(TesserafinApplicationFactory factory) : IClassFixture<TesserafinApplicationFactory>
{
    [Fact]
    public async Task VersionGraph_RollsBackFailedCreation_Retries_UpdatesAndPromotesMultipartAlternate()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        using var sources = new Sources();
        var writes = factory.Services.GetRequiredService<IItemWriteCommands>();
        var resolution = factory.Services.GetRequiredService<IItemResolutionService>();
        var lookup = factory.Services.GetRequiredService<IItemLookupService>();
        var repository = factory.Services.GetRequiredService<IItemRepository>();
        var linked = factory.Services.GetRequiredService<ILinkedChildrenService>();
        var changes = factory.Services.GetRequiredService<IItemChangeEvents>();
        var databases = factory.Services.GetRequiredService<IDbContextFactory<TesserafinDbContext>>();
        var primary = new Movie
        {
            Id = resolution.GetNewItemId(sources.Primary, typeof(Movie)),
            Name = "Write graph primary",
            Path = sources.Primary,
            Container = "mkv",
            VideoType = VideoType.VideoFile,
            IsInMixedFolder = true,
            LocalAlternateVersions = [sources.Alternate, sources.Alternate],
            DateLastSaved = DateTime.UnixEpoch
        };
        var alternateId = resolution.GetNewItemId(sources.Alternate, typeof(Movie));
        var added = new List<Guid>();
        var updated = new List<Guid>();
        changes.ItemAdded += Added;
        changes.ItemUpdated += Updated;
        try
        {
            int before;
            await using (var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken))
            {
                before = await database.BaseItems.CountAsync(TestContext.Current.CancellationToken);
                await database.Database.ExecuteSqlRawAsync($"CREATE TRIGGER fail_item_graph AFTER INSERT ON BaseItems WHEN (SELECT COUNT(*) FROM BaseItems) > {before + 1} BEGIN SELECT RAISE(ABORT, 'second graph row rejected'); END;", TestContext.Current.CancellationToken);
            }

            try
            {
                Assert.ThrowsAny<Exception>(() => writes.CreateItems([primary], null, TestContext.Current.CancellationToken));
                Assert.Null(lookup.GetItemById(primary.Id));
                Assert.Null(lookup.GetItemById(alternateId));
                Assert.Empty(added);
                await using var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken);
                Assert.Equal(before, await database.BaseItems.CountAsync(TestContext.Current.CancellationToken));
                Assert.False(await database.LinkedChildren.AnyAsync(row => row.ParentId.Equals(primary.Id), TestContext.Current.CancellationToken));
            }
            finally
            {
                await using var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken);
                await database.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_item_graph;", TestContext.Current.CancellationToken);
            }

            writes.CreateItems([primary], null, TestContext.Current.CancellationToken);
            Assert.Equal(new[] { primary.Id }, added);
            var alternate = Assert.IsType<Movie>(repository.RetrieveItem(alternateId));
            Assert.Equal(primary.Id, alternate.OwnerId);
            Assert.Equal(primary.Id, alternate.PrimaryVersionId);
            Assert.Equal(sources.SecondPart, Assert.Single(alternate.AdditionalParts));
            Assert.Equal(new[] { alternateId }, linked.GetLinkedChildrenIds(primary.Id, (int)Tesserafin.Controller.Entities.LinkedChildType.LocalAlternateVersion));

            primary.Overview = "One committed description for both versions";
            await writes.UpdateItemAsync(primary, null, ItemUpdateType.MetadataEdit, TestContext.Current.CancellationToken);
            Assert.Equal(primary.Overview, repository.RetrieveItem(alternateId).Overview);
            Assert.Equal(primary.Overview, repository.RetrieveItem(primary.Id).Overview);
            Assert.Equal(new[] { primary.Id, alternateId }, updated);
            Assert.NotEqual(DateTime.UnixEpoch, primary.DateLastSaved);

            await factory.Services.GetRequiredService<IItemDeletionCommands>().DeleteAsync(primary, new DeleteOptions { DeleteFileLocation = false }, TestContext.Current.CancellationToken);
            Assert.Null(lookup.GetItemById(primary.Id));
            var promoted = Assert.IsType<Movie>(repository.RetrieveItem(alternateId));
            Assert.Null(promoted.PrimaryVersionId);
            Assert.Equal(Guid.Empty, promoted.OwnerId);
            Assert.Equal(sources.SecondPart, Assert.Single(promoted.AdditionalParts));
            Assert.True(File.Exists(sources.Primary));
            Assert.True(File.Exists(sources.Alternate));
            Assert.True(File.Exists(sources.SecondPart));
        }
        finally
        {
            changes.ItemAdded -= Added;
            changes.ItemUpdated -= Updated;
        }

        void Added(object? sender, ItemChangeEventArgs args)
        {
            if (args.Item.Id.Equals(primary.Id) && lookup.GetItemById(alternateId) is Movie)
            {
                added.Add(args.Item.Id);
            }
        }

        void Updated(object? sender, ItemChangeEventArgs args)
        {
            if (args.Item.Id.Equals(primary.Id) || args.Item.Id.Equals(alternateId))
            {
                updated.Add(args.Item.Id);
            }
        }
    }

    private sealed class Sources : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("write-graph-host-").FullName;

        public Sources()
        {
            Primary = Path.Combine(_root, "Feature - 2160p.mkv");
            Alternate = Path.Combine(_root, "Feature - 1080p cd1.mkv");
            SecondPart = Path.Combine(_root, "Feature - 1080p cd2.mkv");
            foreach (var path in new[] { Primary, Alternate, SecondPart })
            {
                File.WriteAllBytes(path, [1, 3, 5, 7]);
            }
        }

        public string Primary { get; }

        public string Alternate { get; }

        public string SecondPart { get; }

        public void Dispose() => Directory.Delete(_root, true);
    }
}
""")

p=".github/workflows/ci-tests.yml";s=read(p)
line="          --require Tesserafin.Server.Integration.Tests.ItemDeletionCommandsEndToEndTests.ItemChanges_AreSharedAndFollowCatalogueAndCachePublication"
if line not in s: raise RuntimeError("Required integration gate not found")
s=s.replace(line,line+"\n          --require Tesserafin.Server.Integration.Tests.ItemWriteCommandsEndToEndTests.VersionGraph_RollsBackFailedCreation_Retries_UpdatesAndPromotesMultipartAlternate")
put(p,s)

p="ARCHITECTURE.md";s=read(p);point=s.index("- **Resolution.**")
s=s[:point]+"""- **Item graph writes.** \u0060IItemWriteCommands\u0060 owns general create/update, per-item version
  metadata propagation, cache publication and item notifications. The manager's five write
  methods and metadata-save helper are removed without forwarding members. Resolution now runs
  in \u0060IItemResolutionService\u0060 over an explicit read policy and lookup; its helpers no longer
  use entity-global lookup, filesystem or configuration services. \u0060ILibraryItemOptions\u0060 owns
  options and content-type lookup, and one internal identity policy serves creation/resolution.
  \u0060ItemVersionGraphPlanner\u0060 prepares typed local alternates, deduplicates paths and includes
  multipart files before persistence. Individual video updates include existing path versions
  even before their first linked row exists. Bulk refresh preserves its nonrecursive behavior,
  avoiding a version lookup per guide program.
- Updated save dates and propagated version metadata are staged on shallow item copies. One
  existing persistence transaction saves the graph before cache publication and notifications.
  A failed SQL write does not publish those staged fields; cache failure evicts the graph after
  the durable write and emits no notification. This does not undo mutations callers made before
  entering the command, deeply isolate mutable child objects, serialize every writer, or provide
  a filesystem/database transaction. Sidecar exports remain best effort apart from cancellation.
- \u0060IItemMetadataExporter\u0060 owns registered saver execution and eligibility, separately from provider
  refresh orchestration. General writes no longer implicitly localize/enrich images; explicit
  refresh/image-delivery paths own that work. Entity refresh helpers carry the write port as an
  argument; API, providers, Live TV, scans, migrations and user-view creation use explicit commands.
  The query-side \u0060IItemStore\u0060 remains a leaf and does not depend on the general command owner.
  Active manager query entry points remain while later read-side decomposition is pending.
""" + s[point:];put(p,s)
p="docs/playback-path-inventory.md";s=read(p)+"""

## 27. Item graph write ownership

General creation, update, version propagation and user-data reattachment now belong to
\u0060IItemWriteCommands\u0060. \u0060CreateItem(s)\u0060, \u0060UpdateItem(s)Async\u0060, \u0060ReattachUserDataAsync\u0060 and
\u0060RunMetadataSavers\u0060 are removed from the manager/interface without compatibility forwarders.
A read-only resolution service and options policy replace the writer's dependency on the manager.
Resolver arguments/helpers use explicit lookup/filesystem/configuration inputs. Deterministic
identity generation is shared by the item store, resolution service and remaining manager queries.

One planner prepares typed local versions and their multipart paths for both create and update.
Individual video updates stage propagated fields and dates on shallow copies, include existing
versions whose link has not yet been inserted, and persist the whole graph before cache/events.
Bulk refresh retains its nonrecursive version behavior. A save failure leaves staged dates and
alternate metadata unpublished; a post-save cache failure evicts graph entries and emits no event.
Caller mutations made before admission, shared mutable child objects, concurrent writers and
external sidecar files are outside that database transaction. No deep snapshot or crash-recovery
claim is made.

Saver registration/execution moves to \u0060IItemMetadataExporter\u0060; the provider manager no longer
exposes SaveMetadataAsync. File exports pair watcher suppression/restoration; cancellation
propagates before catalogue persistence. Image localization is no longer a hidden full-item-write
side effect: refresh and image delivery use the image lifecycle explicitly. No new entity service
static is introduced. HTTP/OpenAPI and SDK shapes remain unchanged; .NET removals/signature
changes are intentional. The new real-host graph rollback/retry/update/promotion test is required
by name in CI. Validation results are recorded on the implementation PR, not assumed here.
""";put(p,s)

# Final import order also covers newly generated test files.
changed=subprocess.check_output(["git","diff","--name-only"],text=True).splitlines()
changed+=subprocess.check_output(["git","ls-files","--others","--exclude-standard"],text=True).splitlines()
for path in sorted(set(changed)):
    if not path.endswith(".cs"): continue
    text=read(path);matches=list(re.finditer(r"(?m)^using [^\n]+;\n",text))
    if not matches: continue
    first,last=matches[0].start(),matches[-1].end()
    if all(not line.strip() or line.startswith("using ") for line in text[first:last].splitlines()):
        directives=sorted(set(m.group().rstrip("\n") for m in matches),key=lambda line:(not line.startswith("using System")," = " in line,line))
        put(path,text[:first]+"\n".join(directives)+"\n"+text[last:])

# Remove bootstrap-only tooling: the deliverable is the implementation, not a generator.
(root/"ci/materialize-item-writes.py").unlink()
(root/".github/workflows/materialize-item-writes.yml").unlink()
