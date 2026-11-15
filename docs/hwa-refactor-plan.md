# Plan implémentation refactor pipeline FFmpeg (préalable autodétection HWA)

Les premières sections décrivent les extractions implémentées sur les branches de consolidation.
Le diagnostic et le séquencement historiques sont conservés ensuite avec leurs anciennes références.

## Première extraction logicielle — consolidation, 2026-11-14

Cette tranche est construite sur `master` après la fusion du transcodage film (#411).
Elle extrait la composition des filtres logiciels et leurs expressions de redimensionnement ;
la sélection des codecs, les arguments de qualité/débit et l'exécution du processus restent aux
étages existants. Elle ne clôt pas l'étape 5 de #129.

- `EncodingHelper.CaptureSoftwarePipeline` résout une fois les capacités, le format de surface,
  la géométrie demandée, le désentrelacement, les valeurs de tone mapping et les sous-titres.
- `SoftwarePipelinePlan` et `SoftwareScalingPlan` ne retiennent que des valeurs immuables. Les
  variantes texte/graphique des sous-titres sont exclusives. Les chemins/fonts ont déjà été
  résolus avant la capture ; le renderer ne consulte pas le disque.
- `SoftwarePipelineRenderer` compose les filtres, rend le scaling et le désentrelacement. Il
  ne lit ni `EncodingJobInfo`, ni `EncodingOptions`, ni `IMediaEncoder`, ni l'environnement.
  Ses sorties sont des tableaux immuables ; les assembleurs peuvent en faire leurs listes locales.
- Le parcours logiciel et les branches matérielles qui font du copy-back utilisent la même
  implémentation. Les entrées publiques `GetSwVidFilterChain`, `GetSwScaleFilter` et
  `GetSwDeinterlaceFilter` sont supprimées. Aucun forwarder ni flag de coexistence n'est ajouté.
- Le parcours Sessions SDR fusionné continue à produire sa requête d'encodage depuis le
  `PlaybackExecutionPlan` conservé (`EnginePlanEncodingRequest`). Ce snapshot de filtres
  constitue un détail de rendu ; il n'ajoute pas de décision de lecture.
- Le mapping, l'insertion FPS commune et la sérialisation finale `-vf` / `-filter_complex`
  restent dans l'assembleur actuel. Le `TranscodePlan` diagnostique existant reste diagnostique.

Le garde-fou d'architecture interdit les dépendances à la configuration, aux jobs et aux API
I/O dans ce renderer, vérifie l'immuabilité des paramètres et l'absence des anciennes entrées.
Les tests couvrent scaling, copy-back, Main10, tone mapping invariant, sous-titres et capture
indépendante des mutations ultérieures. Trois commandes FFmpeg réelles vérifient H.264,
pixel format et dimensions avec ffprobe. Les parcours Sessions sont vérifiés séparément en
intégration, avec FFmpeg, seek et nettoyage des jobs. Ces contrôles ne constituent pas une
validation matérielle ou une nouvelle acceptation HDR/3D.

Le retrait des trois méthodes est une rupture d'ABI autorisée par la politique pré-lancement.
La comparaison reste exécutée et ses différences restent visibles. Cette branche reprend,
à l'identique de #412, le mode explicite `ABI_ALLOW_BREAKING=1` dans le verdict CI : erreurs
d'outillage, artefacts manquants, manifeste invalide et zéro comparaison restent bloquants.

## Extraction NVIDIA/CUDA — consolidation, 2026-11-14

Cette tranche suit #417 et reste dans l'étape 5 de #129. Elle ne change pas le choix du codec,
du périphérique ou du parcours Sessions. `EncodingHelper` passe de 7 878 à 7 741 lignes.

- `RenderNvidiaFilterChain` reste un point de routage privé : si le pipeline CUDA n'est pas
  disponible, il utilise le renderer logiciel unique. Ce parcours logiciel est une décision
  d'exécution selon les capacités, pas une couche de compatibilité avec un ancien serveur.
- `CaptureNvidiaPipeline` résout une fois les surfaces d'entrée/sortie, les capacités, les
  dimensions après rotation, les paramètres de tone mapping et les filtres de sous-titres.
  Les fonctions partagées de géométrie et de formatage tone mapping restent partagées ;
  leurs résultats sont des chaînes immuables, sans callback vers le job ou les services.
- `NvidiaPipelineRenderer` possède l'ordre des filtres et les transferts mémoire/CUDA,
  le désentrelacement CUDA, la rotation et les graphes d'overlay. Les sous-titres texte en
  mémoire, texte sur source alpha CUDA et graphiques sont des variantes exclusives.
  Le scaling logiciel réutilise `SoftwarePipelineRenderer`, y compris le format 3D existant.
- Les entrées publiques `GetNvidiaVidFilterChain` et `GetNvidiaVidFiltersPrefered` sont
  supprimées, ainsi que la branche CUDA de `GetHwDeinterlaceFilter`. Aucun forwarder,
  flag ou second renderer n'est conservé. Le mapping, les FPS et la commande finale restent
  dans l'assembleur commun.

La matrice de 67 graphes a été enregistrée sur l'implémentation de #417 (`f402a38783`), avant
l'extraction, puis comparée par `GetVideoProcessingFilterParam`, le point d'entrée de production.
Elle couvre mémoire/CUDA, NVENC/logiciel/MJPEG, SDR/HDR, sous-titres SRT/ASS/PGS, rotation,
désentrelacement et capacités optionnelles absentes. Le fichier de référence est statique ;
les tests ne le régénèrent pas. D'autres contrôles vérifient la capture indépendante des
mutations du job, de la configuration et des capacités, les sorties immuables, et les mêmes
interdictions de services/I/O que pour le renderer logiciel.

Ces graphes caractérisent le rendu des commandes ; ils ne prouvent pas une exécution CUDA,
NVENC, HDR ou Dolby Vision sur matériel NVIDIA. La validation des sessions avec FFmpeg logiciel
reste distincte. Les autres backends et la sélection des encodeurs/décodeurs restent à extraire.

Les sections suivantes conservent le diagnostic et le séquencement historiques ; leurs noms
`MediaBrowser.*` et numéros de ligne ne décrivent pas l'arbre après cette extraction.

## État actuel confirmé (fichiers clés)

| Classe | Fichier | Lignes | Rôle actuel |
|---|---|---|---|
| `EncodingHelper` | `MediaBrowser.Controller/MediaEncoding/EncodingHelper.cs` | 7987 | tout: décodeur, encodeur, device args, filtres, HLS, tone mapping |
| `EncodingJobInfo` | `MediaBrowser.Controller/MediaEncoding/EncodingJobInfo.cs` | 735 | base mutable partagée API/encoding (commentaire L21: "common base class... for now") |
| `StreamState` | `MediaBrowser.Controller/Streaming/StreamState.cs` | 183 | hérite EncodingJobInfo, ajoute HTTP + live source + `Dispose()` synchrone (L171-176, commentaire "REVIEW" L170) |
| `TranscodingJob` | `MediaBrowser.Controller/MediaEncoding/TranscodingJob.cs` | 288 | 1 `Process` unique par job |
| `TranscodeManager` | `MediaBrowser.MediaEncoding/Transcoding/TranscodeManager.cs` | 757 | `StartFfMpeg()` L371-540: construit `Process` brut direct (L417-435), attend fichier de sortie en polling (L510-513) |
| `JobLogger` | `MediaBrowser.Controller/MediaEncoding/JobLogger.cs` | 164 | `ParseLogLine()` L62-161: split `stderr` sur espaces, cherche `fps=`/`time=`/`size=`/`bitrate=` |
| `EncoderValidator` | `MediaBrowser.MediaEncoding/Encoder/EncoderValidator.cs` | 699 | `GetProcessOutput()` L636-665: ne lit qu'UN flux (stdout OU stderr) selon param `readStdErr` — l'autre non consommé |
| `MediaEncoder` | `MediaBrowser.MediaEncoding/Encoder/MediaEncoder.cs` | 1404 | singleton, listes globales encodeurs/décodeurs/hwaccels |

**Mutation env globale confirmée** — `EncodingHelper.cs`:
- L1053-1054: `Environment.SetEnvironmentVariable("LIBVA_DRIVER_NAME", "i965")` + `..._JELLYFIN`
- L1072: `Environment.SetEnvironmentVariable("AMD_DEBUG", "noefc")`

Ces deux lignes mutent le process serveur entier, pas seulement le futur process FFmpeg. Confirme le point 7 du diagnostic original.

**Device args backends** déjà séparés par méthode (bonne nouvelle, base réutilisable) :
- `GetRkmppDeviceArgs()` L825, `GetVideoToolboxDeviceArgs()` L833, `GetVaapiDeviceArgs()` L910, `GetQsvDeviceArgs()` L947, `GetHwaccelType()` L6587.

---

## Ordre de PR (identique à la proposition initiale, confirmé faisable)

### PR 1 — Tests de caractérisation
Aucun fichier à toucher. Construire matrice de commandes attendues (software/VAAPI/QSV/NVENC/AMF/VideoToolbox/RKMPP × HDR/SDR × sous-titres × H264/HEVC/AV1 × progressive/HLS/live) en snapshot-testant `EncodingHelper.GetVideoEncoder()` (L480) et consorts tels quels. Zéro risque, sert de filet pour tout le reste.

### PR 2 — `FfmpegCommand` + `IFfmpegProcessRunner`
Nouveaux types dans `MediaBrowser.Controller/MediaEncoding/`:
```csharp
public sealed record FfmpegCommand(string Executable, ImmutableArray<string> Arguments, ImmutableDictionary<string, string> Environment, string? WorkingDirectory);
```
Point d'injection unique: `TranscodeManager.StartFfMpeg()` L417-435 construit aujourd'hui `Process`/`ProcessStartInfo` en dur. Remplacer par `IFfmpegProcessRunner.StartTranscodeAsync(command, ct)`.
`EncoderValidator.GetProcessOutput()` L636-665 et `GetProcessExitCode()` L668-691 basculent sur `RunProbeAsync()` — corrige au passage le bug de flux non consommé (L663: un seul de `StandardError`/`StandardOutput` est lu, l'autre peut saturer son buffer et bloquer le process).
Les deux `Environment.SetEnvironmentVariable` globaux (L1053-1072) deviennent des entrées dans `FfmpegCommand.Environment`, plus jamais posés sur le process serveur.

### PR 3 — Progression structurée + classification erreurs
Remplacer `JobLogger.ParseLogLine()` (L62-161, parsing `stderr` humain fragile) par lancement avec `-progress pipe:1 -nostats`, parsing clé=valeur strict (`out_time_us`, `total_size`, `progress=continue/end`). `stderr` reste réservé diagnostics → alimente un nouveau `FfmpegErrorClassifier` (catégories: DeviceUnavailable, PermissionDenied, UnsupportedCodec, EncoderSessionLimit, DeviceInitializationFailed, FilterInitializationFailed, InvalidInput, ResourceExhausted, Unknown).
Touche `TranscodeManager.StartFfMpeg()` L505 (`new JobLogger(...).StartStreamingLog(...)`) et l'appel `state.ReportTranscodingProgress()` L159 de `JobLogger`.

### PR 4 — `TranscodeSession` / `TranscodeAttempt`
`TranscodingJob` (288 lignes, un seul `Process`) devient conteneur de session ; nouvelle classe `TranscodeAttempt` porte `Process` + `FfmpegCommand` + log + résultat. `TranscodeManager.OnFfMpegProcessExited()` L640 et `OnTranscodeBeginning()` L576-609 adaptés pour créer une tentative au lieu de fermer directement la session. Toujours 1 seule tentative activée dans cette PR — le fallback multi-tentatives vient en PR 9.

### PR 5 — Extraction renderers par backend
`EncodingHelper.cs` (7987 lignes) éclaté. Les méthodes device-args (L825-953) et `GetHwaccelType()` (L6587) sont déjà isolées — bon point de départ pour extraire `VaapiPipelineRenderer`, `QsvPipelineRenderer`, etc. Chaque renderer reçoit un `TranscodePlan` déjà décidé, ne relit pas la config globale. Un backend à la fois, avec les tests PR 1 comme garde-fou de non-régression.

### PR 6 — Capacités par périphérique
`MediaEncoder` (1404 lignes, singleton, listes globales `SupportsEncoder`/`SupportsDecoder`/`SupportsHwaccel`) devient producteur de `HardwareCapabilitySnapshot` par device. Point de départ concret de l'autodétection.

### PR 7-9 — Planner diagnostic → sélection auto → fallback
Dans cet ordre, chacun activable/désactivable indépendamment.

---

## Refactor transverse à faire en parallèle (pas bloquant, mais à ne pas oublier)

- **`EncodingJobInfo`/`StreamState`** (PR indépendante, peut suivre PR 4) : séparer en `StreamingRequest` (immuable) / `TranscodeRequirements` / `TranscodePlan` / `TranscodeRuntimeState` mutable. `StreamState.Dispose()` L161-182 ferme une source live de façon synchrone dans un `Dispose()` — à corriger en même temps (le commentaire `// REVIEW` L170 le signale déjà comme suspect dans le code même).
- **Nullable reference types** : `EncodingHelper`, `EncodingJobInfo`, `IMediaEncoder`, `MediaEncoder`, `JobLogger` ont `#nullable disable` en tête de fichier (confirmé L1 de `JobLogger.cs` et `EncodingJobInfo.cs`). Ne pas convertir tout le fichier existant — mais chaque nouvelle classe extraite (renderers, `FfmpegCommand`, `TranscodeSession`) naît nullable-enabled dès le départ.

## Ce qui NE doit PAS être fait avant l'autodétection
- Déplacer bêtement les branches VAAPI/QSV/NVENC/AMF/VideoToolbox/RKMPP dans 6 fichiers sans changer le couplage (réduit taille fichier, pas dette).
- "Grand nettoyage" du service locator / statics (`BaseItem`, `Episode`) — trop transversal, à faire domaine par domaine plus tard, pas un préalable HWA.
- Réécriture Rust — écarté: coût GPU/codec est dans FFmpeg pas dans l'orchestrateur C#, casserait le système de plugins .NET existant, ne résout aucune des règles fonctionnelles (Dolby Vision, profils client, HLS timestamps).
