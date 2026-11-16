# RFC v2 — Démêlage du cycle DI query / user-views / channel / live-tv

- **PR** : PR105 (design uniquement, aucun code de production)
- **Remplace** : PR99 (`docs/pr99-rfc-di-cycle-untangle.md`) — rejeté en revue externe (2026-07-13) pour cycles cachés
- **Amendé** : 2026-07-14 (PR105b, suite revue externe) — règle d'or reformulée en invariants vérifiables I1/I2 (§8), contrat `ItemAdded` tranché (§2), critère PR111 « −3 paramètres » remplacé (§9), PR106 scindé en PR106a/PR106b (§9)
- **Précède** : PR106a/PR106b–PR111 (application de la découpe)
- **Clos** : 2026-07-14 (PR111, `docs/pr111-di-closure-audit.md`)
- **HEAD au moment de la rédaction** : `c7ed2b36d8` (PR104)
- **Processus** : à partir de PR106a, chaque PR de cette séquence passe par une vraie branche Git + PR GitHub (le graphe DI central est touché ; revue par objet PR, plus de commit direct sur `master` pour cette tranche)

> État courant (2026-11-16) : le contrat de relais `ItemStore.ItemSaved` →
> `LibraryManager.ItemAdded` décrit ici est remplacé par la publication directe vers
> `IItemChangePublisher`, observée via `IItemChangeEvents`. Les anciens événements sont supprimés.
> Voir [l’inventaire, §26](playback-path-inventory.md#26-item-change-notification-ownership).
> L’invariant de construction sans dépendance vers les gestionnaires reste applicable.

Aucun code ici. Toutes les références fichier:ligne ci-dessous ont été vérifiées directement dans le code à HEAD, avec confirmation croisée `graphify path` pour chaque arête de la SCC (dans les deux sens).

---

## 0. Pourquoi PR99 a été rejeté, et ce que ce document fait différemment

La revue externe a rejeté PR99 pour **deux cycles cachés** que le RFC n'avait pas modélisés :

1. **`IUserRootFolderProvider` aliasé sur `LibraryManager`.** Le port existe (`Reefin.Controller/Library/IUserRootFolderProvider.cs`), mais son enregistrement DI est :
   ```csharp
   // Reefin.Server.Core/ApplicationHost.cs:568
   serviceCollection.AddSingleton<IUserRootFolderProvider>(sp => (IUserRootFolderProvider)sp.GetRequiredService<ILibraryManager>());
   ```
   Le commentaire XML du port le dit lui-même noir sur blanc (`IUserRootFolderProvider.cs:13-17`) : *« `LibraryManager` is the sole implementation … this port stays backed by `LibraryManager`. »* PR99 traitait ce port comme une feuille cycle-free (découpe (2), §3) sans vérifier son implémentation réelle. **Ce piège existe encore tel quel aujourd'hui** — vérifié ci-dessous, §1.
2. **Cycle caché via `IChannelManager`.** PR99 modélisait la branche channel comme *parallèle* au cycle à 2 nœuds (§1.1 du RFC PR99 : « Et une branche channel parallèle »), pas comme une fermeture du cycle. En réalité, `ChannelManager` référence `ILibraryManager` directement dans son constructeur, et `UserViewManager` référence `IChannelManager` — donc le cycle se referme *aussi* par ce chemin. PR99 ne l'a jamais tracé en tant que membre de la SCC.

**Ce que ce RFC fait différemment** :
- Il traite explicitement `ChannelManager` et `LiveTvManager` comme des **membres** de la SCC (pas des branches annexes) — voir §7.
- Pour chaque port proposé (PR106–111), il exige une **preuve** que l'implémentation n'est ni `LibraryManager` ni un alias dessus, en listant les dépendances transitives de l'implémentation jusqu'à ce qu'elles touchent uniquement des feuilles déjà cycle-free ou de nouvelles feuilles autonomes. C'est la **règle d'or** de ce RFC (§8).
- Il identifie un **troisième piège** implicite dans le code de user-views : le fallback statique `BaseItem.LibraryManager` (§3, §8.3), qui referme silencieusement un cycle même quand tous les ports DI explicites sont propres.

---

## 1. Axe 1 — Le propriétaire réel de `UserRootFolder`

### État des lieux

`LibraryManager` est **l'unique propriétaire réel** — construction, cache et (absence de) invalidation :

```csharp
// Reefin.Server.Core/Library/LibraryManager.cs:66
internal class LibraryManager : ILibraryManager, IItemLookupService, IUserRootFolderProvider
```

```csharp
// LibraryManager.cs:113
private volatile UserRootFolder? _userRootFolder;

// LibraryManager.cs:1133-1178 — GetUserRootFolder()
public Folder GetUserRootFolder()
{
    if (_userRootFolder is null)
    {
        lock (_userRootFolderSyncLock)          // double-checked locking
        {
            if (_userRootFolder is null)
            {
                // construit via _configurationManager.ApplicationPaths.DefaultUserViewsPath,
                // GetNewItemId, GetItemById, ResolvePath+DeepCopy si absent (L1141-1171)
                _userRootFolder = tmpItem;
            }
        }
    }
    return _userRootFolder;
}
```

**Lifecycle réel** : lazy, thread-safe (double-checked lock + `volatile`), **jamais invalidé** — aucune méthode `InvalidateUserRootFolder`/`ResetUserRootFolder`/`_userRootFolder = null` n'existe ailleurs dans le repo (`grep` vide sur ces trois motifs, hors tests). Le cache vit pour la durée de vie du singleton `LibraryManager`.

Le port `IUserRootFolderProvider` (`IUserRootFolderProvider.cs:19-26`, une seule méthode `Folder GetUserRootFolder()`) a été introduit en PR85b pour `ItemQueryScopeService`, mais **son enregistrement DI l'aliase sur `LibraryManager`** (`ApplicationHost.cs:568`, cité §0). Ce n'est pas une implémentation autonome : c'est un cast.

### Consommateurs actuels

| Consommateur | Comment |
| --- | --- |
| `ItemQueryScopeService` | `IUserRootFolderProvider _rootFolderProvider` (ctor `ItemQueryScopeService.cs:42/55`) — mais résout vers `LibraryManager` via l'alias |
| `UserViewManager` | `_libraryManager.GetUserRootFolder()` direct, **pas** via le port (`UserViewManager.cs:58`, `UserViewManager.cs:294`) |
| `LibraryManager` (interne) | 7 appels internes à `GetUserRootFolder()` : `LibraryManager.cs:1406,1537,2064,2606,2749,3023,3463` |

### État cible (PR107)

- Nouveau service **autonome**, ex. `UserRootFolderProvider : IUserRootFolderProvider`, propriétaire unique de la construction/cache/invalidation. `LibraryManager` **cesse d'implémenter** `IUserRootFolderProvider`.
- Dépendances du nouveau service : `IServerConfigurationManager` (chemin), `IFileSystem`, une capacité de résolution de chemin équivalente à `LibraryManager.ResolvePath` (méthode publique, `LibraryManager.cs:811-816`, mais non exposée sur un port indépendant de `ILibraryManager` aujourd'hui — à extraire ou déléguer), et le nouveau leaf item-store introduit en PR106 (§2, item id + création/cache) pour `GetNewItemId`/`GetItemById`/persistance.
- `LibraryManager` devient **consommateur** : injecte `IUserRootFolderProvider` (non aliasé) et délègue tous ses appels internes.
- Enregistrement DI cible : `services.AddSingleton<IUserRootFolderProvider, UserRootFolderProvider>();` (plus de factory lambda castant `ILibraryManager`).

**Preuve d'absence de cycle** : `UserRootFolderProvider` ne référencerait aucune interface implémentée par `LibraryManager`, `UserViewManager`, `ChannelManager` ou `LiveTvManager` — ses seules dépendances sont `IServerConfigurationManager`, `IFileSystem` et le leaf PR106 (lui-même prouvé cycle-free, §2).

---

## 2. Axe 2 — Création des vues named/shadow (`GetNamedView`/`GetShadowView`)

### État des lieux

Ces méthodes **vivent dans `LibraryManager`**, exposées via `ILibraryManager` (`Reefin.Controller/Library/ILibraryManager.cs:433,448,461,475,489` — 4 surcharges `GetNamedView` + 1 `GetShadowView`) et implémentées `LibraryManager.cs:2756-2980` :

- `GetNamedView(User, string, CollectionType?, string)` → `LibraryManager.cs:2756`
- `GetNamedView(string, CollectionType, string)` → `LibraryManager.cs:2765`
- `GetNamedView(User, string, Guid, CollectionType?, string)` → `LibraryManager.cs:2809`
- `GetShadowView(BaseItem, CollectionType?, string)` → `LibraryManager.cs:2874`
- `GetNamedView(string, Guid, CollectionType?, string, string)` → `LibraryManager.cs:2938`

**Ce qu'elles touchent réellement** (lu ligne par ligne, `LibraryManager.cs:2756-2980`) : `GetNewItemId`, `GetItemById`, `CreateItem(item, null)`, `item.UpdateToRepositoryAsync(...)`, `ProviderManager.QueueRefresh(...)` (= `_providerManagerFactory.Value`, `LibraryManager.cs:258`), `_configurationManager`, `_fileSystem`. **Aucune** de ces méthodes ne touche `IUserViewManager`, `IChannelManager` ou `ILiveTvManager` — elles sont pures item-CRUD.

Décomposition de ce que `CreateItem(item, null)` fait réellement dans ce contexte précis (`LibraryManager.cs:2243-2302`, `CreateItems`) : avec `parent == null` et `item` de type `UserView` (jamais `Video`), la branche « alternate local versions » (L2258-2287) ne s'exécute jamais. Il ne reste que :
```csharp
_persistenceService.SaveItems(allItems, cancellationToken);   // IItemPersistenceService — déjà un port injecté
foreach (var item in allItems) RegisterItem(item);             // -> _itemCacheStore.Register(item), IItemCacheStore — déjà un port injecté
```
Et `RegisterItem` (`LibraryManager.cs:341-346`) est un pur passe-plat vers `IItemCacheStore.Register`. `GetNewItemId` (`LibraryManager.cs:782-809`) est une fonction pure de `_configurationManager` + hash MD5 — zéro dépendance cachée.

**Conclusion clé** : pour le sous-ensemble d'usage réel (création de `UserView`, parent toujours `null`), `GetNamedView`/`GetShadowView` ne dépendent **que** de ports déjà injectables séparément : `IItemLookupService` (GetItemById), `IItemCacheStore` (Register), `IItemPersistenceService` (SaveItems), `IServerConfigurationManager`, `IFileSystem`, `IProviderManager` (QueueRefresh — voir mise en garde ci-dessous). Le seul chaînon manquant est **la génération d'id** (`GetNewItemId`) qui n'est exposée sur aucun port existant.

**Mise en garde vérifiée sur `IProviderManager` — corrige une erreur de premier jet de ce RFC.** `ProviderManager` (`Reefin.Providers/Manager/ProviderManager.cs:109-122`) injecte `ILibraryManager libraryManager` **directement dans son propre constructeur** (`:117`). C'est *exactement pourquoi* `LibraryManager` le tient en `Lazy<IProviderManager>` (`LibraryManager.cs:76/258`) plutôt qu'en champ direct — une injection directe des deux côtés (`LibraryManager → IProviderManager` et `IProviderManager → ILibraryManager`) romprait la construction DI. **`IProviderManager` n'est donc pas un leaf sûr à injecter non-Lazy dans `UserViewFactory`** si `LibraryManager` délègue ensuite `GetNamedView`/`GetShadowView` à `UserViewFactory` (ce qui recrée le chemin `LibraryManager → UserViewFactory → IProviderManager → ILibraryManager`). `ChannelManager` l'injecte non-Lazy (`ChannelManager.cs:53/80`) mais cela ne prouve rien ici : `ChannelManager` a de toute façon déjà `ILibraryManager` en direct dans son propre ctor (`:49/75`, membre de la SCC, §7) — l'ajout d'un second chemin vers `ILibraryManager` via `IProviderManager` ne change rien à son statut. **`UserViewFactory` doit donc injecter `Lazy<IProviderManager>`, pas `IProviderManager` direct** — corrigé dans le design cible ci-dessous et en §8/§9.

**Second point vérifié — l'événement `ItemAdded`.** `CreateItems` (`LibraryManager.cs:2249-2329`) déclenche, après `SaveItems`/`RegisterItem`, l'événement `ItemAdded` (`:2303-2328`) pour tout item avec `SourceType == SourceType.Library` — ce qui inclut a priori les `UserView` créées par `GetNamedView`/`GetShadowView` aujourd'hui. Cet événement a de vrais abonnés en production : `LibraryMonitor` (`Reefin.Server.Core/IO/LibraryMonitor.cs:118`) et `LibraryChangedNotifier` (`Reefin.Server.Core/EntryPoints/LibraryChangedNotifier.cs:80`), tous deux abonnés sur l'instance `ILibraryManager`. **Si `IItemStore`/`UserViewFactory` est une classe distincte de `LibraryManager`, elle ne peut pas déclencher cet événement sur l'instance `LibraryManager`** — ces abonnés ne seraient plus notifiés à la création d'une vue named/shadow, un changement de comportement réel, pas seulement structurel. Traité comme risque explicite en §10 plutôt que résolu ici ; PR106 doit trancher (répliquer l'événement sur le nouveau port, ou documenter/tester le changement de comportement comme accepté).

### Qui appelle `GetNamedView`/`GetShadowView` aujourd'hui

| Appelant | Ligne | Cible |
| --- | --- | --- |
| `UserViewManager.GetUserViews` | `UserViewManager.cs:93` | `_libraryManager.GetNamedView(user, folder.Name, folder.Id, folderViewType, null)` |
| `UserViewManager.GetUserViews` | `UserViewManager.cs:131` | `_libraryManager.GetNamedView(name, CollectionType.folders, string.Empty)` |
| `UserViewManager.GetUserSubViewWithName` | `UserViewManager.cs:181` | `_libraryManager.GetNamedView(name, parentId, type, sortName, uniqueId)` |
| `UserViewManager.GetUserView` (folders privé) | `UserViewManager.cs:210` | `_libraryManager.GetNamedView(user, name, viewType, sortName)` |
| `UserViewManager.GetUserView` (public, shadow) | `UserViewManager.cs:215` | `_libraryManager.GetShadowView(parent, viewType, sortName)` |
| `LiveTvManager.GetInternalLiveTvFolder` | `src/Reefin.LiveTv/LiveTvManager.cs:1266` | `_libraryManager.GetNamedView(name, CollectionType.livetv, name)` |

Donc : **tous** les appelants passent par `ILibraryManager`, y compris `LiveTvManager` — ce qui referme un second chemin de cycle (§7).

### Implémentations vérifiées des ports d'infrastructure sous-jacents (pas des alias)

Contrairement à `IUserRootFolderProvider` (§0/§1, aliasé sur `LibraryManager` via une factory lambda), les trois ports dont `IItemStore` dépendrait sont bien portés par des **classes autonomes**, vérifié dans `ApplicationHost.cs` et leurs fichiers d'implémentation :

- `IItemLookupService` **et** `IItemCacheStore` sont tous deux résolus vers la **même** classe concrète autonome `ItemLookupService` (`internal sealed class ItemLookupService : IItemLookupService, IItemCacheStore`, fichier `Reefin.Server.Core/Library/ItemLookupService.cs:34`) — enregistrement `ApplicationHost.cs:564-565` (`sp.GetRequiredService<ItemLookupService>()` pour les deux interfaces, pas de cast vers `ILibraryManager`). Ctor : `IItemRepository`, `IServerConfigurationManager` (`ItemLookupService.cs:44`) — zéro référence à `LibraryManager`/`UserViewManager`/`ChannelManager`/`LiveTvManager`.
- `IItemPersistenceService` est résolu vers `ItemPersistenceService` (`ApplicationHost.cs:538`). Ctor : `IDbContextFactory<ReefinDbContext>`, `IServerApplicationHost`, `ILogger` (`Reefin.Database.../Persistence/ItemPersistenceService.cs:39-42`) — zéro référence à la SCC.

Ces trois ports sont donc des leaves **prouvés**, pas supposés.

### État cible (PR106)

- Nouveau leaf **`IUserViewFactory`** — `GetNamedView`(les 4 surcharges) + `GetShadowView` — implémenté par une nouvelle classe autonome, ex. `UserViewFactory`, dépendant de :
  - `IItemLookupService` (lookup)
  - un nouveau leaf minimal **`IItemStore`** (ou extension d'un port existant) exposant `GetNewItemId(string, Type)` + `CreateItem(BaseItem, BaseItem?)`/`RegisterItem(BaseItem)` — composé lui-même de `IItemPersistenceService` + `IItemCacheStore` + `IServerConfigurationManager` (voir décomposition ci-dessus et vérification ci-dessus). **Ce leaf n'existe pas encore** — c'est un prérequis explicite de PR106, pas seulement un renommage.
  - `IServerConfigurationManager`, `IFileSystem`
  - **`Lazy<IProviderManager>`** — **pas** d'injection directe (voir mise en garde ci-dessus : `ProviderManager` référence `ILibraryManager` dans son propre ctor, `ProviderManager.cs:117` ; une injection directe recréerait un cycle `LibraryManager → UserViewFactory → IProviderManager → ILibraryManager` une fois `LibraryManager` migré pour déléguer à `UserViewFactory`).
- `LibraryManager` garde `GetNamedView`/`GetShadowView` sur `ILibraryManager` (compat API) mais **délègue** à `IUserViewFactory`.
- `UserViewManager` et `LiveTvManager` migrent leurs appels vers `IUserViewFactory` directement — **plus de passage par `ILibraryManager`/`_libraryManager.GetNamedView`**.
- **Contrat `ItemAdded` — tranché en PR105b (2026-07-14)** : `IItemStore` expose un événement métier minimal (`ItemSaved`, déclenché quand un item est persisté **et** enregistré au cache, avec le même filtre `SourceType == SourceType.Library` que `CreateItems` aujourd'hui). `LibraryManager` s'y abonne et **relaie** vers son événement historique `ItemAdded`, tant que les abonnés legacy (`LibraryMonitor`, `LibraryChangedNotifier`) existent. Règle d'unicité : une création d'item donnée déclenche `ItemAdded` **exactement une fois** — soit par le chemin historique `CreateItems`, soit par le relais `ItemSaved`, jamais les deux ; si `CreateItems` migre un jour en interne vers `IItemStore`, son déclenchement direct doit être supprimé au même commit (le relais devient l'unique source). Test de contrat exigé en PR106a : une vue créée via le nouveau chemin déclenche exactement une notification `ItemAdded` observable ; une vue créée via le chemin historique aussi (pas de doublon, pas de perte silencieuse).

**Preuve d'absence de cycle** : `IItemStore` (le prérequis) dépend de `IItemPersistenceService`/`IItemCacheStore`/`IServerConfigurationManager` — aucun d'eux n'implémente ni ne référence `LibraryManager`, `UserViewManager`, `ChannelManager` ou `LiveTvManager`, **vérifié au niveau de leurs classes concrètes** (`ItemLookupService`, `ItemPersistenceService`), pas seulement supposé au niveau de l'interface. `IUserViewFactory` lui-même n'a donc aucune arête vers les 4 membres de la SCC, à condition que `IProviderManager` y soit tenu en `Lazy<T>` comme dans `LibraryManager`.

---

## 3. Axe 3 — Probes playlist/boxset

### État des lieux

Dans `UserViewManager.GetUserViews` (`UserViewManager.cs:71-89`) :

```csharp
// Playlist and BoxSet libraries require special handling because the folder only references linked items
if (folderViewType == CollectionType.playlists || folderViewType == CollectionType.boxsets)
{
    var items = folder.GetItemList(
        new InternalItemsQuery(user) { ParentId = folder.ParentId },
        _channelManager, _collectionManager, this /* IUserViewManager */, _tvSeriesManager, _itemSortService);

    if (!items.Any(item => item.IsVisible(user))) continue;
}
```

`Folder.GetItemList(...)` (`Reefin.Controller/Entities/Folder.cs:1043-1046`) est **marqué `[Obsolete]`** sur la surcharge à 5 paramètres (`Folder.cs:1037`, message : *« Application code should use IItemQueryService instead »*) mais la surcharge à 6 paramètres (avec `IItemSortService`, celle réellement appelée ici) ne porte pas l'attribut. Sous le capot, `GetItemListCore` (`Folder.cs:1065-1074`) appelle, pour les requêtes avec `ItemIds` non vide, `LibraryManager.GetItemList(query)` — où `LibraryManager` ici est **le static `BaseItem.LibraryManager`** (`Reefin.Controller/Entities/BaseItem.cs:477`, `public static ILibraryManager LibraryManager { get; set; }`), pas un champ d'instance.

**Troisième piège identifié** : même après avoir nettoyé tous les ports DI explicites, ce chemin referme un cycle vers `LibraryManager` via ce static — invisible dans les graphes de constructeur, invisible dans les enregistrements DI. Le static `BaseItem.LibraryManager` est un mécanisme accepté et suivi séparément (chantier « statics », `docs/major-rewrite-plan-v13.md` ligne ~909 et ~1487 — explicitement hors périmètre d'élimination pour ce RFC). Ce RFC ne propose **pas** de l'éliminer, mais pose l'invariant suivant : **le nouveau code introduit par PR106-111 ne doit jamais appeler `Folder.GetItems`/`Folder.GetItemList`** — le probe playlist/boxset doit être réécrit contre `IItemQueryService`/le futur `IUserViewCatalog` (PR109), pas contre l'API `BaseItem` statique.

`this` (l'instance `IUserViewManager`) est aussi passée en paramètre à `GetItemList` — ce paramètre est utilisé plus loin dans `GetItemsInternal` pour des besoins de résolution de sous-vues ; dans l'état cible, ce paramètre disparaît avec le remplacement de l'appel par `IItemQueryService`.

### État cible (PR109)

- Le probe playlist/boxset est réécrit contre `IItemQueryService` (le service query déjà cycle-free vis-à-vis de `LibraryManager` — il ne referme pas la SCC, §7) plutôt que `Folder.GetItemList`.
- Invariant : zéro nouvel appel à `Folder.GetItems`/`GetItemList` dans le code introduit par PR106-111.

---

## 4. Axe 4 — Catalogue brut des chaînes (channels)

### État des lieux

`IChannelManager` (`Reefin.Controller/Channels/IChannelManager.cs:16-98`) expose 11 méthodes — features, delete, DTOs, media sources, latest items — un port large mêlant catalogue brut et DTO/mutation.

`ChannelManager` (`src/Reefin.LiveTv/Channels/ChannelManager.cs:44-90`) :
```csharp
public class ChannelManager : IChannelManager, IDisposable
{
    // ctor L72-90
    IUserManager userManager, IDtoService dtoService, ILibraryManager libraryManager,
    ILogger<ChannelManager> logger, IServerConfigurationManager config, IFileSystem fileSystem,
    IUserDataManager userDataManager, IProviderManager providerManager, IMemoryCache memoryCache,
    IEnumerable<IChannel> channels
}
```
`ILibraryManager` est injecté directement (**pas** Lazy) — c'est l'arête qui referme la SCC côté channel (confirmé `graphify path "ChannelManager" "ILibraryManager"` → 1 hop direct, `--references-->`).

Ce que `UserViewManager` utilise réellement de `IChannelManager` : **une seule méthode**, `GetChannelsInternalAsync(ChannelQuery)` (`UserViewManager.cs:136-139`), qui retourne `QueryResult<Channel>` (entités brutes, pas des DTOs).

Tracé de `GetChannelsInternalAsync` (`ChannelManager.cs:153-179`, `GetAllChannelEntitiesAsync` `ChannelManager.cs:321-327`) :
- `_userManager.GetUserById(...)` (optionnel, filtrage)
- `GetAllChannels()` → énumère `IEnumerable<IChannel>` injecté (plugins), pas de dépendance service
- `GetChannel(id)` → `ChannelManager.cs:526-528` : `_libraryManager.GetItemById(id) as Channel` — couvert par `IItemLookupService.GetItemById<T>`
- `GetInternalChannelId(name)` → `ChannelManager.cs:584-588` : `_libraryManager.GetNewItemId(...)` — couvert par le leaf `IItemStore` introduit en PR106 (§2)

**Aucun appel à `IDtoService` dans ce chemin** — `IDtoService` n'intervient que dans `GetChannelsAsync` (la variante DTO, non utilisée par user-views).

### État cible (PR108)

- Nouveau leaf **`IChannelCatalog`** — une seule méthode utile à user-views, `Task<QueryResult<Channel>> GetChannelsAsync(ChannelQuery)` (nom à trancher en implémentation ; garder le nom `GetChannelsInternalAsync` ou le simplifier), implémenté par une **nouvelle classe autonome** (ex. `ChannelCatalog`), **pas** `ChannelManager`.
- Dépendances de `ChannelCatalog` : `IEnumerable<IChannel>` (plugins, injection directe comme `ChannelManager` le fait déjà), `IItemLookupService`, le leaf `IItemStore` (PR106), `IUserManager` (filtrage optionnel). **Aucune dépendance à `IDtoService` ni `ILibraryManager`.**
- `ChannelManager` garde `IChannelManager` complet pour ses propres consommateurs (API DTO, refresh, delete) mais peut déléguer sa portion catalogue à `ChannelCatalog` en interne si souhaité (non requis pour la sortie de PR108).
- `UserViewManager` migre son unique appel vers `IChannelCatalog`.
- **Amendement PR108 (découvert en implémentation)** : `ChannelManager.GetAllChannelEntitiesAsync` a un fallback qui **crée** les items de chaîne manquants et appelle `item.RefreshMetadata(...)` → static `BaseItem.ProviderManager` → SCC. `ChannelCatalog` est donc **lookup-only** : une chaîne plugin pas encore matérialisée est omise au lieu d'être créée à la volée — gap cold-start uniquement (`RefreshChannelsScheduledTask` matérialise toutes les chaînes en régime établi), documenté dans les remarks. De plus, `ChannelQuery.IsFavorite`/`RefreshLatestChannelItems` (hors du sous-ensemble exercé par user-views, qui ne construit que `{ UserId }`) jettent `NotSupportedException` plutôt que d'ajouter `IUserDataManager` ou de se comporter silencieusement autrement.

**Preuve d'absence de cycle** : `ChannelCatalog` ne référence ni `ILibraryManager` ni `IChannelManager` ni `IUserViewManager` ni `ILiveTvManager`.

---

## 5. Axe 5 — Présence Live TV

### État des lieux

`UserViewManager.GetUserViews` (`UserViewManager.cs:145-148`) :
```csharp
if (_liveTvManager.GetEnabledUsers().Select(i => i.Id).Contains(user.Id))
{
    list.Add(_liveTvManager.GetInternalLiveTvFolder(CancellationToken.None));
}
```

`LiveTvManager` (`src/Reefin.LiveTv/LiveTvManager.cs:37-74`) — ctor injecte **`ILibraryManager` directement** (L44/57) et `IChannelManager` (L46/59), `IDtoService` (L42/55) :
```csharp
public LiveTvManager(IServerConfigurationManager config, ILogger<LiveTvManager> logger,
    IUserDataManager userDataManager, IDtoService dtoService, IUserManager userManager,
    ILibraryManager libraryManager, ILocalizationManager localization, IChannelManager channelManager,
    IRecordingsManager recordingsManager, LiveTvDtoService liveTvDtoService, IEnumerable<ILiveTvService> services)
```

- `GetEnabledUsers()` (`LiveTvManager.cs:1226-1230`) : `_userManager.GetUsers().Where(IsLiveTvEnabled)` — pas de dépendance cyclique.
- `GetInternalLiveTvFolder(...)` (`LiveTvManager.cs:1263-1267`) :
  ```csharp
  public Folder GetInternalLiveTvFolder(CancellationToken cancellationToken)
  {
      var name = _localization.GetLocalizedString("HeaderLiveTV");
      return _libraryManager.GetNamedView(name, CollectionType.livetv, name);
  }
  ```
  **Dépend directement de `ILibraryManager.GetNamedView`** — le même point d'entrée que l'axe 2. C'est le chemin de fermeture de cycle documenté §0/§7 que PR99 n'avait pas modélisé.

### État cible (PR108, port Live TV équivalent à `IChannelCatalog`)

- Nouveau leaf **`ILiveTvPresenceProvider`** (ou nom équivalent) — deux méthodes : `IEnumerable<User> GetEnabledUsers()` + `Folder GetLiveTvFolder(CancellationToken)`.
- Implémenté par une **nouvelle classe autonome** (ex. `LiveTvPresenceProvider`), **pas** `LiveTvManager` (qui reste chargé d'`ILibraryManager`/`IChannelManager`/`IDtoService`).
- Dépendances : `IUserManager` (GetEnabledUsers), `ILocalizationManager` (libellé), et **`IUserViewFactory`** (PR106, §2) pour `GetLiveTvFolder` — **pas** `_libraryManager.GetNamedView`.
- **Amendement PR108 (découvert en implémentation)** : reproduire fidèlement `LiveTvManager.IsLiveTvEnabled` exige deux dépendances supplémentaires non prévues par la liste ci-dessus — `IServerConfigurationManager` (options tuner) et le **nombre** de `ILiveTvService` enregistrés. Une injection directe `IEnumerable<ILiveTvService>` aurait violé **I1** : le `DefaultLiveTvService` in-tree prend `ILibraryManager` directement dans son ctor (chemin eager `LiveTvPresenceProvider → ILiveTvService[] → DefaultLiveTvService → ILibraryManager`), arête assertée par un test DiWiring dédié. Mitigation retenue : **`Lazy<IReadOnlyList<ILiveTvService>>`** (exclu du graphe eager, même motif que `Lazy<IProviderManager>`). **Troisième exception runtime assumée (bénie en PR108, à réévaluer à la clôture PR111)** : `.Value` est évalué à chaque `GetEnabledUsers()` — y compris depuis le chemin de listing `GetUserViews` —, ce qui matérialise les services (dont `DefaultLiveTvService` chargé d'`ILibraryManager`) au premier appel. Comportement runtime identique au legacy (`LiveTvManager` tient ces services en direct et est lui-même construit eager) ; l'usage est count-only. Alternative si réévaluation négative : port étroit « service count ».

**Preuve d'absence de cycle** : `LiveTvPresenceProvider` dépend de `IUserManager`, `ILocalizationManager`, `IUserViewFactory`. `IUserViewFactory` (§2) ne dépend que du leaf `IItemStore` + `IItemLookupService` + infra — chaîne transitive complète : `LiveTvPresenceProvider → IUserViewFactory → IItemStore/IItemLookupService → IItemPersistenceService/IItemCacheStore/IServerConfigurationManager/IFileSystem`. Aucun maillon ne touche `LibraryManager`, `UserViewManager`, `ChannelManager` ou `LiveTvManager`.

---

## 6. Axe 6 — Tri et préférences utilisateur

### État des lieux

Le tri final dans `GetUserViews` (`UserViewManager.cs:151-174`) :
```csharp
if (!query.IncludeHidden)
{
    list = list.Where(i => !user.GetPreferenceValues<Guid>(PreferenceKind.MyMediaExcludes).Contains(i.Id)).ToList();
}
var sorted = _itemSortService.Sort(list, user, [ItemSortBy.SortName], SortOrder.Ascending).ToList();
var orders = user.GetPreferenceValues<Guid>(PreferenceKind.OrderedViews);
return list.OrderBy(i => /* index dans orders */).ThenBy(sorted.IndexOf).ThenBy(i => i.SortName).ToArray();
```

`GetPreferenceValues<T>` et `IsFolderGrouped` (utilisé plus haut, `UserViewManager.cs:97`) sont des **méthodes d'extension sur l'entité `User`** — `Reefin.Data/UserEntityExtensions.cs:70` (`GetPreferenceValues`) et `:160` (`IsFolderGrouped`). **Aucune dépendance à un service injecté** : les préférences (`PreferenceKind.OrderedViews`, `MyMediaExcludes`) vivent directement sur l'entité `User` chargée depuis la base, pas dans `IDisplayPreferencesManager` ni `UserConfiguration`. Le tri lui-même passe par `IItemSortService`.

`IItemSortService` (implémentation `Reefin.Server.Core/Sorting/ItemSortService.cs:16-30`) : ctor `IUserManager` + `IUserDataManager` uniquement — **leaf déjà cycle-free**, confirmé (aucune référence à `LibraryManager`/`UserViewManager`/`ChannelManager`/`LiveTvManager`).

### État cible

**Aucun changement structurel requis sur cet axe.** Le tri/préférences ne participe à aucun cycle : `IItemSortService` est un leaf, et l'accès aux préférences est un accès direct à l'entité `User`, sans DI. Le futur `IUserViewCatalog` (PR109) consomme `IItemSortService` tel quel et lit les préférences sur `User` tel quel — pas de nouveau port nécessaire ici.

---

## 7. La SCC réelle (revue et corrigée par rapport à PR99)

### 7.1 Membres

PR99 modélisait une SCC à **2 nœuds** (`LibraryManager ↔ UserViewManager`) avec channel comme « branche parallèle ». La SCC réelle a **4 membres** :

```
LibraryManager ──Lazy<IUserViewManager>──▶ UserViewManager
UserViewManager ──ILibraryManager──▶ LibraryManager
UserViewManager ──IChannelManager──▶ ChannelManager
ChannelManager ──ILibraryManager──▶ LibraryManager
UserViewManager ──ILiveTvManager──▶ LiveTvManager
LiveTvManager ──ILibraryManager──▶ LibraryManager
```

Deux boucles fermées distinctes passent par `LibraryManager ↔ UserViewManager` puis ressortent via `ChannelManager` et `LiveTvManager` respectivement — **c'est exactement le « cycle caché via IChannelManager » cité par la revue externe comme motif de rejet de PR99**, plus un troisième chemin équivalent via `LiveTvManager` que PR99 n'avait pas non plus identifié.

### 7.2 Preuves (constructeur + `graphify path` bidirectionnel)

| Arête | Preuve fichier:ligne | `graphify path` |
| --- | --- | --- |
| `LibraryManager → Lazy<IUserViewManager>` | `LibraryManager.cs:77` (champ), `:160` (ctor param), `:260` (propriété `=> _userViewManagerFactory.Value`), appel `:2002` | `LibraryManager --implements--> ILibraryManager <--references-- UserViewManager` |
| `UserViewManager → ILibraryManager` | ctor `UserViewManager.cs:32/42`; appels `GetUserRootFolder` L58,294, `GetNamedView`/`GetShadowView` L93,131,181,210,215 | `UserViewManager --implements--> IUserViewManager <--references-- LibraryManager` (sens inverse) |
| `UserViewManager → IChannelManager` | ctor `UserViewManager.cs:35/42`; appel `GetChannelsInternalAsync` L136-139 | `UserViewManager --references--> ILibraryManager <--references-- ChannelManager` (chemin le plus court trouvé passe par `ILibraryManager`, confirme l'arête `ChannelManager → ILibraryManager` en aval) |
| `ChannelManager → ILibraryManager` | ctor `ChannelManager.cs:49/75`; ~15 appels `_libraryManager.*` (`GetItemById` L103,112,121,442,522,528,699,742; `CreateItem` L490; `GetNewItemId` L588; `GetItemIds` L534,736; `GetItemsResult` L655,758; `DeleteItem` L745) | `ChannelManager --references--> ILibraryManager` (1 hop direct) |
| `UserViewManager → ILiveTvManager` | ctor `UserViewManager.cs:36/42`; appels `GetEnabledUsers`/`GetInternalLiveTvFolder` L145,147 | `UserViewManager --references--> ILibraryManager <--references-- LiveTvManager` |
| `LiveTvManager → ILibraryManager` | ctor `LiveTvManager.cs:44/57`; appel `GetNamedView` L1266 | (confirmé par grep constructeur — chemin `graphify path` le plus court passe par `IUserDataManager`, non pertinent ; l'arête directe ctor est la preuve retenue) |

### 7.3 Consommateurs adjacents à la SCC (pas membres)

`ItemQueryScopeService` et `ItemQueryService` **dépendent de** `IUserViewManager` (et, pour le premier, de `IUserRootFolderProvider` aliasé) mais **ne sont référencés par aucun des 4 membres de la SCC** — vérifié par grep de `IItemQueryService`/`IItemQueryScopeService` dans `LibraryManager.cs`, `UserViewManager.cs`, `ChannelManager.cs`, `LiveTvManager.cs` : zéro occurrence. Ce sont des **feuilles entrantes** dans la SCC, pas des membres — la distinction que PR99 faisait déjà correctement pour `ItemQueryScopeService` reste valide, mais s'étend maintenant à la compréhension de ce qui referme réellement le cycle en aval.

| Consommateur | Arête entrante | Ligne |
| --- | --- | --- |
| `ItemQueryScopeService` | `IUserViewManager` | ctor `ItemQueryScopeService.cs:40/53`; appel `GetUserViews` `:128` |
| `ItemQueryScopeService` | `IUserRootFolderProvider` (alias `LibraryManager`) | ctor `ItemQueryScopeService.cs:42/55` |
| `ItemQueryService` | `IUserViewManager` | ctor (fichier `ItemQueryService.cs:38`) |

### 7.4 `Lazy<T>` recensés

`LibraryManager.cs` — 4 champs `Lazy<T>` au total :
- `Lazy<ILibraryMonitor>` (`:75`)
- `Lazy<IProviderManager>` (`:76`)
- `Lazy<IUserViewManager>` (`:77`) — **celui qui casse la SCC, cible de suppression PR110**
- `Lazy<IExternalDataManager>` (`:99`)

Aucun `Lazy<T>` dans `UserViewManager.cs`, `ItemQueryScopeService.cs`, `ChannelManager.cs`, `LiveTvManager.cs`.

### 7.5 Constructeur `LibraryManager` — référence pour PR111

30 paramètres (`LibraryManager.cs:150-180`), dont 4 `Lazy<T>` (ci-dessus). Liste complète : `appHost, loggerFactory, taskManager, userManager, configurationManager, userDataManager, libraryMonitorFactory(Lazy), fileSystem, providerManagerFactory(Lazy), userViewManagerFactory(Lazy), mediaEncoder, itemRepository, persistenceService, nextUpService, countService, linkedChildrenService, itemSortService, itemNamingService, itemPeopleService, imageProcessor, namingOptions, directoryService, peopleRepository, pathManager, dotIgnoreIgnoreRule, mediaStreamLanguageService, externalDataManagerFactory(Lazy), itemLookupService, itemCacheStore, itemAccessService`.

**Note pour PR111 (critère reformulé en PR105b)** : supprimer `Lazy<IUserViewManager>` (PR110) retire **1** paramètre, pas 3 — la délégation `GetNamedView`/`GetShadowView` (PR106b) et `IUserRootFolderProvider` (PR107) remplacent de la logique interne par des dépendances injectées (net neutre ou positif en paramètres). Le critère « −3 paramètres » du plan v13 est donc **abandonné** : il n'était garanti que pour 1 paramètre et incitait à inventer deux extractions marginales pour atteindre un chiffre. Le nouveau critère de sortie PR111 (§9) ne comporte plus d'objectif chiffré : le nombre de paramètres est **mesuré et expliqué** (avant/après, chaque écart justifié), pas ciblé.

---

## 8. Règle d'or et application port par port

**Règle d'or — reformulée en PR105b.** L'énoncé initial (« aucune dépendance transitive vers `LibraryManager` ») était **contradictoire** avec le design lui-même : `UserViewFactory` reçoit `Lazy<IProviderManager>`, et `ProviderManager` dépend directement d'`ILibraryManager` (`ProviderManager.cs:117`). Le `Lazy<T>` casse le **cycle de construction**, pas la **dépendance transitive d'exécution** — l'invariant tel qu'énoncé était donc invérifiable, et PR106 aurait pu satisfaire le graphe de construction tout en réintroduisant une dépendance cachée au runtime. Il est remplacé par deux invariants distincts, chacun vérifiable :

- **I1 — graphe de construction (eager)** : aucun nouveau port PR106–111 n'a, dans son graphe de dépendances de constructeur (injections directes suivies transitivement, `Lazy<T>` **exclus** du parcours), de chemin vers `LibraryManager`, `UserViewManager`, `ChannelManager` ou `LiveTvManager` — ni via un alias DI (le piège `IUserRootFolderProvider` actuel), ni via une factory castant un autre service. Vérification : inspection des ctors des implémentations concrètes + `graphify path` + test architectural DiWiring (PR106b).
- **I2 — chemin d'exécution de la création de vue** : entre l'entrée dans `GetNamedView`/`GetShadowView` et le retour de l'item persisté+enregistré, aucun membre de la SCC n'est invoqué, à **deux exceptions assumées près**, toutes deux préexistantes à l'identique dans `LibraryManager` (donc pas des régressions) : (1) `Lazy<IProviderManager>.Value` pour `QueueRefresh`, évaluée **après** persistance+enregistrement (déclenchement de refresh post-création) — vérifié empiriquement et asserté par test en PR106b : ordre observé save → register → `.Value`, et `.Value` jamais évalué pour une vue existante déjà rafraîchie (si `.Value` s'était avéré évalué avant la persistance, le design aurait dû être revu, pas l'invariant) ; (2) *(découvert en PR106b)* `item.UpdateToRepositoryAsync(...)` sur le chemin new-item de la surcharge `GetNamedView(string, CollectionType, string)` (la seule sans `parentId`) lit en interne le static `BaseItem.LibraryManager` (`BaseItem.cs:2370`) et appelle `UpdateItemAsync` dessus — arête runtime réelle vers la SCC via le static §3/§10.2, hors du graphe de construction (I1 intact), reproduite verbatim car préexistante ; à réévaluer à la clôture I2 de PR111 (l'éliminer relève du chantier statics, pas de ce RFC).
- Le fallback statique `BaseItem.LibraryManager` reste couvert par l'invariant §3 : zéro appel `Folder.GetItems`/`GetItemList` dans le code introduit par PR106–111.

**Réévaluation à la clôture (PR111)** : les 4 exceptions runtime listées dans ce RFC et découvertes en implémentation (PR106b : `Lazy<IProviderManager>.Value` post-persistance et le static `UpdateToRepositoryAsync` ; PR108 : `Lazy<IReadOnlyList<ILiveTvService>>.Value` ; PR109 : `Lazy<IItemQueryService>.Value`) sont statuées en détail — chemin retracé, statut, justification, chantier futur nommé le cas échéant — dans `docs/pr111-di-closure-audit.md` (« Réévaluation des 4 exceptions runtime bénies »). Toutes les 4 sont conservées ; l'exception 2 (static `UpdateToRepositoryAsync`) s'avère avoir deux sites d'appel dans `UserViewFactory`, pas un seul comme décrit ci-dessus. **DEBT-SERVER-1 (2026-10-18)** lève l'exception 4 : le probe passe par le port étroit `IVisibleContentProbe` (`IItemLookupService` + `IItemRepository`), `Lazy<IItemQueryService>` n'est plus enregistré.

Pour chaque port, ce RFC répond à trois questions : (a) qui l'implémente (classe nommée, jamais un alias/factory castant un autre service), (b) quelles sont ses dépendances directes, (c) est-ce que l'une d'elles atteint la SCC au sens **I1** (eager) ou **I2** (pendant la création de vue) ?

| Port | Implémentation | Dépendances directes | I1/I2 respectés ? |
| --- | --- | --- | --- |
| `IItemStore` (PR106a) | `ItemStore` (nouveau) | `IItemPersistenceService` (→ `ItemPersistenceService`, vérifié), `IItemCacheStore` (→ `ItemLookupService`, vérifié), `IServerConfigurationManager` | Oui — implémentations concrètes vérifiées (pas seulement les interfaces), aucune n'implémente/référence un membre de la SCC (I1 et I2) |
| `IUserViewFactory` (PR106b) | `UserViewFactory` (nouveau) | `IItemLookupService`, `IItemStore`, `IServerConfigurationManager`, `IFileSystem`, `Lazy<IProviderManager>` | I1 : oui (`IProviderManager` en `Lazy`, exclu du graphe eager ; non-Lazy violerait I1, `ProviderManager.cs:117`). I2 : oui sous réserve de vérification empirique — `.Value` attendu uniquement pour `QueueRefresh` post-persistance (exception assumée, à asserter en PR106b) |
| `IUserRootFolderProvider` (PR107) | `UserRootFolderProvider` (nouveau, **remplace l'alias**) | `IServerConfigurationManager`, `IFileSystem`, `IItemStore` | Non |
| `IChannelCatalog` (PR108) | `ChannelCatalog` (nouveau) | `IEnumerable<IChannel>`, `IItemLookupService`, `IItemStore`, `IUserManager` | Non |
| `ILiveTvPresenceProvider` (PR108) | `LiveTvPresenceProvider` (nouveau) | `IUserManager`, `IServerConfigurationManager`, `ILocalizationManager`, `IUserViewFactory`, `Lazy<IReadOnlyList<ILiveTvService>>` (amendement PR108, §5) | I1 : oui (`ILiveTvService` sous `Lazy` — non-Lazy violerait I1 via `DefaultLiveTvService → ILibraryManager`, testé). Runtime : 3e exception assumée — `.Value` à chaque `GetEnabledUsers()`, count-only, identique au legacy (§5), à réévaluer PR111 |
| `IUserViewCatalog` (PR109) | `UserViewCatalog` (nouveau) | `IUserRootFolderProvider`, `IUserViewFactory`, `IChannelCatalog`, `ILiveTvPresenceProvider`, `IItemSortService`, `IServerConfigurationManager`, `ILocalizationManager`, **`Lazy<IItemQueryService>`** (probe playlist/boxset, §3 — amendement PR109 ; remplacé par `IVisibleContentProbe` en DEBT-SERVER-1, 2026-10-18) | I1 : oui, **à condition que `IItemQueryService` soit `Lazy`** — ce RFC affirmait « Non » sans tracer l'impl concrète : le ctor d'`ItemQueryService` prend `IUserViewManager` ET `IChannelManager` directs (fallback de compat `Folder.GetItems`), une injection directe aurait construit le cycle `LibraryManager → IUserViewCatalog → IItemQueryService → IUserViewManager` **silencieusement vert à PR109, détonant à PR110** ; assertée par tests DiWiring (dont le miroir positif prouvant que le Lazy est porteur). Runtime : **4e exception assumée (bénie post-PR109)** — `.Value` forcé sur le chemin de listing `GetUserViews` quand une bibliothèque playlists/boxsets existe ; matériellement équivalent au legacy (le probe historique passait par `Folder.GetItemList` → static `BaseItem.LibraryManager`, donc le listing touchait déjà la SCC au runtime) ; jamais forcé sinon (testé) ; à réévaluer PR110/111 (alternative : port probe-only étroit) |

---

## 9. Design cible par PR (106–111)

### PR106a — `IItemStore` (leaf, aucun consommateur migré)
- Introduire `IItemStore` (§2/§8) : `Guid GetNewItemId(string, Type)`, `void CreateItem(BaseItem, BaseItem?)`, `void RegisterItem(BaseItem)`. Implémentation `ItemStore` composée de `IItemPersistenceService`/`IItemCacheStore`/`IServerConfigurationManager` (implémentations concrètes vérifiées §2 : `ItemPersistenceService`, `ItemLookupService`).
- **Sémantique transactionnelle explicite** : documenter et caractériser par test le comportement sur échec partiel (persistance réussie mais enregistrement cache échoué, et inversement). Référence = comportement actuel de `CreateItems` (ordre save-puis-register, pas de rollback) — le reproduire, pas l'améliorer silencieusement.
- **Événement `ItemSaved` + relais `ItemAdded`** (contrat §2, tranché) : `ItemStore` émet, `LibraryManager` relaie ; test exactly-once (nouveau chemin = exactement une notification `ItemAdded`, chemin historique inchangé, aucun doublon).
- Tests de parité contre le chemin historique (`CreateItems` restreint au cas `UserView`/`parent=null`). **Aucun consommateur migré dans cette PR.**
- **Critère de sortie mesurable** : I1 vérifié sur `ItemStore` (aucune arête eager vers la SCC) ; test exactly-once `ItemAdded` vert ; comportement sur échec partiel caractérisé.

### PR106b — `IUserViewFactory`
- Introduire `IUserViewFactory { GetNamedView(4 surcharges), GetShadowView }`, implémentation `UserViewFactory` — **`Lazy<IProviderManager>`, pas d'injection directe** (§2/§8, `ProviderManager` référence `ILibraryManager`).
- **Vérifier empiriquement quand `Lazy<IProviderManager>.Value` est évalué** (attendu : uniquement pour `QueueRefresh`, après persistance+enregistrement) et l'asserter par test — c'est la vérification I2 (§8). Si `.Value` est atteint avant persistance, revoir le design avant de merger.
- **Test architectural sur le graphe DI eager** (I1) : motif DiWiring, `UserViewFactory` sans chemin constructeur vers `ILibraryManager`/`IUserViewManager`/`IChannelManager`/`ILiveTvManager`.
- `LibraryManager` délègue ses méthodes `GetNamedView`/`GetShadowView` (surface `ILibraryManager` inchangée, façade historique conservée) à `IUserViewFactory`.
- **Ne touche pas** `UserViewManager`/`LiveTvManager` (migration différée à PR109/PR110 — l'objectif de sortie est que le leaf existe et soit testé en parité, pas que tous les appelants aient migré).
- **Critère de sortie mesurable** : tests de parité `GetNamedView`/`GetShadowView` verts contre le chemin historique ; I1 asserté par test architectural ; I2 asserté par test d'évaluation `Lazy.Value`.

### PR107 — Vrai propriétaire de `UserRootFolder`
- `UserRootFolderProvider` (nouveau, autonome) implémente `IUserRootFolderProvider` avec construction+cache+invalidation propres (le code actuel n'a pas d'invalidation — en ajouter une si le lifecycle cible l'exige, sinon documenter explicitement l'absence).
- `ApplicationHost.cs:568` : remplacer la factory lambda par `services.AddSingleton<IUserRootFolderProvider, UserRootFolderProvider>();`.
- `LibraryManager` cesse d'implémenter `IUserRootFolderProvider` (retirer de la liste d'interfaces `LibraryManager.cs:66`), injecte `IUserRootFolderProvider` et délègue ses ~9 appels internes à `GetUserRootFolder()`.
- **Critère de sortie mesurable** : `LibraryManager.cs:66` ne mentionne plus `IUserRootFolderProvider` ; `ApplicationHost.cs` n'a plus de cast `(IUserRootFolderProvider)sp.GetRequiredService<ILibraryManager>()` ; `ItemQueryScopeService` résout vers `UserRootFolderProvider`, pas vers `LibraryManager` (test de câblage DI dédié, motif `DiWiring_...` déjà utilisé ailleurs dans le repo, ex. `LibraryManagerItemLookupTests`).

### PR108 — Catalogues externes feuilles
- `IChannelCatalog`/`ChannelCatalog` et `ILiveTvPresenceProvider`/`LiveTvPresenceProvider` (§4, §5).
- **Critère de sortie mesurable** : ni `ChannelCatalog` ni `LiveTvPresenceProvider` ne référencent `IDtoService`, `ILibraryManager`, `IChannelManager` (pour ce dernier) ou `ILiveTvManager` (pour ce dernier) dans leur constructeur.

### PR109 — `IUserViewCatalog`
- `UserViewCatalog` construit sur `IUserRootFolderProvider` (PR107) + `IUserViewFactory` (PR106) + `IChannelCatalog`/`ILiveTvPresenceProvider` (PR108) + `IItemSortService` (déjà leaf) + `IServerConfigurationManager` (EnableFolderView) + `ILocalizationManager` (libellés) + `IItemQueryService` (probe playlist/boxset, remplace `Folder.GetItemList`, §3).
- Reproduit la logique de tri/préférences telle quelle (§6, aucun nouveau port requis) — lecture directe `User.GetPreferenceValues<T>`/`IsFolderGrouped`.
- **Critère de sortie mesurable** : tests de parité `GetUserViews` (leaf vs `UserViewManager.GetUserViews` historique) verts ; `UserViewCatalog` sans arête vers `ILibraryManager`/`IUserViewManager`/`IChannelManager`/`ILiveTvManager`.

### PR110 — Casser la SCC
- `LibraryManager` : `Lazy<IUserViewManager> _userViewManagerFactory` → `IUserViewCatalog` injecté directement (plus de `Lazy`, plus d'arête vers `IUserViewManager`). Retire champ `:77`, ctor param `:160`, propriété `:260`.
- `ItemQueryScopeService` : `IUserViewManager` → `IUserViewCatalog` dans le ctor (`ItemQueryScopeService.cs:40/53`, appel `:128`).
- `UserViewManager.GetUserViews` délègue à `IUserViewCatalog` (une seule implémentation réelle, plus de duplication) ; `UserViewManager` garde `GetUserSubView`/`GetLatestItems` mais route ses créations de vue via `IUserViewFactory` au lieu de `_libraryManager.GetNamedView`/`GetShadowView`.
- `LiveTvManager.GetInternalLiveTvFolder` migre vers `IUserViewFactory` (retire son unique dépendance à `_libraryManager.GetNamedView`) — casse la dernière arête `LiveTvManager → ILibraryManager` liée à ce chantier (`LiveTvManager` peut garder `ILibraryManager` pour d'autres besoins hors périmètre de ce RFC si applicable ; à vérifier en implémentation).
- **Critère de sortie mesurable** : `graphify path "LibraryManager" "UserViewManager"` et `graphify path "UserViewManager" "LibraryManager"` ne trouvent plus de chemin à 2 hops via `IUserViewManager`/`ILibraryManager` direct (un chemin plus long peut subsister via des ports hors périmètre — à documenter, pas nécessairement zéro chemin absolu) ; `Lazy<IUserViewManager>` absent de `LibraryManager.cs`.

### PR111 — Clôture
- **Critères de sortie (reformulés en PR105b — remplacent le « −3 paramètres » du plan v13, voir §7.5)** :
  1. `Lazy<IUserViewManager>` supprimé de `LibraryManager` (confirmé §7.4/PR110) ;
  2. cycle de construction cassé : I1 vérifié sur l'ensemble des nouveaux ports, `graphify path` bidirectionnel `LibraryManager`↔`UserViewManager` sans chemin 2-hops via `IUserViewManager`/`ILibraryManager` direct ;
  3. une seule implémentation réelle de `GetUserViews` (`UserViewCatalog` ; `UserViewManager.GetUserViews` délègue sans dupliquer) ;
  4. aucun nouveau fallback statique introduit : grep `BaseItem.LibraryManager`/`Folder.GetItems`/`Folder.GetItemList` sur tout le code introduit PR106–111 → zéro occurrence ;
  5. nombre de paramètres du ctor `LibraryManager` **mesuré et expliqué** — avant (30) / après, chaque écart justifié dans le journal du plan v13, **sans objectif chiffré arbitraire** ;
  6. zéro service locator (aucun `IServiceProvider` dans ces nœuds — déjà vrai aujourd'hui, à ne pas régresser) ; une seule orchestration query (`IItemQueryService`, déjà vrai depuis PR86) ; façades historiques (`ILibraryManager.GetNamedView`/`GetShadowView`, `IUserViewManager.GetUserViews`) délèguent sans dupliquer la logique.
- Mesurer aussi : SCC réelle après changement (re-dériver le graphe, pas supposer), chemins directs/lazy/statiques résiduels vers `LibraryManager` depuis les nouveaux ports.

---

## 10. Risques et rollback

1. ~~**Le −3 paramètres du ctor `LibraryManager` (PR111) n'est prouvé que pour −1 par ce RFC.**~~ **Retiré en PR105b** : le critère chiffré a été abandonné et remplacé par les critères qualitatifs §9/PR111 (Lazy supprimé, cycle cassé, une seule impl, pas de nouveau static, paramètres mesurés/expliqués). Le risque d'extraction artificielle pour atteindre un chiffre disparaît avec le chiffre.
2. **`BaseItem.LibraryManager` (static, §3) reste un chemin de cycle non éliminé.** Ce RFC pose l'invariant « pas de nouveau code contre `Folder.GetItems`/`GetItemList` » mais n'élimine pas le static existant (hors périmètre, chantier séparé documenté dans le plan v13). Risque résiduel : un futur contributeur réintroduit une dépendance cachée via ce static dans le code PR106-111 sans s'en rendre compte. Mitigation : revue de code ciblée sur ce point pour chaque PR106-111 ; ajouter un test de câblage DI si possible pour détecter l'usage du static dans les nouvelles classes.
3. **`IItemStore` est un nouveau port non prévu explicitement par le plan v13 initial (PR106-111).** C'est un prérequis découvert par ce RFC (§2, §8) — sans lui, PR106 tel que décrit dans le mandat (« sans dépendre d'ILibraryManager ni d'IUserViewManager ») est irréalisable, car `GetNamedView`/`GetShadowView` ont besoin de `GetNewItemId`/`CreateItem`/`RegisterItem` qui ne sont exposés sur aucun port existant. Mitigation : `IItemStore` est scindé de PR106 comme un sous-livrable explicite en tête de PR106, avec ses propres tests de parité, avant que `UserViewFactory` ne soit construit dessus.
4. **`LiveTvManager` garde `ILibraryManager`/`IChannelManager`/`IDtoService` même après PR108/PR110** (seul son appel `GetNamedView` migre). Si un autre chemin de `LiveTvManager` vers `UserViewManager`/`ChannelManager` existe hors du périmètre audité ici, la SCC pourrait se reformer partiellement pour d'autres consommateurs de `LiveTvManager`. Mitigation : `graphify path "LiveTvManager" "UserViewManager"` et retour, à revérifier à la clôture PR110/111.
6. **L'événement `ItemAdded` ne serait plus déclenché** si la création de vues named/shadow bascule de `LibraryManager.CreateItems` vers un `IItemStore`/`UserViewFactory` autonome (§2). Deux abonnés réels existent aujourd'hui : `LibraryMonitor` (`Reefin.Server.Core/IO/LibraryMonitor.cs:118`) et `LibraryChangedNotifier` (`Reefin.Server.Core/EntryPoints/LibraryChangedNotifier.cs:80`), tous deux abonnés sur l'instance `ILibraryManager`. Une classe `IItemStore` distincte ne peut pas lever cet événement sur cette instance. **Tranché en PR105b (§2)** : `IItemStore` émet `ItemSaved`, `LibraryManager` relaie vers `ItemAdded` (relais temporaire, tant que les abonnés legacy existent), règle d'unicité exactly-once, test de contrat exigé en PR106a. Le risque résiduel se réduit à l'exécution correcte de ce contrat (doublon si `CreateItems` migre vers `IItemStore` sans retirer son déclenchement direct — couvert par le test exactly-once sur les deux chemins).
7. **Rollback** : chaque PR106-109 introduit un nouveau leaf **sans retirer** l'ancien chemin (`LibraryManager.GetNamedView` continue de fonctionner en interne, délégué). Le risque de régression est donc confiné aux PR110 (bascule effective des consommateurs) — rollback possible PR par PR en revenant sur la seule migration de câblage DI (`ApplicationHost.cs`) sans toucher aux nouveaux leaves, qui restent inertes tant qu'ils ne sont pas branchés.

---

## 11. Séquence PR106–111 (résumé)

| PR | Contenu | Critère de sortie |
| --- | --- | --- |
| PR106a | `IItemStore` (leaf) : id/persistance/cache, sémantique transactionnelle caractérisée, événement `ItemSaved` + relais `ItemAdded`, aucun consommateur migré | I1 vérifié, test exactly-once `ItemAdded` vert, parité chemin historique |
| PR106b | `IUserViewFactory` (leaf) : parité `GetNamedView`/`GetShadowView`, `LibraryManager` délègue (façade conservée) | I1 asserté (test DiWiring), I2 asserté (évaluation `Lazy<IProviderManager>.Value` vérifiée) |
| PR107 | `IUserRootFolderProvider` implémenté par un service autonome, `LibraryManager` n'implémente plus le port | Alias `ApplicationHost.cs:568` supprimé |
| PR108 | `IChannelCatalog` + `ILiveTvPresenceProvider` (leaves autonomes) | Ni `IDtoService` ni les 4 membres de la SCC dans leurs ctors |
| PR109 | `IUserViewCatalog` construit sur PR106-108 + tri/préférences existants | Parité `GetUserViews` verte, zéro arête vers la SCC |
| PR110 | Migration `LibraryManager`/`ItemQueryScopeService`/`UserViewManager`/`LiveTvManager` vers les leaves ; suppression `Lazy<IUserViewManager>` | Chemin 2-hops `LibraryManager↔UserViewManager` disparu |
| PR111 | Clôture : mesures (SCC re-dérivée, chemins résiduels), façades délèguent, doc plan v13 mise à jour | Critères 1–6 de §9/PR111 (sans objectif chiffré de paramètres) |

Chaque PR de cette séquence est livrée via une vraie branche + PR GitHub (voir en-tête), avec la revue ciblée §10.2 (pas de nouveau code contre le static) à chaque fois.
