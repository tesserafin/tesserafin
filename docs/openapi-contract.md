# Contrat OpenAPI — versionnement, régénération déterministe, pinning

Issue #36. Ce document décrit comment le contrat OpenAPI de `reefin` est
généré, identifié, vérifié et consommé.

> ## ⚠ Update 2026-07-30 — hosted status (#162)
>
> Sections below written while hosted Actions were parked describe
> `openapi-pull-request.yml` and `openapi-generate.yml` as inert. They are not,
> and the statements marked *« Non vérifié ici »* about `openapi-diff` are
> superseded. Current state:
>
> | Control | Trigger | What it proves |
> | --- | --- | --- |
> | `Tests` (`ci-tests.yml`) | automatic, `pull_request` | the committed `openapi/openapi.json` is byte-for-byte what the **HEAD server** generates — the drift check of §7, now also enforced off-laptop |
> | `OpenAPI Check` (`openapi-pull-request.yml`) | automatic, `pull_request` | the **merge-base** contract compared with the **pull-request head** contract is compatible, breaking or indeterminate |
> | `./ci/run.sh` | local, mandatory | still the authoritative merge gate |
>
> `OpenAPI Check` reads both contract/lock pairs straight out of git, validates
> each `sha256` against its `contract.lock.json`, and runs `ci/openapi-compat.sh`.
> It never regenerates or canonicalises anything: `./ci/openapi-generate.sh`
> remains the single authoritative generator (§1).
>
> The engine is no longer `openapi-diff`. 2.1.6 died with
> `java.lang.StackOverflowError` on this exact document (run `30230606338`) and
> 2.1.7 still does; the `sed 's:allOf:oneOf:g'` workaround removed by #48 is
> therefore moot. The engine is oasdiff, pinned by version and immutable digest
> in `ci/openapi-compat.sh`, with the project's severity policy in
> `ci/openapi-severity-levels.txt` and 34 deterministic controls in
> `ci/tests/openapi-compat.test.sh`.
>
> Reports live in the Actions job summary and in a 7-day artifact. The
> pull-request comment workflow (`openapi-workflow-run.yml`) is **deleted**: it
> needed `secrets.JF_BOT_TOKEN`, which does not exist here.
>
> Two things §6 says are still true and still gaps: **nothing publishes a
> Tesserafin OpenAPI document** — `openapi-merge.yml` remains parked, targets
> an inherited Reefin SCP/SSH server, and is not a release channel — and
> **nothing automatically detects a mismatch between the two repositories**;
> cross-repository SDK regeneration is not hosted here. #97 stays open for
> that, and #94 stays open because required status checks and CodeQL remain
> unavailable on a private repository under a free organisation plan.

## TL;DR

```bash
./ci/openapi-generate.sh     # régénère openapi/openapi.json + openapi/contract.lock.json
./ci/run.sh                  # vérifie (entre autres) que le contrat commité n'a pas dérivé
```

Deux fichiers commités, toujours régénérés **ensemble** :

| Fichier | Rôle |
| --- | --- |
| `openapi/openapi.json` | le contrat lui-même, sous forme canonique |
| `openapi/contract.lock.json` | l'empreinte : `{algorithm, sha256, spec, version}` |

## 1. Pourquoi ce mécanisme existe

Avant cette tranche, la « génération » du contrat était un effet de bord d'un
test xUnit (`OpenApiSpecTests.GetSpec_ReturnsCorrectResponse`) qui écrivait la
réponse HTTP dans son répertoire `bin/`. Rien n'était commité, rien n'était
versionné, rien ne détectait une dérive.

Le job hébergé qui publiait ce fichier
(`.github/workflows/openapi-generate.yml`) et le job de diff
(`.github/workflows/openapi-pull-request.yml`, workflow « OpenAPI Check ») sont
aujourd'hui **inertes** : le quota GitHub Actions hébergé est épuisé depuis le
~2026-07-06 (`ci/run.sh`, `docs/local-ci.md`). Le seul gate de merge vivant est
`ci/run.sh`, en Docker, en local. Le contrat devait donc y être rattaché.

### Un seul générateur autoritaire (issue #48)

La tranche #36 avait laissé `OpenApiSpecTests` intact, `openapi-generate.yml`
continuant à le cibler par `--filter`. Il en résultait **deux** définitions
concurrentes du « contrat » :

| | générateur | artefact | canonique ? |
| --- | --- | --- | --- |
| local (#36) | `./ci/openapi-generate.sh` → `OpenApiContractTests` | `openapi/openapi.json` | oui |
| hébergé (avant #48) | `--filter OpenApiSpecTests` | `tests/.../bin/Release/net10.0/openapi.json` | **non** |

L'artefact hébergé était la réponse HTTP brute : `servers` réécrit depuis
l'en-tête `Host` du runner, ordre des clés non déterministe. Le gate hébergé
validait donc un artefact différent du contrat canonique.

Le job de diff portait par ailleurs un `sed -i 's:allOf:oneOf:g'` appliqué aux
deux entrées. C'était un problème **distinct** : `allOf` est émis depuis le
modèle de types C# et traverse la canonicalisation **inchangé** (`Canonicalize`
ne trie que les clés et retire `servers` ; le contrat canonique contient 586
occurrences d'`allOf`). Cette réécriture ne compensait donc rien du bruit
hôte — c'était un contournement symétrique qui changeait la composition de
schémas soumise à `openapi-diff`.

Depuis #48 :

- **`./ci/openapi-generate.sh` est le générateur unique.** Rien d'autre ne
  produit un contrat.
- `openapi-generate.yml` ne génère plus : il fait un `checkout` et téléverse le
  fichier **commité** `openapi/openapi.json`. C'est légitime précisément parce
  que `ci/run.sh` interdit à ce fichier de dériver du serveur (§7).
- le `sed allOf → oneOf` a disparu : `openapi-diff` reçoit désormais le document
  réel. **Non vérifié ici** : l'effet exact sur le rapport produit, le quota
  Actions rendant « OpenAPI Check » inerte.
- `OpenApiSpecTests` n'écrit plus rien et n'alimente plus aucun gate. Il ne
  garde que son assertion non contractuelle : le point de terminaison de
  découverte répond 2xx avec `application/json; charset=utf-8` — type de média
  asserté nulle part ailleurs (`OpenApiContractTests` ne fait qu'un
  `EnsureSuccessStatusCode` sur la même route).

Conséquence sur `openapi-merge.yml` (publication SCP/SSH vers le serveur de
dépôt) : ce fichier n'est **pas modifié** — déclencheurs, garde
`contains(github.repository_owner, 'reefin')`, condition de tag et cibles SCP
sont inchangés. Comme il consomme le même workflow réutilisable, ce qu'il
publie devient en revanche le contrat canonique au lieu de l'artefact brut.
Au premier `push` sur `master` après cette tranche, le `diff` côté serveur
(garde d'idempotence du script SSH) verra donc un contenu différent et fera
tourner les liens `unstable` une fois. C'est le comportement recherché : ce qui
est publié devient identique à ce qui est commité et pinné par
`contract.lock.json`.

## 2. `info.version` — provenance

`info.version` vaut la version d'assembly du serveur en cours d'exécution :

```
Reefin.Server/Extensions/ApiServiceCollectionExtensions.cs
  var version = typeof(ApplicationHost).Assembly.GetName().Version?.ToString(3);
  c.SwaggerDoc("api-docs", new OpenApiInfo { Title = "Reefin API", Version = version, ... })
```

qui remonte à `SharedVersion.cs` (`[assembly: AssemblyVersion("1.0.0")]`).
Aujourd'hui : **`1.0.0`**. Ni constante littérale, ni horodatage de build.

`OpenApiContractTests.InfoVersion_ComesFromServerAssemblyVersion` verrouille
cette provenance : si quelqu'un remplace la valeur par un littéral, le test
casse.

### Pourquoi pas le SHA du commit

L'issue #36 évoquait « `AssemblyVersion` et/ou le SHA du commit serveur ».
Le SHA du commit est **délibérément écarté** : il change à chaque commit, y
compris ceux qui ne touchent pas du tout à l'API. Injecté dans le document, il
rendrait le contrat différent à chaque commit et le contrôle de dérive
permanent et inutile — l'inverse de ce qu'on cherche.

L'identité d'un contrat, c'est donc le couple :

- **version** = version serveur (`1.0.0`) — *quel serveur*,
- **sha256** = empreinte du contenu canonique — *quel contrat exactement*.

Deux serveurs `1.0.0` avec des surfaces d'API différentes ont des `sha256`
différents. C'est ce couple qui sert de pin.

## 3. Déterminisme — comment il est obtenu

Les octets bruts servis par `/api-docs/openapi.json` ne sont **pas** une
identité stable. Deux sources de variation sans changement d'API :

1. **`servers`** — réécrit à chaque requête depuis l'en-tête `Host`
   (`ApiApplicationBuilderExtensions`, `PreSerializeFilter` ; et
   `CachingOpenApiProvider.AdjustDocument`). Il décrit où *ce* processus est
   joignable, pas ce qu'est l'API.
2. **L'ordre des membres dans les objets JSON** — artefact d'ordre d'émission
   de la génération de schémas.

La canonicalisation (`tests/Reefin.Server.Integration.Tests/OpenApiContract.cs`)
neutralise les deux :

- `servers` de premier niveau : **supprimé**. Un SDK généré ne doit pas hériter
  d'un `http://localhost/` issu du serveur de test.
- clés d'objets : réémises en ordre **ordinal**.
- tableaux : ordre **préservé** (en OpenAPI, `parameters`, `required`, `enum`
  sont sémantiquement ordonnés).
- nombres : réémis depuis le token brut (pas d'aller-retour `double`, qui
  reformaterait les entiers et perdrait de la précision).
- écriture : indentation 2 espaces, `NewLine = "\n"`, retour à la ligne final.
  Ces trois réglages sont **explicites** et non laissés au défaut :
  `JsonWriterOptions.NewLine` vaut `Environment.NewLine` par défaut (CRLF sous
  Windows).

`.gitattributes` fixe `eol=lf` sur les deux fichiers, pour que les octets sur
disque après `git checkout` soient ceux qui ont été hachés, quelle que soit la
plateforme.

### Preuve

Deux exécutions successives de `./ci/openapi-generate.sh` depuis un arbre
propre, chacune dans un conteneur neuf (donc un processus serveur froid) :

```
run 1: 46b7e041ee55ef96aef52154835e7581365e170a543bc0290f74411298ce29de
run 2: 46b7e041ee55ef96aef52154835e7581365e170a543bc0290f74411298ce29de
```

Vérifiable :

```bash
./ci/openapi-generate.sh && sha256sum openapi/openapi.json
./ci/openapi-generate.sh && sha256sum openapi/openapi.json
```

Et en test :
`OpenApiContractTests.Contract_IsByteIdentical_AcrossColdGenerations` compare
deux générations **froides**. Le mot « froide » est important :
`CachingOpenApiProvider` mémoïse le document pendant cinq minutes par instance
d'application, donc appeler deux fois `/api-docs/openapi.json` sur le même
serveur comparerait un objet en cache avec lui-même et ne prouverait rien. Le
test instancie donc deux `ReefinApplicationFactory` distinctes.

## 4. Empreinte — pas d'auto-référence

Le `sha256` **n'est pas écrit dans le document OpenAPI**. Y écrire l'empreinte
serait circulaire : insérer le hash change les octets, ce qui change le hash.

L'empreinte vit dans un fichier annexe, `openapi/contract.lock.json`, dont
l'entrée est *exclusivement* les octets canoniques de `openapi/openapi.json`.
Le contrat ne référence jamais l'annexe. La dépendance est un arc à sens
unique :

```
openapi/openapi.json  ──sha256──▶  openapi/contract.lock.json
```

Corollaire : on peut recalculer l'empreinte hors de tout outillage Reefin —
`sha256sum openapi/openapi.json` donne exactement la valeur du champ `sha256`.

## 5. Horodatage — constat et décision

Champs d'horodatage **dans le document produit** : aucun.

Vérifié sur le fichier réellement généré (pas déduit du code) :

```bash
grep -onE '"[^"]*20[0-9]{2}-[0-9]{2}-[0-9]{2}[^"]*"' openapi/openapi.json   # 0 résultat
```

Les occurrences de `Timestamp` / `GeneratedAt` dans le document sont des **noms
de propriétés de schémas**, c'est-à-dire de la surface d'API, pas des
métadonnées de génération. Exemple :
`PlaybackOperationalMetricsResponse.GeneratedAt`
(`Reefin.Api/Models/PlaybackSessionDtos/PlaybackOperationalMetricsResponse.cs`)
apparaît comme
`{"description": "When this snapshot was taken.", "format": "date-time", "type": "string"}`.
C'est un champ de réponse à l'exécution ; il est stable par construction et
n'est pas touché.

Décision pour le nouveau `contract.lock.json` : **omission pure** d'un champ
`generatedAt`.

- *Raison* : rien ne le consomme. Le seul consommateur du pin est
  `reefin-web/src/lib/reefin-sdk/spec/version.json`, qui s'appuie sur version +
  hash (§6). Aucun autre outil du dépôt ne lit de date de génération.
- *Raison décisive* : un tel champ changerait à chaque régénération **même
  quand le contrat est identique octet pour octet**. Or ce fichier existe
  précisément pour porter le signal « le contrat a bougé ». Un horodatage le
  ferait mentir en permanence et déclencherait le gate de dérive sur du bruit.

`SOURCE_DATE_EPOCH` aurait rendu un tel champ reproductible, mais aurait ajouté
une variable d'environnement à gérer pour porter une information que personne
ne lit. L'omission est strictement plus simple.

## 6. Publication et pinning

Un contrat publié est identifié par le couple `(version, sha256)` de
`openapi/contract.lock.json` :

```json
{
  "algorithm": "sha256",
  "sha256": "46b7e041ee55ef96aef52154835e7581365e170a543bc0290f74411298ce29de",
  "spec": "openapi/openapi.json",
  "version": "1.0.0"
}
```

### Consommation côté `reefin-web`

`reefin-sdk` (`reefin-web/src/lib/reefin-sdk/generated/`) est généré par
`openapi-generator-cli` 7.11.0 via `npm run generate:reefin-sdk`, et pinné par
`reefin-web/src/lib/reefin-sdk/spec/version.json`
(cf. `docs/pr116-client-migration-design.md` §4.1). Ce fichier doit désormais
reprendre **les deux** champs `version` et `sha256` publiés ici : la version
seule ne suffit pas à identifier un contrat, puisque la surface d'API évolue
entre deux bumps de `SharedVersion.cs` (le serveur est resté en `12.0.0` pendant
plusieurs tranches, puis est passé à `1.0.0` à l'ouverture de l'époque de
versions publiques — voir `docs/versioning-policy.md`).

Procédure de mise à jour du SDK client :

1. côté `reefin`, récupérer `openapi/openapi.json` et `openapi/contract.lock.json`
   au commit visé ;
2. vérifier `sha256sum openapi/openapi.json` contre le champ `sha256` ;
3. régénérer le SDK depuis ce fichier ;
4. recopier `version` + `sha256` dans `spec/version.json`.

**Rappel d'état** : `reefin-sdk-contract-check.yml` n'a jamais été livré côté
`reefin-web`, et « OpenAPI Check » est inerte côté `reefin` (quota Actions).
Rien ne détecte automatiquement un désalignement *entre les deux dépôts*. Ce
qui est désormais automatique, c'est la détection d'un désalignement
**contrat commité ↔ serveur** à l'intérieur de `reefin` (§7) — ce qui garantit
au moins que le fichier pinné par le client correspond bien à un serveur réel.

### Aucun wrapper manuel

Règle permanente du projet : tout ce qu'OpenAPI peut générer doit être généré.
`openapi/openapi.json` est la source unique du client typé ; aucun wrapper
d'API écrit à la main ne doit doubler une opération présente dans ce contrat.

## 7. Contrôle de dérive dans le gate de merge

`OpenApiContractTests.CommittedContract_MatchesRunningServer` fait partie de la
suite exécutée par `./ci/run.sh`. Si `openapi/openapi.json` ne correspond plus
à ce que le serveur produit, le gate échoue avec :

```
The committed OpenAPI contract is out of date.

  committed openapi/openapi.json : sha256 f0ef17be...
  server produces                : sha256 46b7e041...

You changed the API surface (a controller, a DTO, an attribute, a status code).
The contract is committed, so it has to be regenerated in the SAME commit:

    ./ci/openapi-generate.sh

then commit the updated openapi/openapi.json and openapi/contract.lock.json.
```

Le test a deux modes, sélectionnés par `REEFIN_OPENAPI_WRITE=1` (posé
uniquement par `ci/openapi-generate.sh`) : sans la variable il **vérifie**,
avec elle il **réécrit**. Sans cette séparation, la vérification contrôlerait
un fichier qu'elle vient d'écrire et ne pourrait jamais échouer.

### Pourquoi dans la suite de tests et pas comme étape de `ci/run.sh`

`ci/run.sh` est le gate obligatoire ; le fragiliser ou le ralentir a un coût
pour tout le monde. Placer la vérification dans la suite :

- réutilise le démarrage serveur que la suite paie déjà (coût marginal : un
  appel HTTP) ;
- laisse `ci/run.sh` inchangé en structure comme en durée (seul un commentaire
  y a été ajouté, pointant vers ce document) ;
- donne un message d'échec dans le rapport de test, là où le développeur
  regarde déjà.

## 8. Contraintes sur les évolutions futures du contrat

Le mécanisme impose une règle de livraison, valable pour toute tranche qui
touche à la surface d'API :

> **Un changement de surface d'API et la régénération du contrat doivent être
> livrés dans la même PR, et idéalement le même commit.**

Le gate rejette toute dérive : une PR qui change un contrôleur sans lancer
`./ci/openapi-generate.sh` ne passera pas `./ci/run.sh`. Ce n'est pas une
lourdeur mais l'effet recherché : le diff sur `openapi/openapi.json` devient la
revue du changement de contrat, lisible dans la PR, avant merge.

Corollaires pratiques :

- La revue d'une PR touchant l'API doit lire le diff de `openapi/openapi.json`.
  Un diff plus large que prévu = un changement de contrat non intentionnel.
- Un `openapi/openapi.json` modifié **sans** changement de code applicatif dans
  la même PR est suspect (édition manuelle) — le fichier est généré, jamais
  édité à la main.
- Un bump de `SharedVersion.cs` change `info.version`, donc le `sha256`, donc
  exige une régénération. C'est voulu : la version du contrat suit la version
  du serveur.

### Applicabilité aux changements prévus par PR #38

La PR de design #38 prévoit trois changements de contrat. Aucun n'est
implémenté ici ; le point est que le mécanisme sait les représenter :

| Changement prévu | Représentation | Effet sur le pin |
| --- | --- | --- |
| ajout du champ `PlaybackAttemptId` (additif, 4 surfaces) | 4 schémas gagnent une propriété dans `components.schemas` | nouveau `sha256`, `version` inchangée tant que `SharedVersion.cs` ne bouge pas |
| `PUT Playback/Sessions/{id}` : `404` → `422` | la clé `"404"` de `responses` devient `"422"` | idem |
| désambiguïsation des deux `409` de `GET .../Stream` | une seule clé `"409"` reste possible par opération : les deux cas doivent être distingués **dans le contrat** (description, ou schéma de corps d'erreur discriminé), pas par un second `409` | idem |

Le troisième point est la vraie contrainte imposée à X2 : **OpenAPI n'autorise
pas deux entrées pour un même code de statut sur une même opération**. La
désambiguïsation doit donc porter sur le corps de la réponse (par exemple un
schéma d'erreur avec un discriminant) ou, au minimum, sur la description — sinon
elle n'existe tout simplement pas dans le contrat, et les clients générés ne
pourront pas la voir. À trancher au moment de l'implémentation de PR #38, pas
ici.

Ces trois changements étant additifs ou correctifs sur des codes de statut, ils
ne justifient pas de bump de `SharedVersion.cs` par eux-mêmes : c'est le
`sha256` qui les distingue.

## 9. Fichiers concernés

| Fichier | Rôle |
| --- | --- |
| `ci/openapi-generate.sh` | régénération Docker, locale, sans Actions hébergées |
| `ci/openapi-compat.sh` | comparaison sémantique base ↔ head, fail-closed (#162) |
| `ci/openapi-severity-levels.txt` | politique de compatibilité — les règles élevées au niveau `err` |
| `ci/tests/openapi-compat.test.sh` | 34 contrôles déterministes de cette porte |
| `openapi/openapi.json` | contrat canonique commité |
| `openapi/contract.lock.json` | empreinte `{version, sha256}` |
| `tests/.../OpenApiContract.cs` | canonicalisation, empreinte, message de dérive |
| `tests/.../OpenApiContractTests.cs` | déterminisme, provenance de version, dérive |
| `tests/.../OpenApiSpecTests.cs` | surface HTTP du point de découverte (type de média) — plus aucun rôle de contrat |
| `ci/run.sh` | commentaire pointant ici (aucun changement fonctionnel) |
| `.gitattributes` | `eol=lf` explicite sur les deux fichiers générés |
| `.github/workflows/openapi-generate.yml` | téléverse le `openapi/openapi.json` commité (ne génère pas) |
| `.github/workflows/openapi-pull-request.yml` | « OpenAPI Check » — comparaison sémantique base ↔ head, automatique sur `pull_request` (#162) |
| `.github/workflows/openapi-merge.yml` | publication SCP/SSH héritée de Reefin — **garée**, ce n'est pas un canal de publication Tesserafin |
