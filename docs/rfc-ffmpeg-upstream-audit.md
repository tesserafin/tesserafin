# RFC — Audit de compatibilité FFmpeg upstream (n9.0.2) pour Tesserafin

- **Issue** : #340 (parente #335)
- **Statut** : brouillon. Faits : inventaire mécanique (phase 1), preuve de compilation linux-x64 (§3.6), inventaire des scénarios et contrat mesuré sur A1, B et C (§3.7). Restent : linux-arm64, audit Windows, prototype.
- **Date** : 2026-10-23
- **Nature** : design et preuves uniquement, aucun changement de runtime, de pins ou d'`EncodingHelper`.

Ce document ne tranche pas encore entre les options. Il fixe ce qui est mesuré, ce qui ne l'est pas, et ce qui change déjà la discussion.

## 1. Options comparées

| Option | Description |
|---|---|
| A1 | Rester sur jellyfin-ffmpeg 7.1.4-3 (état actuel) |
| A2 | Passer à jellyfin-ffmpeg 8.1.3-1 |
| B | FFmpeg upstream n9.0.2 sans patch, serveur adapté |
| C | n9.0.2 + correctifs maintenus par Tesserafin (branche de commits rebasée par release) |

FFmpeg 9.1 n'est pas sortie (seul `n9.1-dev` existe). jellyfin-ffmpeg n'a pas de série 9.x : sa branche `jellyfin` est en 8.1.3-2.

## 2. Méthode et limites

**Entrées épinglées**

| Entrée | Commit |
|---|---|
| jellyfin-ffmpeg `v7.1.4-3` (= `components.json`) | `d4590e12452f94d40e413caecb34b08de608353b` |
| jellyfin-ffmpeg `v8.1.3-1` | `253db2a7b0a8045c54ce68ce33d7f601229b1822` |
| FFmpeg `n7.1.4` | `9abe92e3af7fa7becc8f7f742b1457b4c28220a6` |
| FFmpeg `n8.1.3` | `1041abdc962f4cc4f394aa8de9dc5236c0c3b9e7` |
| FFmpeg `n9.0.2` | `946fcce07b6dcd0331c8cc609192aeff5e1924f8` |
| Tesserafin `master` | `19b2d77bd7` |

**Instrument.** Chaque patch de la série est testé seul contre un arbre FFmpeg vierge avec `patch --dry-run -F2`, dans les deux sens. Un hunk est « OK » s'il s'applique, « déjà présent » s'il s'applique à l'envers. La série est ensuite appliquée **en séquence** (fuzz 2, rejets écartés), ce qui reflète un vrai portage. Script en annexe B.

**Témoins.**
- Série 7.1.4-3 appliquée en séquence sur n7.1.4 : 0 échec. Les entrées sont cohérentes.
- Patch 0001 sur n7.1.4 vierge : 3/3 OK, 0 déjà présent. Après application : 0 OK, 3/3 déjà présents. L'instrument distingue les deux états.
- Calibrage du test isolé sur la base d'origine : 8.1.3-1 sur n8.1.3 donne 909/939 hunks OK (97 %), 7.1.4-3 sur n7.1.4 donne 791/805 (98 %). Un patch testé seul échoue donc sur ~2-3 % de hunks à cause de ses dépendances dans la série, sans aucun écart upstream.

**Ce que l'instrument ne prouve pas.**
- Un hunk qui s'applique n'est pas un code qui compile, ni un code correct. La compilation est traitée en §3.6.
- « Déjà présent » peut être une coïncidence de contexte sur de petits hunks. Les cas qui décident sont à relire à la main.
- L'inventaire côté serveur (§3.2) lit les littéraux de chaîne du code non-test. Un nom composé à l'exécution lui échappe. Un seul cas trouvé : `"tonemap_{0}=…"` dans `EncodingHelper.cs`, qui couvre `tonemap_cuda` et `tonemap_opencl`.

## 3. Résultats

### 3.1 Trois FFmpeg différents sont livrés aujourd'hui

| Livraison | Source du binaire | Accélérations compilées |
|---|---|---|
| Docker (amd64/arm64) | `.deb` jellyfin-ffmpeg 7.1.4-3 (`Dockerfile`) | VAAPI, QSV (libvpl), CUDA/NVENC, AMF, OpenCL, Vulkan + libplacebo, RKMPP/RKRGA |
| Natif Linux | `ci/ffmpeg` (`ffmpeg-configure*.txt`) | VAAPI, CUDA/NVENC, OpenCL ; x64 : + libvpl, AMF. Pas de Vulkan ni libplacebo, pas de RKMPP |
| Windows | `ci/windows/ffmpeg` | D3D11VA/D3D12VA/DXVA2, AMF, libvpl, CUDA/NVENC. Pas de VAAPI, OpenCL ni Vulkan |

Conséquences :
- Une capacité validée sous Docker ne l'est pas pour le natif, et inversement. Le contrat de capacités doit être défini par livraison tant que ces binaires diffèrent.
- **Licence, à traiter hors de cet RFC.** Le FFmpeg de l'image Docker est configuré avec `--enable-gpl --enable-libfdk-aac` (constaté par `ffmpeg -buildconf` dans `tesserafin-local:master-8f6ce4e601` et dans l'image `ghcr.io/tesserafin-project/tesserafin-server` `ac038fbe9d86`) et expose l'encodeur `libfdk_aac`. `EncodingHelper.cs:800` le préfère dès qu'il est présent. C'est la combinaison que le patch 0029 rend possible et que `fork-patches.json` classe `unsafe` pour le runtime natif.

### 3.2 Ce que le serveur exige d'absent d'upstream n9.0.2

199 composants FFmpeg (filtres, codecs, BSF) sont nommés par le code serveur. Ceux qui existent chez jellyfin-ffmpeg 8.1.3-1 et pas dans n9.0.2 :

| Composant | Où le serveur le nomme | Patch 8.1.3-1 | Livraison qui le compile aujourd'hui |
|---|---|---|---|
| `tonemapx` | `EncodingHelper`, `MediaEncoder`, `EncoderValidator` | 0053 | Docker, natif Linux, Windows |
| `alphasrc` | `EncodingHelper` (12), `EncoderValidator` | 0023 | toutes |
| `hwupload_vaapi` | `EncodingHelper` (6), `EncoderValidator` | 0014 | Docker, natif Linux |
| `scale_opencl` | `EncodingHelper`, `EncoderValidator` | 0006 | Docker, natif Linux |
| `yadif_opencl`, `bwdif_opencl` | `EncodingHelper`, `EncoderValidator` | 0069 | Docker, natif Linux |
| `tonemap_cuda` | `EncoderValidator`, `"tonemap_{0}"` | 0004 | toutes |
| décodeur `ac4` | `EncoderValidator` | 0054 | toutes |
| options `remove_dovi` / `remove_hdr10plus` de `hevc_metadata` et `av1_metadata` | `EncoderValidator` | 0061 | toutes |
| `*_rkmpp`, `*_rkrga` | `EncodingHelper`, `EncoderValidator` | 0042 | Docker arm64 seulement |
| `*_videotoolbox` (overlay, tonemap, bwdif, encodeur mjpeg) | `EncodingHelper`, `EncoderValidator` | 0044, 0046, 0047, 0056 | aucune (pas de plateforme macOS déclarée) |

Options sondées par `EncoderValidator` et déjà présentes upstream : le framesync (« Action to take when encountering EOF ») d'`overlay_vaapi` et d'`overlay_cuda`, et `format` de `scale_cuda`. Absentes upstream (mesuré sur un binaire n9.0.2 vierge, §3.7) :
- le framesync d'`overlay_opencl` (0008) et d'`overlay_vulkan` (0082). Une version précédente de ce RFC le donnait présent upstream, à tort ;
- `alpha_format` d'`overlay_opencl` et `overlay_cuda` (0008, 0031), passé par `EncodingHelper.cs:4227,4451` pour l'incrustation de sous-titres ;
- `bt2390` de `tonemap_opencl` (0007 ; upstream ne le propose que dans `libplacebo`) ;
- le demi-tour de `transpose_opencl` (0051). Upstream l'a pour `transpose_cuda`, `transpose_vaapi`, `transpose_vt` et `vpp_qsv`. `fork-patches.json` classe ce patch `useful` (« le serveur ne le nomme pas »), alors que `EncoderValidator` sonde `TransposeOpenclReversal` : premier exemple de classement à revoir.

Retraits upstream en 9.0 (NPP, OMX, `libcelt`, `sonic`, `v308`/`v408`/`v410`) : le serveur n'en nomme aucun.

**Lecture.** L'option B suppose de remplacer ou supprimer tous les usages de la première partie du tableau. Les chemins VideoToolbox ne sont compilés par aucune livraison, et RKMPP seulement par Docker. Ils peuvent sortir du périmètre sans attendre la décision sur le fork.

### 3.3 La série jellyfin 8.1.3-1 se porte en grande partie sur n9.0.2

| Série → base | Hunks | OK seuls | Patches intacts en séquence | Hunks en échec en séquence |
|---|---:|---:|---:|---:|
| 7.1.4-3 → n9.0.2 | 805 | 548 (68 %) | — | — |
| 8.1.3-1 → n9.0.2 | 939 | 811 (86 %) | 78 / 100 | 108 |
| 8.1.3-1 → n8.1.3 (témoin) | 939 | 909 (97 %) | — | — |

Sur les 22 patches non intacts :
- **Absorbés upstream** (tous les hunks déjà présents) : 0016 (NVDEC > 32 surfaces), 0084 (détection AC-4 en MPEG-TS), 0090 (`sw_pix_fmt` dans codecpar).
- **Dérive de contexte de 1 à 3 hunks** : 0002, 0003, 0004, 0006, 0022 (sub2video), 0027 (side data DoVi dans hlsenc/mpegtsenc), 0042, 0044, 0045, 0055, 0061 (BSF DoVi/HDR10+), 0066, 0092, 0094. Un portage manuel court est probable, à confirmer à la compilation.
- **Portage réel** : 0005 AMF (19 hunks en échec, dont 13 déjà présents), 0078 backports Vulkan (35, dont 29 déjà présents), 0082 filtres Vulkan (13), 0083 backports CUDA (10, dont 8 déjà présents).
- **Remplacé upstream** : 0050 `transpose_cuda` (5 hunks en échec) crée un filtre qu'upstream fournit désormais (`vf_transpose_cuda.c`, demi-tour compris). À comparer, pas à porter.

Ces chiffres sont à lire avec §2 : environ 3 % des échecs viennent de l'instrument lui-même.

Tableau complet en annexe A. L'arbre porté est conservé pour la compilation (§6).

### 3.4 Patches « required » abandonnés par Jellyfin en 8.1

21 patches de 7.1.4-3 n'existent plus dans 8.1.3-1. Pour ceux classés `required`, état dans n9.0.2 :

| Patch 7.1.4-3 | Hunks déjà présents dans n9.0.2 | Lecture |
|---|---|---|
| 0017 backports VAAPI | 7 / 7 | absorbé |
| 0045 fMP4 x265 sur Safari | 1 / 1 | absorbé |
| 0065 yuv420p → p01x | 12 / 14 | absorbé en grande partie |
| 0093 écarts n7.1.4 / release/7.1 | 9 / 12 | absorbé en grande partie |
| 0078 race CSC VAAPI | 3 / 4 | absorbé en grande partie |
| 0087 padding RPU DoVi | 4 / 5 | absorbé en grande partie |
| 0088 retrait de l'EL au strip DoVi | 1 / 2 | partiel, à relire |
| 0015 `alpha_format` d'`overlay_vaapi` | 0 / 4 | **absent**. Remplacé en 8.1 par 0015 « check alpha mode for vaapi overlay ». Le serveur ne passe `alpha_format` qu'à `overlay_cuda` et `overlay_opencl` : la justification `required` est à revoir |
| 0023 décodage HEVC HDR dans le BSF | 0 / 3 | **absent**, à vérifier |
| 0027 encodage HEVC VAAPI sur Tiger Lake | 0 / 1 | **absent**, à vérifier |
| 0089 codecpar quand le décodeur active des BSF | 0 / 1 | **absent**, à vérifier |

Les lignes « absent » sont soit devenues inutiles avec le code upstream 8.x, soit perdues par Jellyfin. Chacune doit être rattachée à un scénario (§6) avant une décision.

### 3.5 Dépendances de build : n9.0.2 exige le SDK AMF ≥ 1.5.2

Premier essai de compilation de l'arbre porté (§6) dans le builder `ci/ffmpeg`, configuration linux-x64 de F0 : le configure s'arrête sur `ERROR: amf requested but not found`. `ffbuild/config.log` montre la condition qui échoue :

```
check_cpp_condition amf AMF/core/Version.h (AMF_VERSION_MAJOR << 48 | AMF_VERSION_MINOR << 32 | AMF_VERSION_RELEASE << 16 | AMF_VERSION_BUILD_NUM) >= 0x1000500020000
```

Version minimale du SDK AMF exigée par le `configure` FFmpeg :

| FFmpeg | Minimum AMF |
|---|---|
| n7.1.4 | 1.4.33 |
| n8.1.3 | 1.4.36 (la série jellyfin 8.1.3-1 le relève à 1.5.0, patch 0005) |
| n9.0.2 | **1.5.2** |

`components.json` épingle `amf-headers` en **v1.5.0** (`afed28d37aca1938da2eedc50599bb3535a987ec`), choisi comme minimum pour la série 7.1.4-3 (v1.4.35 n'a pas `AMF_VIDEO_ENCODER_AV1_B_PIC_PATTERN`). Les options B et C imposent donc de monter ce pin, au moins en v1.5.2 (`eadd00804d5f7e5cd8c85d540073198312870776`) ; la dernière étiquette est v1.5.3 (`57ee0cfb0e1a08d9041f77435653794c1b012be0`).

Conséquences :
- Monter le pin touche la chaîne F0 (`components.json`, empreintes acceptées, SBOM, reproductibilité) sur linux-x64 et win-x64, les deux plateformes qui compilent AMF. linux-arm64 n'est pas concernée (AMF n'y est pas compilé).
- Les en-têtes ne suffisent pas : le pilote AMD installé doit fournir un runtime AMF compatible. Aucun banc ne le vérifie (§4.2).
- C'est le premier coût de dépendance mesuré du passage en 9.x. D'autres peuvent apparaître une fois le configure passé.

La suite de l'essai (§3.6) substitue les en-têtes AMF v1.5.3 dans une copie du cache, sans toucher au pin du dépôt.

### 3.6 Preuve de compilation linux-x64

**Montage.** Le vrai pipeline F0 (`fetch-sources.sh`, dépendances épinglées, image `ci/ffmpeg/builder` figée, `ffmpeg-configure.txt` + `ffmpeg-configure.linux-x64.txt`, `--network none`), avec une seule substitution : la source FFmpeg est l'arbre porté, placé dans une copie du cache à la place de `git/jellyfin-ffmpeg`. Le dépôt n'est pas modifié. Les contrôles du builder s'exécutent sur l'arbre porté : `tonemapx` et `alphasrc` présents, `libfdk_aac` toujours dans la liste nonfree. Le patch fdk-aac de la série 8.1 (0026) est retiré, conformément à F0. Build incrémental de développement : il ne vaut pas preuve de reproductibilité.

**Référence.** Le F0 actuel (jellyfin-ffmpeg 7.1.4-3) est reconstruit avec les mêmes dépendances. Écart à noter : il utilise lui aussi les en-têtes AMF v1.5.3 au lieu du pin v1.5.0.

**Résultat.** Le build aboutit et produit `ffmpeg version 9.0.2-tesserafin.1`, configuré `--disable-libfdk-aac`, sans encodeur `libfdk_aac`, avec pour seules bibliothèques partagées non système `libva` et `libva-drm`, comme F0. Il a fallu :

| Correction manuelle | Patch d'origine | Cause |
|---|---|---|
| 7 lignes `*_deps` de `configure` (`tonemap_cuda` ×2, `scale_opencl`, `tonemap_d3d11`, `overlay_videotoolbox`, décodeur `mpeg4_rkmpp` ×2) | 0004, 0006, 0042, 0044, 0094 | Hunks rejetés. Sans ces lignes, le composant s'active sans condition : `tonemap_d3d11` cassait la compilation Linux (`windows.h`) |
| 4 enregistrements (`ff_vf_scale_opencl` et son `OBJS-`, `OBJS-` de `mpeg4_rkmpp`, `ff_vf_transpose_d3d11`) | 0006, 0042, 0092 | Hunks rejetés. Sans erreur de compilation : `scale_opencl` manquait silencieusement au binaire jusqu'au contrôle de contrat |
| Doublon de `saturate_rintf` retiré de `cuda/vector_helpers.cuh` | 0003 | Upstream 9.0 a intégré l'helper de Jellyfin ; le patch l'ajoutait une seconde fois |
| `#include "libavcodec/itut35.h"` dans `bsf/av1_metadata.c` et `bsf/h265_metadata.c` | 0061 | Hunk rejeté |
| `amfenc_av1.c` : `ff_amf_pix_fmts` → `ff_amf_pix_fmts_10b_8b` | 0005 | Upstream a une liste unique, Jellyfin l'a scindée ; le hunk AV1 n'est pas passé |

Diff total : 8 fichiers, +14 / −24 lignes (annexe C). Deux comportements perdus ont été trouvés ensuite et corrigés (§3.7) : le diff passe à 10 fichiers, +48 / −31.

**Contrat.** L'inventaire de §3.2 relève 202 noms de composants dans le code serveur (filtres, codecs, BSF ; quelques faux positifs, comme `ulti`, fragment d'une regex de `VideoListResolver`). Comparaison du binaire porté au F0 de référence :
- **Aucun composant nommé par le serveur n'est perdu.** Ceux qui manquent aux deux binaires relèvent de backends que F0 ne compile pas (RKMPP, VideoToolbox, Vulkan, libplacebo) ou d'exclusions voulues (`libfdk_aac`, `aac_at`).
- Les 14 sondes d'option d'`EncoderValidator` (§3.2), rejouées par `ffmpeg -h filter=…` / `-h bsf=…`, donnent des résultats **identiques** : 13 positives, plus `overlay_vulkan` négative sur les deux binaires (Vulkan non compilé). Cette réponse négative montre aussi que la sonde sait dire non. Ce contrat de noms et de sondes ne voit pas les comportements : deux pertes lui ont échappé (§3.7).
- Pertes, toutes dues à des retraits upstream et non nommées par le serveur : codecs `sonic`, `sonicls`, `v308`, `v408`, `v410` et filtre `pp` (retrait de libpostproc en FFmpeg 8).
- Gains : notamment `vpp_amf`, `sr_amf`, `vp9_amf`, `pad_cuda`, BSF `dovi_split`.
- `EncoderValidator` lit la version (`^ffmpeg version n?((?:[0-9]+\.?)+)` donne 9.0.2). Sa table `_ffmpegMinimumLibraryVersions` cite encore `libpostproc`, qui n'existe plus. Elle ne sert que si la version est illisible, mais il faudra la nettoyer.

**Exécution (pas une preuve de fidélité).** Sur le VAAPI AMD de l'hôte (`/dev/dri/renderD128`) : `h264_vaapi` via `hwupload_vaapi` et `hevc_vaapi` en HLS fMP4 fonctionnent. En logiciel, `tonemapx` et `alphasrc` + `overlay` fonctionnent avec la syntaxe d'`EncodingHelper`. Mes premières commandes échouaient de la même façon sur la référence 7.1.4 : l'erreur venait de la ligne de commande, pas du portage.

**Écart `tonemapx`.** Sur une même source, la sortie brute de `tonemapx` (BT.2390, sans encodeur) diffère entre 9.0.2 et la référence 7.1.4 : PSNR 38,3 dB, SSIM 0,997. Source et conversion 10 bits sont identiques au bit près. Le code `tonemapx` de l'arbre porté est identique octet pour octet à celui de jellyfin-ffmpeg 8.1.3-1, et le patch `tonemapx` a changé entre 7.1.4-3 et 8.1.3-1. L'écart vient donc vraisemblablement de la série 8.1, et toucherait aussi l'option A2, plutôt que du passage en 9.0. Ce n'est pas prouvé au pixel : aucun binaire jellyfin 8.1.3 n'a été construit. Ce point relève du prototype HDR10 → SDR (§6.4), avec une référence externe.

**Ce que la compilation n'établit pas** : la correction des chemins CUDA, QSV et AMF (pas de banc), la fidélité des couleurs, le comportement Dolby Vision, linux-arm64 (le builder exige un hôte arm64 natif) et win-x64.

**Leçon pour tout portage de la série.** Un test hunk par hunk ne suffit pas. Il faut aussi contrôler que chaque ligne `configure` et chaque enregistrement (`allfilters.c`, `allcodecs.c`, `Makefile`) ajoutés par la série sont présents, puis comparer `-filters`, `-encoders`, `-decoders` et `-bsfs` à une référence. La perte de `scale_opencl` n'a été détectée qu'à ce dernier contrôle.

### 3.7 Inventaire des scénarios et contrat mesuré sur trois binaires

**Montage.** Trois binaires linux-x64 sortent du même pipeline F0 (§3.6) :
- **A1** : F0 actuel, jellyfin-ffmpeg 7.1.4-3 ;
- **C** : n9.0.2 avec la série 8.1.3-1 portée, corrections de l'annexe C comprises ;
- **B** : n9.0.2 vierge. Seules les deux assertions du builder « `tonemapx` et `alphasrc` présents » sont neutralisées, dans une copie de `ci/` hors dépôt.

A2 (jellyfin 8.1.3-1 sur n8.1.3) n'est pas construit. Quand le code concerné est identique à C, son résultat est déduit de C et marqué comme tel.

Le serveur choisit une voie à l'aide de **barrières** :
- des prédicats évalués sur ce que liste le binaire (`EncodingHelper.cs:302-363`) ;
- les 14 sondes d'option ;
- trois sondes d'exécution de `MediaEncoder` (`MediaEncoder.cs:237-239`) ;
- des seuils de version.

Une barrière fausse fait basculer sur une autre voie, en général sans erreur visible. Les instruments de l'annexe B rejouent chaque barrière comme le serveur la calcule. Chaque instrument a été contrôlé dans les deux sens : réponse positive sur A1 et C, négative sur B.

**Barrières (linux-x64)**

| Barrière | Code serveur | A1 | C | B | Patch 8.1.3-1 | Si elle est fausse |
|---|---|---|---|---|---|---|
| filtre `alphasrc` | `EncodingHelper.cs:4063, 4273, 4506, 5109, 5847, 6035` ; décodeurs `:6830, 6902, 6977, 7034, 7186` | oui | oui | **non** | 0023 | Les voies matérielles complètes (NVIDIA, AMD D3D11, Intel QSV, VAAPI) retombent toutes sur `GetSwVidFilterChain` : filtres et incrustation en logiciel, encodeur matériel au mieux derrière un `hwupload` |
| `IsSwTonemapAvailable` (`tonemapx`) | `:366` | oui | oui | **non** | 0053 | Aucun tonemap logiciel : un HDR transcodé hors voie matérielle sort sans conversion SDR |
| `IsVaapiFullSupported` | `:302` | oui | oui | **non** | 0014 (`hwupload_vaapi`) | Voie VAAPI complète indisponible. Elle exige aussi `IsOpenclFullSupported` (`:5103`) |
| `IsOpenclFullSupported` | `:323` | oui | oui | **non** | 0006, 0007, 0008 | Pas de tonemap OpenCL (VAAPI et D3D11) |
| `IsCudaFullSupported` | `:333` | oui | oui | **non** | 0004 (`tonemap_cuda`) | Voie CUDA complète indisponible |
| `IsVulkanFullSupported` | `:345` | non | non | non | 0082 et `libplacebo` | Indisponible partout : `libplacebo` n'est pas compilé (§3.1) |
| retrait HDR10+ | `:1507` | oui | oui | **non** | 0061 | Pas de remux pour un client DV qui refuse HDR10+. Le retrait DV reste possible via `dovi_rpu=strip` (upstream) |
| touche `p`/`u` | `MediaEncoder.cs:237`, `TranscodeManager.cs:956` | oui | oui | **non** | 0028 | Throttling désactivé (FFmpeg > 6.1) |
| `-only_first_vframe` | `MediaEncoder.cs:239, 639` | oui, 1 trame | oui, 1 trame après correction | **non** | 0066 | HDR10+ non détecté à l'analyse (`ProbeResultNormalizer.cs:954`) |
| pièces jointes MKV, nom avec espace | `AttachmentExtractor.cs:291` | 2/2 extraites | 2/2 | **0/2, ffmpeg en échec** | 0089 | Upstream 8+ refuse le premier nom non strict et abandonne l'entrée : aucune pièce jointe extraite, donc pas de polices pour l'incrustation ASS |

A2 : 0089 et 0066 sont nouveaux en 8.1. A2 a donc le comportement de C sur les deux dernières lignes. Ce n'est pas mesuré.

**Options écrites sans sonde.** Le contrôle porte sur deux ensembles :
- les 89 couples (filtre, option) qu'écrit `EncodingHelper` ;
- toutes les options listées par `-h full` : 5 553 pour C, 5 475 pour B.

Les 79 options propres à C sont toutes des options de filtres ou de BSF du fork, à deux exceptions près : `main10sp` de `hevc_qsv` et une option du décodeur AC-4. Le serveur n'utilise aucune option d'encodeur propre au fork. Trois options écrites ne sont protégées par aucune sonde :
- `sub2video` de `subtitles` (0022), écrit dans les voies matérielles qui incrustent via `alphasrc` ;
- `passthrough` de `vpp_qsv` (0019), dans la voie QSV ;
- `tonemap_mode` de `tonemap_opencl` et `tonemap_cuda` (0004, 0007). Elle n'est protégée que par les seuils de version `_minFFmpegOclCuTonemapMode` (5.1.3) et `_minFFmpegAdvancedTonemapMode` (7.0.1).

Sous B, ces options ne sont jamais atteintes, car les barrières ci-dessus sont fausses. Dans un C réduit, retirer le patch tout en gardant la voie fait échouer la commande. Un sous-ensemble doit donc garder ces paires ensemble : 0023 avec 0022, la voie QSV avec 0019, le tonemap matériel avec 0004 et 0007.

**Seuils de version.** `EncodingHelper` compte 17 seuils `_minFFmpeg*`, qui suivent la chronologie de jellyfin-ffmpeg. En 9.0.2, tous sont franchis. Un seul protège une option propre au fork (`tonemap_mode`) ; les autres protègent des options upstream. Ce seuil est à remplacer par une sonde, quelle que soit l'option retenue.

**Correction de §3.2.** Upstream, le framesync (« Action to take when encountering EOF ») n'existe que pour `overlay_vaapi` et `overlay_cuda`. Pour `overlay_opencl`, il vient de 0008 ; pour `overlay_vulkan`, de 0082. La version précédente de ce RFC avait lu l'arbre porté au lieu de l'arbre vierge. Conséquence : même avec `libplacebo` compilé, la voie Vulkan d'upstream ne passe pas `IsVulkanFullSupported` sans 0082.

**Correction de §3.6 : deux comportements perdus.** Le contrat de noms et de sondes était bien identique, mais le portage avait perdu deux comportements, sans erreur de compilation ni de sonde :
- **0066, hunk 7** : `-only_first_vframe` était accepté mais ne s'arrêtait plus. Mesuré : 25 trames décodées au lieu d'une sur un fichier d'une seconde. Comme le serveur ne sonde que le code de sortie, chaque analyse de bibliothèque aurait décodé le fichier entier.
- **0027, hunk 5** : le muxer MKV écrivait la configuration DV sans la valider. Corrigé, mais non testé faute d'échantillon DV.

Détection : pour chaque hunk de la série, l'instrument vérifie que ses lignes ajoutées sont présentes dans l'arbre (`lost-hunks.py`, annexe B). Il a été étalonné sur l'arbre jellyfin 8.1.3 complet, où il produit 21 faux positifs : des patches ultérieurs réécrivent ces lignes. Il détecte bien 0066 sur l'arbre d'avant correction. Après les deux corrections, il reste 27 écarts :
- **22 dans des backends que F0 ne compile pas**, à porter si la voie est retenue : Vulkan (0078 ×7, 0082 ×13), RKMPP (0042), VideoToolbox (0055) ;
- **5 faux positifs vérifiés à la main**, sur 4 patches : 0002 (`tex2D`) et 0083 (`nvenc`), absorbés upstream sous une autre forme ; 0005, dont le seuil AMF est remplacé par l'exigence upstream 1.5.2 ; 0061 (2 hunks), dont l'include est écrit sous un autre chemin.

L'annexe C est mise à jour : 10 fichiers, +48 / −31.

**Leçon, s'ajoutant à celle de §3.6.** Un portage se vérifie à quatre niveaux :
1. hunks ;
2. lignes `configure` et enregistrements ;
3. listes de composants et sondes ;
4. lignes ajoutées par chaque hunk.

Un hunk rejeté qui ne casse ni la compilation ni une sonde ne se voit qu'au quatrième niveau.

**Scénarios.** Chaque scénario est rattaché au code serveur qui le pilote et aux patches qui le portent. Le rattachement patch par patch, avec l'effet de chaque absence, est en annexe D.

| # | Scénario | Code serveur | Patches 8.1.3-1 (Linux) |
|---|---|---|---|
| S1 | Transcodage vidéo logiciel SDR | `GetSwVidFilterChain` (`:3924`) | aucun |
| S2 | HDR10 / HLG → SDR | `IsSwTonemapAvailable`, `GetHwTonemapFilter` (`:3762`), `GetLibplaceboFilter` (`:3843`) | 0053 ; en matériel : 0004, 0007, 0032, 0070, 0085, 0099 |
| S3 | Dolby Vision : profil, retrait, reshaping, remux | `ShouldRemoveDynamicHdrMetadata`, `GetBitStreamArgs` (`:1532`) | 0061, 0027 ; reshaping P5 : 0053, 0007, 0004 |
| S4 | Voie VAAPI complète | `GetVaapiVidFilterChain` (`:5088`) et ses variantes Intel/AMD | 0014, 0023, 0015, 0024, 0034, 0039, 0067, 0068, 0070, 0074, 0075, 0086 |
| S5 | Filtres et tonemap OpenCL | `IsOpenclFullSupported` (`:323`) | 0006, 0007, 0008, 0010 |
| S6 | Voie CUDA / NVENC | `GetNvidiaVidFilterChain` (`:4046`) | 0002, 0003, 0004, 0031, 0032, 0083 |
| S7 | Voie QSV | `GetIntelVidFilterChain` (`:4480`) | 0011, 0017–0020, 0036, 0037, 0059, 0064, 0071, 0099 |
| S8 | Voie Vulkan / libplacebo | `GetAmdVaapiFullVidFiltersPrefered` (`:5385`) | 0035, 0038, 0078–0082, 0085, plus `libplacebo` à compiler |
| S9 | Windows : D3D11, AMF, Media Foundation | voies D3D11 et AMF | 0005, 0009, 0012, 0013, 0030, 0033, 0040, 0072, 0076, 0091–0095, 0097 (audit séparé, §6) |
| S10 | Incrustation de sous-titres texte | `GetTextSubtitlesFilter` (`:1987`), `AttachmentExtractor` | 0022, 0023, 0065, 0089 |
| S11 | Incrustation de sous-titres image | `overlay_*` des voies matérielles | 0008, 0015, 0031, 0058, 0098 |
| S12 | HLS TS / fMP4 | `DynamicHlsController`, `GetHlsVideoKeyFrameArguments` | 0027, 0039, 0086 |
| S13 | Throttling | `TranscodingThrottler` | 0028 |
| S14 | Analyse (probe) | `MediaEncoder` (`:639`), `ProbeResultNormalizer` | 0066, 0087 (DTS:X) ; 0084 absorbé |
| S15 | Audio | `GetAudioEncoder` (`:782`), décodeurs | 0052, 0054 (AC-4) |
| S16 | Désentrelacement matériel | `GetHwDeinterlaceFilter` (`:3706`) | 0069 |
| S17 | Rotation matérielle | `transpose_*` | 0051 |
| S18 | Métadonnées et images | analyse des tags et des pièces jointes | 0025, 0088 |
| S19 | Extraction d'images, trickplay | `MediaEncoder` (`:918`), `GetMjpegEncoder` (`:245`) | 0096 |
| S20 | VideoToolbox | voies Apple | 14 patches, hors matrice |
| S21 | RKMPP | voies Rockchip | 0042, Docker arm64 seulement |

**Répartition des 100 patches.**

| Catégorie | Patches | Nombre |
|---|---|---:|
| Rattachés à un scénario serveur (Linux) | voir annexe D | 60 |
| Windows seul | 0009, 0012, 0013, 0030, 0033, 0040, 0072, 0076, 0091–0095, 0097 | 14 |
| VideoToolbox | 0043–0049, 0055–0057, 0060, 0062, 0063, 0073 | 14 |
| RKMPP | 0042 | 1 |
| Absorbés ou remplacés upstream | 0016, 0050, 0084, 0090 | 4 |
| Exclu (unsafe) | 0026 | 1 |
| Sans scénario | 0001, 0021, 0029, 0041, 0077, 0100 | 6 |

Le détail des six patches sans scénario :
- **0001** : correctif du muxer `segment`. Il est classé `required` (« HLS segmenting »), mais le serveur segmente avec `-f hls` et ne nomme jamais le muxer `segment`. Deuxième classement à revoir après 0051.
- **0021** et **0077** : journaux seulement.
- **0029** : performance.
- **0041** : icône Windows.
- **0100** : BSF `trim`, inutilisé (le trim audio passe par `noise=drop`).

Parmi les 60 patches rattachés à un scénario, **18 sont visibles par une barrière ou une option écrite** : 0003, 0004, 0006, 0007, 0008, 0014, 0019, 0022, 0023, 0028, 0031, 0051, 0053, 0054, 0061, 0066, 0069, 0089. Les 42 autres sont **comportementaux** : ils ne changent aucune barrière, et seul un test de scénario sur le backend concerné peut les valider. Le banc VAAPI AMD peut en exercer dix : 0015, 0024, 0034, 0039, 0067, 0068, 0074, 0075, 0086, 0096. Les patches d'interopérabilité Vulkan (0035, 0038, 0079, 0081) demandent d'abord de compiler la voie Vulkan.

**Dépendances.** Elles sont tracées de deux façons :
- par les symboles qu'un patch ajoute dans un en-tête et qu'un patch ultérieur utilise (`symdeps.py`, annexe B) ;
- par les échecs d'un patch testé seul sur sa base d'origine (§2).

Dépendances qui contraignent un sous-ensemble :
- **0053 (`tonemapx`) dépend de 0004 (CUDA)**, qui apporte les helpers DoVi de `colorspace.{c,h}` (`FFDOVIReshapeData`, `ff_map_dovi_metadata`…), et de 0007 (`tonemap_lut`). 0007 et 0094 (tonemap D3D11) dépendent des mêmes helpers. Garder `tonemapx` sans la voie CUDA oblige à scinder 0004.
- 0004 dépend de 0002 et 0003 (CUDA). 0092, 0094 et 0095 dépendent de 0091 (D3D11).
- L'exemple de l'issue, 0006/0007 → 0010, n'est pas une dépendance de symbole : 0010 modifie le comportement de `hwmap=reverse=1` à l'exécution, ce que seul un test révèle.
- Trois faux positifs ont été écartés : des helpers homonymes de noyaux OpenCL, CUDA et GLSL compilés séparément (0031 → 0008, 0082 → 0008, 0083 → 0069).

**Alternatives côté serveur pour B.**

| Barrière | Alternative upstream | État |
|---|---|---|
| `-only_first_vframe` | `-show_frames -select_streams v:0 -read_intervals "%+#1"` | Testé sur B : 1 trame. La présence de la side data HDR10+ n'est pas vérifiée faute d'échantillon |
| pièces jointes | extraction par index avec un nom explicite (`-dump_attachment:N <chemin>`), forme déjà utilisée par `AttachmentExtractor.cs:160, 422` | Testé sur B : 2/2 |
| `p`/`u` | aucune dans `ffmpeg`. `SIGSTOP`/`SIGCONT` sous Linux uniquement, ou `-readrate` | Non testé |
| `alphasrc` | source `color` à alpha nul et `format=yuva420p`. Le paramètre `start` d'`alphasrc` sert à la synchronisation après un seek | Hypothèse non testée |
| `hwupload_vaapi` | `hwupload` générique | Non testé |
| `tonemapx` | `zscale` + `tonemap` (sans reshaping DV, plus lent), ou `libplacebo` (Vulkan, chaîne de build différée) | Coût élevé |
| retrait HDR10+ | aucune ; repli sur un transcodage | — |

**Lecture.**
1. **B ne revient pas à « upstream plus quelques retouches serveur ».** Sur linux-x64, B coupe les quatre voies matérielles complètes et le tonemap logiciel. Il perd aussi le throttling, la détection et le retrait HDR10+, et l'extraction des polices dès qu'un nom contient un espace. Pour chaque barrière, il faut un patch ou un changement serveur.
2. **Deux changements serveur sont utiles quelle que soit l'option** : l'analyse par `-read_intervals` et l'extraction des pièces jointes par index. Ils suppriment la dépendance à 0066 et à 0089. 0089 relâche en outre un durcissement upstream. Les deux sont testés sur B ; le premier reste à vérifier sur un échantillon HDR10+.
3. **Le noyau d'un C Linux** se lit dans la colonne « Patch » des barrières : 0023, 0053 (avec 0004 scindé et 0007), 0014, 0006–0008, 0022, 0028 et 0061, plus 0069 et 0051 pour le désentrelacement et la rotation OpenCL. Les 42 patches comportementaux restent à trier par test de scénario. Sans banc, ils restent non validés.

## 4. Ce qui change déjà la discussion

1. **Le coût de l'option C est plus bas que redouté** si on part de la série 8.1.3-1 et non de 7.1.4-3 : 78 patches sur 100 passent tels quels sur n9.0.2, et l'arbre porté compile en linux-x64 avec 10 fichiers retouchés (+48 / −31 lignes), sans perte sur le contrat serveur (§3.6, §3.7). Reste à prouver : le comportement des 42 patches comportementaux, les autres plateformes et le coût des rebases futurs. Un portage se vérifie à quatre niveaux, pas seulement à la compilation (§3.7).
2. **Le périmètre des backends décide de la taille du fork.** Les patches lourds à porter correspondent à des backends (AMF, Vulkan, CUDA) qu'aucun banc ne valide aujourd'hui. Seul AMD VAAPI est testable.
3. **Les écarts entre livraisons sont un problème indépendant du fork.** Docker et natif n'exposent pas les mêmes capacités, et l'image Docker embarque `libfdk_aac` avec `--enable-gpl` (#342).
4. **L'option B est mesurée, et elle coupe beaucoup plus que des filtres** (§3.7). Sur linux-x64, sans changement serveur, elle désactive les quatre voies matérielles complètes (barrière `alphasrc`), le tonemap logiciel, le throttling, la détection et le retrait HDR10+. Elle fait aussi échouer l'extraction des pièces jointes d'un MKV dès qu'un nom contient un espace. B demande donc, pour chaque barrière, un changement serveur dont plusieurs n'ont pas d'équivalent upstream (`tonemapx`, retrait HDR10+, pause).
5. **Le fork utile est petit, mais il n'est pas découpable au hasard.** 18 patches sont visibles par une barrière ou une option écrite, 42 sont comportementaux, et 40 n'ont aucun scénario linux-x64 (Windows, VideoToolbox, RKMPP, absorbés, sans usage). Les dépendances imposent des paires : `tonemapx` exige de scinder 0004 (CUDA), et `sub2video` doit suivre `alphasrc`.
6. **Deux changements serveur sont utiles quelle que soit l'option** : l'analyse par `-read_intervals "%+#1"` à la place de `-only_first_vframe`, et l'extraction des pièces jointes par index. Tous deux sont testés sur B. Le seuil de version qui protège `tonemap_mode` est à remplacer par une sonde.

## 5. Questions ouvertes

- Seuil chiffré de fidélité des couleurs (ΔE/SSIM) et profils DV testés (5, 7, 8.1 proposés). À fixer avant les mesures.
- Périmètre des backends par plateforme (§4.2).
- Traitement de la question `libfdk_aac` de l'image Docker (§3.1) : issue #342.
- Classements de `fork-patches.json` à corriger quelle que soit l'option : 0051 (`useful`, alors que le serveur le sonde) et 0001 (`required`, alors que le muxer `segment` n'est pas utilisé). Ce sont des numéros 8.1.3-1 ; la correspondance avec 7.1.4-3 est en annexe A.
- Les deux changements serveur de §4.6 : issue dédiée, ou partie de l'option retenue ?

## 6. Étapes suivantes

1. **Compilation.** linux-x64 : faite (§3.6). Restent linux-arm64, qui demande un hôte arm64 natif (le builder refuse la cross-compilation et l'émulation), et win-x64 (étape 3).
2. **Inventaire des scénarios** : fait pour linux-x64 (§3.7, annexe D). Restent les 42 patches comportementaux, qui ne se trient que par test de scénario (étape 4), et la vérification de l'alternative `-read_intervals` sur un échantillon HDR10+.
3. **Audit Windows séparé**, puisque `win-x64` applique toute la série.
4. **Prototype** des cinq cas de l'issue, avec les critères chiffrés une fois fixés.

## Annexe A — Série 8.1.3-1 sur n9.0.2, patch par patch

« Seul » : patch testé isolément sur n9.0.2 vierge. « Séquence » : hunks en échec quand la série est appliquée dans l'ordre. Classement 7.1.4-3 : décision actuelle de `fork-patches.json` (numéro 7.1.4-3 entre parenthèses), rapprochée par le nom du patch.

| Patch (8.1.3-1) | Hunks | Seul sur n9.0.2 : OK / déjà présent | Échecs en séquence | Statut mécanique | Classement 7.1.4-3 | Composant nommé par le serveur |
|---|---:|---|---:|---|---|---|
| `0001-add-fixes-for-segment-muxer` | 3 | 3 / 0 | 0 | intact | required (0001) |  |
| `0002-update-cuda-func-header` | 5 | 4 / 0 | 1 | à porter | required (0002) |  |
| `0003-add-enhanced-cuda-pixfmt-converter-impl` | 50 | 49 / 6 | 1 | absorbé en partie | required (0003) |  |
| `0004-add-cuda-tonemap-impl` | 22 | 21 / 0 | 1 | à porter | required (0004) | tonemap_cuda |
| `0005-add-amf-fixes-from-upstream-and-custom-tunings` | 34 | 15 / 13 | 19 | à porter | required (0005) |  |
| `0006-add-opencl-scaler-and-pixfmt-converter-impl` | 6 | 3 / 0 | 3 | à porter | required (0006) | scale_opencl |
| `0007-add-bt2390-eetf-and-code-refactor-to-opencl-tonemap` | 28 | 28 / 1 | 0 | intact | required (0007) |  |
| `0008-add-pgs-support-to-opencl-overlay` | 16 | 16 / 0 | 0 | intact | required (0008) | alpha_format |
| `0009-add-d3d11-opencl-interop-for-amd` | 17 | 17 / 1 | 0 | intact | irrelevant (0009) |  |
| `0010-add-a-hack-for-opencl-reverse-mapping` | 13 | 13 / 0 | 0 | intact | required (0010) |  |
| `0011-add-fixes-for-mapping-from-qsv-source-device` | 11 | 11 / 1 | 0 | intact | required (0011) |  |
| `0012-add-d3d11-opencl-interop-for-qsv` | 6 | 5 / 0 | 0 | intact | irrelevant (0012) |  |
| `0013-add-vendor-id-option-for-d3d11` | 1 | 1 / 0 | 0 | intact | irrelevant (0013) |  |
| `0014-add-vaapi-hwupload-filter` | 4 | 4 / 0 | 0 | intact | required (0014) | hwupload_vaapi |
| `0015-check-alpha-mode-for-vaapi-overlay-filter` | 4 | 4 / 1 | 0 | intact | nouveau en 8.1 |  |
| `0016-add-fixes-for-nvdec-exceed-32-surfaces-error` | 3 | 0 / 3 | 3 | absorbé | required (0016) |  |
| `0017-backport-fixes-for-qsv-from-upstream` | 5 | 5 / 0 | 0 | intact | required (0018) |  |
| `0018-backport-fixes-for-qsv-from-cartwheel` | 16 | 16 / 0 | 0 | intact | required (0019) |  |
| `0019-add-fixes-for-qsv-vpp-filters` | 24 | 24 / 3 | 0 | intact | required (0020) |  |
| `0020-add-external-frame-support-for-qsv-enc` | 6 | 6 / 0 | 0 | intact | required (0021) |  |
| `0021-suppress-overlay-warning-logs` | 1 | 1 / 0 | 0 | intact | useful (0022) |  |
| `0022-add-sub2video-option-to-subtitles-filter` | 20 | 19 / 0 | 1 | à porter | required (0024) |  |
| `0023-add-alphasrc-source-video-filter` | 3 | 3 / 0 | 0 | intact | required (0025) | alphasrc |
| `0024-increase-the-default-async-depth-for-vaapi-encode` | 1 | 1 / 0 | 0 | intact | useful (0026) |  |
| `0025-add-multiple-values-tags-and-webp-support-for-id3v2` | 1 | 1 / 0 | 0 | intact | useful (0028) |  |
| `0026-remove-fdk-aac-from-nonfree` | 2 | 2 / 0 | 0 | intact | unsafe (0029) |  |
| `0027-pass-dovi-sidedata-to-hlsenc-and-mpegtsenc` | 12 | 11 / 0 | 1 | à porter | required (0030) |  |
| `0028-add-pause-support-for-ffmpeg-cli` | 9 | 9 / 0 | 0 | intact | irrelevant (0031) |  |
| `0029-enable-gcc-vectorization-and-fix-cpuflags` | 5 | 5 / 0 | 0 | intact | useful (0032) |  |
| `0030-tune-dxva-align-for-intel-to-avoid-copy-on-qsv` | 5 | 5 / 0 | 0 | intact | irrelevant (0033) |  |
| `0031-add-10bit-support-for-cuda-overlay` | 14 | 14 / 1 | 0 | intact | required (0034) | alpha_format |
| `0032-add-hdr-metadata-for-nvenc-hevc-encoder` | 2 | 2 / 0 | 0 | intact | required (0035) |  |
| `0033-add-d3d11va-dxva2-hevc-422-444-decoding-on-intel` | 21 | 21 / 0 | 0 | intact | irrelevant (0036) |  |
| `0034-default-vaapi-scaler-algorithm-to-fast` | 1 | 1 / 0 | 0 | intact | useful (0037) |  |
| `0035-dont-fail-on-vaapi-to-drm-export-vasync` | 1 | 1 / 0 | 0 | intact | required (0038) |  |
| `0036-skip-loading-plugins-on-vpl-runtime` | 1 | 1 / 0 | 0 | intact | required (0039) |  |
| `0037-tune-the-default-bf-for-qsv-enc` | 3 | 3 / 0 | 0 | intact | useful (0040) |  |
| `0038-allow-vaapi-import-drm-prime2-planar-formats` | 5 | 5 / 1 | 0 | intact | required (0041) |  |
| `0039-add-vui-info-to-the-seq-header-of-hevc-vaapi-encoder` | 2 | 2 / 0 | 0 | intact | required (0042) |  |
| `0040-sync-intel-d3d11va-textures-before-mapping-to-opencl` | 13 | 11 / 0 | 0 | intact | irrelevant (0043) |  |
| `0041-add-icon-for-windows-version-ffmpeg` | 2 | 2 / 0 | 0 | intact | irrelevant (0044) |  |
| `0042-add-full-hwa-pipeline-for-rockchip-rk3588-platform` | 80 | 76 / 0 | 3 | à porter | irrelevant (0046) | av1_rkmpp, mjpeg_rkmpp, mpeg1_rkmpp, mpeg2_rkmpp, mpeg4_rkmpp, overlay_rkrga, scale_rkrga, vpp_rkrga |
| `0043-add-format-option-to-vt-scale-filter` | 4 | 4 / 0 | 0 | intact | irrelevant (0047) |  |
| `0044-add-vf-overlay-videotoolbox-filter` | 7 | 6 / 0 | 1 | à porter | irrelevant (0048) | overlay_videotoolbox |
| `0045-add-coreimage-based-vf-transpose-vt-filter` | 3 | 2 / 0 | 1 | à porter | irrelevant (0049) |  |
| `0046-add-vf-tonemap-videotoolbox-filter` | 5 | 5 / 0 | 0 | intact | irrelevant (0050) | tonemap_videotoolbox |
| `0047-add-mjpeg-videotoolbox-encoder` | 9 | 9 / 0 | 0 | intact | irrelevant (0051) | mjpeg_videotoolbox |
| `0048-add-vt-low-priority-keyframe-decoding` | 3 | 3 / 0 | 0 | intact | irrelevant (0052) |  |
| `0049-add-dummy-device-derive-to-videotoolbox-hwcontext` | 2 | 2 / 0 | 0 | intact | irrelevant (0053) |  |
| `0050-add-cuda-transpose-filter-impl` | 5 | 0 / 1 | 5 | remplacé upstream | useful (0054) |  |
| `0051-add-flip-feat-to-opencl-transpose-filter` | 5 | 5 / 0 | 0 | intact | useful (0055) |  |
| `0052-opus-allow-5point1-side-inputs` | 1 | 1 / 0 | 0 | intact | required (0056) |  |
| `0053-add-simd-optimized-tonemapx-filter` | 20 | 18 / 0 | 0 | intact | required (0057) | tonemapx |
| `0054-add-ac4-decoder-for-atsc-3-0` | 17 | 17 / 1 | 0 | intact | required (0058) | ac4 |
| `0055-allow-vt-sw-decoder-for-every-codec` | 1 | 0 / 0 | 1 | à porter | irrelevant (0066) |  |
| `0056-add-bwdif-videotoolbox-filter` | 10 | 10 / 0 | 0 | intact | irrelevant (0067) | bwdif_videotoolbox |
| `0057-add-12bit-decoding-on-videotoolbox` | 2 | 2 / 0 | 0 | intact | irrelevant (0068) |  |
| `0058-fix-the-sub2video-perf-regressions` | 2 | 2 / 0 | 0 | intact | required (0069) |  |
| `0059-allow-vpl-qsv-to-init-with-the-legacy-msdk-path` | 14 | 14 / 0 | 0 | intact | required (0070) |  |
| `0060-alway-set-videotoolboxenc-pixel-buffer-info` | 2 | 2 / 0 | 0 | intact | irrelevant (0071) |  |
| `0061-add-remove-dovi-hdr10plus-bsf` | 17 | 15 / 0 | 2 | à porter | required (0072) | remove_dovi/remove_hdr10plus |
| `0062-fix-atenc-layout-samplerate` | 4 | 4 / 0 | 0 | intact | irrelevant (0073) |  |
| `0063-videotoolbox-remove-opengl-compatability` | 1 | 1 / 0 | 0 | intact | irrelevant (0074) |  |
| `0064-use-dynamic-pool-for-vpl-qsv-hwupload` | 3 | 3 / 0 | 0 | intact | required (0075) |  |
| `0065-fix-ass-incorrect-null-copy` | 1 | 1 / 0 | 0 | intact | required (0077) |  |
| `0066-add-first-vframe-only-to-ffprobe` | 8 | 7 / 0 | 1 | à porter | useful (0079) |  |
| `0067-fix-auto-inserting-sw-color-conv-filters-between-hw-fmts` | 1 | 1 / 0 | 0 | intact | required (0080) |  |
| `0068-fix-dummy-hw-frame-missing-hw-frames-ctx` | 1 | 1 / 0 | 0 | intact | required (0081) |  |
| `0069-add-yadif-and-bwdif-opencl-filter-impl` | 18 | 15 / 0 | 0 | intact | required (0082) | bwdif_opencl, yadif_opencl |
| `0070-allow-vpp-tonemap-without-hdr10-metadata` | 8 | 8 / 1 | 0 | intact | required (0083) |  |
| `0071-allow-qsvenc-to-bail-out-from-unsupported-low-power-mode` | 1 | 1 / 0 | 0 | intact | required (0084) |  |
| `0072-add-array-of-textures-support-for-d3d11va-hwaccel` | 11 | 8 / 0 | 0 | intact | irrelevant (0086) |  |
| `0073-dont-return-ext-error-for-invalid-vt-frame` | 1 | 1 / 0 | 0 | intact | irrelevant (0090) |  |
| `0074-fix-mapped-hwframe-to-swframe-swscale-conversion` | 2 | 2 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0075-fix-hwupload-filter-cannot-use-devices-created-by-hwaccel` | 3 | 3 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0076-fix-seeking-h264-open-gop-videos-with-d3d11va-on-amd` | 1 | 1 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0077-suppress-the-resumed-reading-at-pts-with-rate-warning` | 1 | 1 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0078-backport-fixes-for-vulkan-from-upstream` | 67 | 32 / 29 | 35 | à porter | useful (0060) |  |
| `0079-add-fixes-for-vaapi-drm-prime-vulkan-interop` | 37 | 37 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0080-prefer-vulkan-device-with-higher-api-version` | 2 | 2 / 0 | 0 | intact | useful (0063) |  |
| `0081-signal-vulkan-semaphores-in-drm-prime-reverse-mapping` | 4 | 4 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0082-add-fixes-and-enhancements-for-vulkan-filters` | 23 | 10 / 0 | 13 | à porter | nouveau en 8.1 |  |
| `0083-backport-fixes-for-cuda-from-upstream` | 19 | 9 / 8 | 10 | à porter | nouveau en 8.1 |  |
| `0084-backport-fixes-for-detecting-ac4-streams-in-mpegts` | 3 | 0 / 3 | 3 | absorbé | nouveau en 8.1 |  |
| `0085-fix-hdr10-sidedata-not-removed-in-libplacebo-outlink` | 1 | 1 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0086-fix-amd-hevc-vaapi-encoder-extradata-with-overrides` | 7 | 7 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0087-add-dtsx-detect-for-dts-hd-hra` | 8 | 8 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0088-add-webp-to-matroskadec-image-mime-types` | 1 | 1 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0089-relax-to-allow-safe-filenames-in-mkv-attachments` | 3 | 3 / 0 | 0 | intact | required (0095) |  |
| `0090-backport-a-fix-to-use-sw-pix-fmt-in-codec-par-if-set` | 1 | 0 / 1 | 1 | absorbé | nouveau en 8.1 |  |
| `0091-add-d3d11-deinterlace-filter` | 13 | 13 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0092-add-d3d11-video-processor-overlay-transpose-filters` | 12 | 9 / 0 | 1 | à porter | nouveau en 8.1 |  |
| `0093-fix-d3d11-scale-visible-source-rect` | 2 | 2 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0094-add-d3d11-tonemap-filter` | 7 | 3 / 0 | 1 | à porter | nouveau en 8.1 |  |
| `0095-add-scale-d3d11-p010-shader-fallback` | 16 | 13 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0096-fix-full-range-input-for-mjpeg-vaapi-encoder` | 1 | 1 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0097-add-misc-enhancements-to-mfenc-hw-encoder` | 30 | 30 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0098-backport-a-fix-to-not-stall-sub2video-on-stream-eof` | 2 | 2 / 0 | 0 | intact | nouveau en 8.1 |  |
| `0099-fix-qsv-av1-decoder-exporting-hdr-side-data` | 4 | 4 / 1 | 0 | intact | nouveau en 8.1 |  |
| `0100-backport-trim-bitstream-filter` | 4 | 4 / 0 | 0 | intact | nouveau en 8.1 |  |

## Annexe B — Instrument

```bash
#!/usr/bin/env bash
# hunks.sh <tree> <patchdir> <out.tsv>
# Per-patch hunk status of each patch of <patchdir>/series against a pristine <tree>,
# each patch tested alone: ok (applies forward, fuzz allowed), rev (already present),
# else neither. Output: patch, hunks, ok, rev, files.
set -eu  # no pipefail: grep finding nothing is a normal count of 0
tree=$1 dir=$2 out=$3
: > "$out"
cd "$tree"
grep -v '^#' "$dir/series" | while read -r p; do
  [ -z "$p" ] && continue
  total=$(grep -c '^@@ ' "$dir/$p" || true)
  fwd=$(patch -p1 -N --dry-run -F2 < "$dir/$p" 2>&1 || true)
  revo=$(patch -p1 -R -f --dry-run -F2 < "$dir/$p" 2>&1 || true)
  # forward: a hunk is ok unless FAILED/ignored; a new file that already exists counts as failed
  # patch prints "N out of M hunk(s) FAILED|ignored" per file; sum N
  ffail=$(grep -oE '^[0-9]+ out of [0-9]+ hunks? (FAILED|ignored)' <<<"$fwd" | awk '{s+=$1} END{print s+0}')
  rfail=$(grep -oE '^[0-9]+ out of [0-9]+ hunks? (FAILED|ignored)' <<<"$revo" | awk '{s+=$1} END{print s+0}')
  ok=$(( total - ffail < 0 ? 0 : total - ffail ))
  rev=$(( total - rfail < 0 ? 0 : total - rfail ))
  files=$(sed -nE 's#^\+\+\+ [^/]+/([^[:space:]]+).*#\1#p' "$dir/$p" | sort -u | paste -sd, -)
  printf '%s\t%s\t%s\t%s\t%s\n' "$p" "$total" "$ok" "$rev" "$files" >> "$out"
done
```

Usage : `hunks.sh <arbre FFmpeg vierge> <dossier debian/patches> <sortie.tsv>`. Les dossiers de patches sont extraits par `git archive <tag> debian/patches` du dépôt jellyfin-ffmpeg.

Instruments de §3.6 et §3.7, conservés dans le rig d'audit (non publiés) :

| Script | Ce qu'il rejoue | Contrôle |
|---|---|---|
| `build-port.sh` | pipeline F0 avec une source FFmpeg substituée (C, ou B avec `REPO_MOUNT` sur une copie de `ci/` sans les assertions du fork) | le builder a vérifié `tonemapx` et `alphasrc` sur C, et a signalé leur absence sur B |
| `gates.sh` | prédicats `Is*FullSupported`, `alphasrc`, `tonemapx` (`EncodingHelper.cs:302-366`) | oui sur A1 et C, non sur B |
| `probe-options.sh` | les 14 sondes d'option d'`EncoderValidator` | `overlay_vulkan` non partout, 13 oui sur A1 et C |
| `check-options.sh` | les 89 couples (filtre, option) écrits par `EncodingHelper` contre `-h filter=` | 23 écarts sur B, 5 (Vulkan non compilé) sur A1 et C. `extra_hw_frames` est écarté : c'est une option générique d'`AVFilterContext`, absente de `-h filter=` |
| `runtime-probes.sh` | sondes `p`, `-only_first_vframe`, `low_priority` de `MediaEncoder` | oui sur A1 et C, non sur B. La sonde `p` exige un stdin ouvert, comme celui du serveur ; avec un stdin fermé, elle répond non à tort |
| `attach-test.sh` | `AttachmentExtractor.cs:291` sur un MKV dont la première pièce jointe s'appelle `Font One.ttf` | 2/2 sur A1 et C, échec sur B |
| `lost-hunks.py` | lignes ajoutées par chaque hunk présentes dans l'arbre | 21 faux positifs sur l'arbre jellyfin 8.1.3 complet ; détecte 0066 sur l'arbre porté avant correction |
| `symdeps.py` | symboles ajoutés à un en-tête par un patch et utilisés par un patch ultérieur | faux positifs relus à la main (§3.7) |
| `filter-options.py`, `chain-components.py` | extraction des options et des composants nommés par méthode d'`EncodingHelper` | relu à la main pour les noms composés (`tonemap_{0}`, `{scale,vpp}_{suffixe}`) et les fragments ajoutés (`":alpha=1"`…) |

## Annexe C — Corrections manuelles du portage (§3.6)

Appliquées après la série 8.1.3-1 en séquence (fuzz 2, rejets écartés) sur n9.0.2, puis le retrait de 0026. L'arbre porté vit dans le rig d'audit, il n'est pas publié. Deux blocs : les corrections de compilation et de contrat (§3.6), puis les comportements perdus trouvés en §3.7.

```diff
diff --git a/configure b/configure
index e46cd5a8bf..5ef591f65a 100755
--- a/configure
+++ b/configure
@@ -3744,6 +3744,8 @@ vp9_mediacodec_decoder_deps="mediacodec"
 vp9_mediacodec_encoder_deps="mediacodec"
 vp9_qsv_decoder_select="qsvdec"
 vp9_rkmpp_decoder_deps="rkmpp"
+mpeg4_rkmpp_decoder_deps="rkmpp"
+mpeg4_rkmpp_decoder_select="mpeg4_unpack_bframes_bsf dump_extradata_bsf"
 vp9_vaapi_encoder_deps="VAEncPictureParameterBufferVP9"
 vp9_vaapi_encoder_select="vaapi_encode"
 vp9_qsv_encoder_deps="libmfx MFX_CODEC_VP9"
@@ -4315,6 +4317,11 @@ tonemapx_filter_deps="const_nan"
 tonemap_vaapi_filter_deps="vaapi VAProcFilterParameterBufferHDRToneMapping"
 tonemap_videotoolbox_filter_deps="metal corevideo videotoolbox const_nan"
 tonemap_opencl_filter_deps="opencl const_nan"
+tonemap_cuda_filter_deps="ffnvcodec const_nan"
+tonemap_cuda_filter_deps_any="cuda_nvcc cuda_llvm"
+scale_opencl_filter_deps="opencl const_nan"
+tonemap_d3d11_filter_deps="d3d11va const_nan d3dcompiler_h"
+overlay_videotoolbox_filter_deps="metal corevideo coreimage videotoolbox"
 transpose_opencl_filter_deps="opencl"
 transpose_vaapi_filter_deps="vaapi VAProcPipelineCaps_rotation_flags"
 transpose_vt_filter_deps="videotoolbox VTPixelRotationSessionCreate"
diff --git a/libavcodec/Makefile b/libavcodec/Makefile
index 525c560843..1255d91f24 100644
--- a/libavcodec/Makefile
+++ b/libavcodec/Makefile
@@ -1099,6 +1099,7 @@ OBJS-$(CONFIG_MPEG4_NVDEC_HWACCEL)        += nvdec_mpeg4.o
 OBJS-$(CONFIG_MPEG4_VAAPI_HWACCEL)        += vaapi_mpeg4.o
 OBJS-$(CONFIG_MPEG4_VDPAU_HWACCEL)        += vdpau_mpeg4.o
 OBJS-$(CONFIG_MPEG4_VIDEOTOOLBOX_HWACCEL) += videotoolbox.o
+OBJS-$(CONFIG_MPEG4_RKMPP_DECODER)     += rkmppdec.o
 OBJS-$(CONFIG_VC1_D3D11VA_HWACCEL)        += dxva2_vc1.o
 OBJS-$(CONFIG_VC1_DXVA2_HWACCEL)          += dxva2_vc1.o
 OBJS-$(CONFIG_VC1_D3D12VA_HWACCEL)        += dxva2_vc1.o d3d12va_vc1.o
diff --git a/libavcodec/amfenc_av1.c b/libavcodec/amfenc_av1.c
index 47326a1499..91da4b2178 100644
--- a/libavcodec/amfenc_av1.c
+++ b/libavcodec/amfenc_av1.c
@@ -751,7 +751,7 @@ const FFCodec ff_av1_amf_encoder = {
     .p.capabilities   = AV_CODEC_CAP_DELAY | AV_CODEC_CAP_HARDWARE |
                       AV_CODEC_CAP_DR1,
     .caps_internal  = FF_CODEC_CAP_INIT_CLEANUP,
-    CODEC_PIXFMTS_ARRAY(ff_amf_pix_fmts),
+    CODEC_PIXFMTS_ARRAY(ff_amf_pix_fmts_10b_8b),
     .color_ranges   = AVCOL_RANGE_MPEG | AVCOL_RANGE_JPEG,
     .p.wrapper_name   = "amf",
     .hw_configs     = ff_amfenc_hw_configs,
diff --git a/libavcodec/bsf/av1_metadata.c b/libavcodec/bsf/av1_metadata.c
index 22274b2988..9fd8333792 100644
--- a/libavcodec/bsf/av1_metadata.c
+++ b/libavcodec/bsf/av1_metadata.c
@@ -24,6 +24,7 @@
 #include "libavcodec/bsf_internal.h"
 #include "libavcodec/cbs.h"
 #include "libavcodec/cbs_bsf.h"
+#include "libavcodec/itut35.h"
 #include "libavcodec/cbs_av1.h"
 
 typedef struct AV1MetadataContext {
diff --git a/libavcodec/bsf/h265_metadata.c b/libavcodec/bsf/h265_metadata.c
index 9590097e72..08887a6c0b 100644
--- a/libavcodec/bsf/h265_metadata.c
+++ b/libavcodec/bsf/h265_metadata.c
@@ -23,6 +23,7 @@
 #include "libavcodec/bsf_internal.h"
 #include "libavcodec/cbs.h"
 #include "libavcodec/cbs_bsf.h"
+#include "libavcodec/itut35.h"
 #include "libavcodec/cbs_h265.h"
 #include "libavcodec/h2645data.h"
 #include "libavcodec/h265_profile_level.h"
diff --git a/libavfilter/Makefile b/libavfilter/Makefile
index 10d0feb6df..25e25d8246 100644
--- a/libavfilter/Makefile
+++ b/libavfilter/Makefile
@@ -489,6 +489,7 @@ OBJS-$(CONFIG_SCALE_D3D11_FILTER)            += vf_scale_d3d11.o scale_eval.o d3
 OBJS-$(CONFIG_SCALE_D3D12_FILTER)            += vf_scale_d3d12.o scale_eval.o
 OBJS-$(CONFIG_SCALE_CUDA_FILTER)             += vf_scale_cuda.o scale_eval.o \
                                                 vf_scale_cuda.ptx.o cuda/load_helper.o
+OBJS-$(CONFIG_SCALE_OPENCL_FILTER)           += vf_scale_opencl.o opencl.o opencl/scale.o scale_eval.o
 OBJS-$(CONFIG_SCALE_QSV_FILTER)              += vf_vpp_qsv.o
 OBJS-$(CONFIG_SCALE_RKRGA_FILTER)            += vf_vpp_rkrga.o scale_eval.o
 OBJS-$(CONFIG_SCALE_VAAPI_FILTER)            += vf_scale_vaapi.o scale_eval.o vaapi_vpp.o
diff --git a/libavfilter/allfilters.c b/libavfilter/allfilters.c
index 3685c3ffdc..73d1a0f97e 100644
--- a/libavfilter/allfilters.c
+++ b/libavfilter/allfilters.c
@@ -452,6 +452,7 @@ extern const FFFilter ff_vf_vpp_amf;
 extern const FFFilter ff_vf_sr_amf;
 extern const FFFilter ff_vf_frc_amf;
 extern const FFFilter ff_vf_scale_cuda;
+extern const FFFilter ff_vf_scale_opencl;
 extern const FFFilter ff_vf_scale_d3d11;
 extern const FFFilter ff_vf_scale_d3d12;
 extern const FFFilter ff_vf_scale_qsv;
@@ -525,6 +526,7 @@ extern const FFFilter ff_vf_tpad;
 extern const FFFilter ff_vf_transpose;
 extern const FFFilter ff_vf_transpose_cuda;
 extern const FFFilter ff_vf_transpose_opencl;
+extern const FFFilter ff_vf_transpose_d3d11;
 extern const FFFilter ff_vf_transpose_vaapi;
 extern const FFFilter ff_vf_transpose_vt;
 extern const FFFilter ff_vf_transpose_vulkan;
diff --git a/libavfilter/cuda/vector_helpers.cuh b/libavfilter/cuda/vector_helpers.cuh
index e4a9353ff1..ca8246e5a0 100644
--- a/libavfilter/cuda/vector_helpers.cuh
+++ b/libavfilter/cuda/vector_helpers.cuh
@@ -132,27 +132,4 @@ inline __device__ float4 saturate_rintf<float4>(float4 a, float factor) {
     );
 }
 
-template<typename T>
-inline __device__ T saturate_rintf(T a, float factor) {
-    return rintf(__saturatef(a) * factor);
-}
-
-template<>
-inline __device__ float2 saturate_rintf<float2>(float2 a, float factor) {
-    return make_float2(
-        saturate_rintf(a.x, factor),
-        saturate_rintf(a.y, factor)
-    );
-}
-
-template<>
-inline __device__ float4 saturate_rintf<float4>(float4 a, float factor) {
-    return make_float4(
-        saturate_rintf(a.x, factor),
-        saturate_rintf(a.y, factor),
-        saturate_rintf(a.z, factor),
-        saturate_rintf(a.w, factor)
-    );
-}
-
 #endif
```

Comportements perdus (§3.7) :

```diff
diff --git a/fftools/ffprobe.c b/fftools/ffprobe.c
index a113b71cae..b696d41f6e 100644
--- a/fftools/ffprobe.c
+++ b/fftools/ffprobe.c
@@ -1860,23 +1860,49 @@ static int read_interval_packets(AVTextFormatContext *tfc, InputFile *ifile,
                 fd->pkt_pos  = pkt->pos;
                 fd->pkt_size = pkt->size;
 
-                while (process_frame(tfc, ifile, frame, pkt, &packet_new) > 0);
+                while (1) {
+                    int frame_ret = process_frame(tfc, ifile, frame, pkt, &packet_new);
+
+                    if (frame_ret <= 0)
+                        break;
+                    if (!only_show_first_video_frame)
+                        continue;
+                    if (++decode_iterations >= ONLY_FIRST_VFRAME_MAX_DECODE_ITERATIONS) {
+                        av_log(NULL, AV_LOG_WARNING,
+                               "Stopping only_first_vframe after %d decoder iterations "
+                               "for stream %d on a single packet\n",
+                               decode_iterations, pkt->stream_index);
+                        break;
+                    }
+                }
+                if (only_show_first_video_frame &&
+                    nb_streams_frames[pkt->stream_index] > frame_count_before) {
+                    finished_video_stream[pkt->stream_index] = 1;
+                    nb_finished_video_streams++;
+                    if (nb_finished_video_streams >= nb_video_streams)
+                        break;
+                }
             }
         }
         av_packet_unref(pkt);
     }
     av_packet_unref(pkt);
-    //Flush remaining frames that are cached in the decoder
-    for (int i = 0; i < ifile->nb_streams; i++) {
-        pkt->stream_index = i;
-        if (do_read_frames) {
-            while (process_frame(tfc, ifile, frame, pkt, &(int){1}) > 0);
+    if (!only_show_first_video_frame) {
+        //Flush remaining frames that are cached in the decoder
+        for (int i = 0; i < ifile->nb_streams; i++) {
+            pkt->stream_index = i;
+            if (do_read_frames) {
+                while (process_frame(tfc, ifile, frame, pkt, &(int){1}) > 0);
+            }
         }
     }
 
 end:
     av_frame_free(&frame);
     av_packet_free(&pkt);
+    av_freep(&finished_video_stream);
+    av_freep(&target_video_stream);
+    av_freep(&video_packets);
     if (ret < 0) {
         av_log(NULL, AV_LOG_ERROR, "Could not read packets in interval ");
         log_read_interval(interval, NULL, AV_LOG_ERROR);
diff --git a/libavformat/matroskaenc.c b/libavformat/matroskaenc.c
index f278271e24..f0e03dfcc5 100644
--- a/libavformat/matroskaenc.c
+++ b/libavformat/matroskaenc.c
@@ -1760,7 +1760,8 @@ static void mkv_write_blockadditionmapping(AVFormatContext *s, const MatroskaMux
 
     const AVDOVIDecoderConfigurationRecord *dovi =
         (const AVDOVIDecoderConfigurationRecord *)sd->data;
-    if (dovi->dv_profile <= 10) {
+    if (dovi->dv_profile <= 10 &&
+        !ff_isom_validate_dovi_config(dovi, par, (int)par->codec_tag)) {
         ebml_master mapping;
         uint8_t buf[ISOM_DVCC_DVVC_SIZE];
         uint32_t type;
```

## Annexe D — Série 8.1.3-1 rattachée aux scénarios (§3.7)

Scénarios S1–S21 : §3.7. « Sans lui » : ce que le serveur perd si le patch manque, observé sur B quand une barrière le rend visible, déduit du code sinon. « Dépend de » : dépendances de symboles et de contexte (§3.7). Plateformes : L = linux-x64 et linux-arm64, W = win-x64, D = image Docker seulement ; « — » = aucune.

| Patch | Scénarios | Sans lui (option B), vu du serveur | Dépend de | Plateformes |
|---|---|---|---|---|
| `0001-add-fixes-for-segment-muxer` | — | aucun : le serveur n'utilise pas le muxer `segment` (HLS = `-f hls`) | — | — |
| `0002-update-cuda-func-header` | S6 | base des filtres CUDA | — | L W |
| `0003-add-enhanced-cuda-pixfmt-converter-impl` | S6 | `scale_cuda` sans `format` : `IsCudaFullSupported` faux, repli copie système | 0002 | L W |
| `0004-add-cuda-tonemap-impl` | S6 S2 S3 S5 | `tonemap_cuda` absent ; porte aussi les helpers DoVi de `colorspace.{c,h}` dont 0007, 0053, 0094 dépendent | 0002 0003 | L W |
| `0005-add-amf-fixes-from-upstream-and-custom-tunings` | S9 | réglages AMF (comportement) ; non testable | — | W L(x64) |
| `0006-add-opencl-scaler-and-pixfmt-converter-impl` | S5 | `scale_opencl` absent : `IsOpenclFullSupported` faux, pas de tonemap OpenCL | — | L W |
| `0007-add-bt2390-eetf-and-code-refactor-to-opencl-tonemap` | S5 S3 | `tonemap_opencl` sans `bt2390` ni reshaping DoVi : `IsOpenclFullSupported` faux | 0004 | L W |
| `0008-add-pgs-support-to-opencl-overlay` | S11 S5 | `overlay_opencl` sans `alpha_format` : PGS incrusté avec alpha faux sur la voie OpenCL | — | L W |
| `0009-add-d3d11-opencl-interop-for-amd` | S9 | interop D3D11↔OpenCL AMD | — | W |
| `0010-add-a-hack-for-opencl-reverse-mapping` | S5 S7 | `hwmap=reverse=1` (D3D11/VAAPI→OpenCL→retour) : comportement | — | L W |
| `0011-add-fixes-for-mapping-from-qsv-source-device` | S7 | dérivation de périphérique depuis QSV | — | L W |
| `0012-add-d3d11-opencl-interop-for-qsv` | S7 S9 | interop D3D11↔OpenCL Intel | — | W |
| `0013-add-vendor-id-option-for-d3d11` | S9 | option `vendor_id` D3D11 | — | W |
| `0014-add-vaapi-hwupload-filter` | S4 | `hwupload_vaapi` absent : `IsVaapiFullSupported` faux, toute la voie VAAPI complète tombe | — | L |
| `0015-check-alpha-mode-for-vaapi-overlay-filter` | S4 S11 | vérification du mode alpha d'`overlay_vaapi` (comportement) | — | L |
| `0016-add-fixes-for-nvdec-exceed-32-surfaces-error` | S6 | absorbé upstream | — | — |
| `0017-backport-fixes-for-qsv-from-upstream` | S7 | backports QSV (comportement) | — | L W |
| `0018-backport-fixes-for-qsv-from-cartwheel` | S7 | backports QSV cartwheel (comportement) | — | L W |
| `0019-add-fixes-for-qsv-vpp-filters` | S7 | correctifs filtres VPP QSV (comportement) | — | L W |
| `0020-add-external-frame-support-for-qsv-enc` | S7 | trames externes pour l'encodeur QSV (comportement) | — | L W |
| `0021-suppress-overlay-warning-logs` | — | journaux d'`overlay` seulement | — | — |
| `0022-add-sub2video-option-to-subtitles-filter` | S10 | `subtitles` sans `sub2video` : écrit sans sonde (`EncodingHelper.cs:1997`) dans les voies matérielles qui incrustent via `alphasrc` ; avec 0023 et sans 0022, la commande échoue | — | L W |
| `0023-add-alphasrc-source-video-filter` | S4 S5 S6 S7 S10 | `alphasrc` absent : barrière de **toutes** les voies matérielles complètes (`Get*VidFilterChain`, décodeurs `hwSurface`), repli copie système partout | — | L W |
| `0024-increase-the-default-async-depth-for-vaapi-encode` | S4 | `async_depth` VAAPI par défaut (performance) | — | L |
| `0025-add-multiple-values-tags-and-webp-support-for-id3v2` | S18 | tags id3v2 multivaleurs et pochettes WebP | — | L W |
| `0026-remove-fdk-aac-from-nonfree` | — | retrait de libfdk_aac de nonfree : **unsafe**, exclu (= 0029 de 7.1.4-3) | — | — |
| `0027-pass-dovi-sidedata-to-hlsenc-and-mpegtsenc` | S12 S3 | side data DoVi non transmise à hlsenc/mpegtsenc : remux DV en HLS perd la signalisation | — | L W |
| `0028-add-pause-support-for-ffmpeg-cli` | S13 | pas de touche `p`/`u` : `IsPkeyPauseSupported` faux, throttling désactivé (FFmpeg > 6.1) | — | L W |
| `0029-enable-gcc-vectorization-and-fix-cpuflags` | — | vectorisation gcc et cpuflags (performance) | — | L W |
| `0030-tune-dxva-align-for-intel-to-avoid-copy-on-qsv` | S7 | alignement DXVA Intel | — | W |
| `0031-add-10bit-support-for-cuda-overlay` | S6 S11 | `overlay_cuda` 10 bits et `alpha_format` absents | — | L W |
| `0032-add-hdr-metadata-for-nvenc-hevc-encoder` | S6 S2 | métadonnées HDR dans NVENC HEVC | — | L W |
| `0033-add-d3d11va-dxva2-hevc-422-444-decoding-on-intel` | S9 | décodage HEVC 4:2:2/4:4:4 D3D11/DXVA Intel | — | W |
| `0034-default-vaapi-scaler-algorithm-to-fast` | S4 | algorithme par défaut de `scale_vaapi` = rapide (performance) | — | L |
| `0035-dont-fail-on-vaapi-to-drm-export-vasync` | S8 | export VAAPI→DRM sans `vaSyncSurface` en échec | — | L |
| `0036-skip-loading-plugins-on-vpl-runtime` | S7 | chargement des plugins VPL | — | L W |
| `0037-tune-the-default-bf-for-qsv-enc` | S7 | `bf` par défaut QSV | — | L W |
| `0038-allow-vaapi-import-drm-prime2-planar-formats` | S8 | import DRM PRIME2 planaire dans VAAPI | — | L |
| `0039-add-vui-info-to-the-seq-header-of-hevc-vaapi-encoder` | S12 S4 | VUI dans l'en-tête HEVC VAAPI | — | L |
| `0040-sync-intel-d3d11va-textures-before-mapping-to-opencl` | S7 S9 | synchronisation D3D11 Intel avant OpenCL | — | W |
| `0041-add-icon-for-windows-version-ffmpeg` | — | icône Windows | — | W |
| `0042-add-full-hwa-pipeline-for-rockchip-rk3588-platform` | S21 | RKMPP/RKRGA | — | D(arm64) |
| `0043-add-format-option-to-vt-scale-filter` | S20 | VideoToolbox | — | — |
| `0044-add-vf-overlay-videotoolbox-filter` | S20 | VideoToolbox | — | — |
| `0045-add-coreimage-based-vf-transpose-vt-filter` | S20 | VideoToolbox | — | — |
| `0046-add-vf-tonemap-videotoolbox-filter` | S20 | VideoToolbox | 0004 | — |
| `0047-add-mjpeg-videotoolbox-encoder` | S20 | VideoToolbox | — | — |
| `0048-add-vt-low-priority-keyframe-decoding` | S20 | VideoToolbox (`low_priority`) | — | — |
| `0049-add-dummy-device-derive-to-videotoolbox-hwcontext` | S20 | VideoToolbox | — | — |
| `0050-add-cuda-transpose-filter-impl` | S6 | remplacé upstream (`vf_transpose_cuda.c`) | — | — |
| `0051-add-flip-feat-to-opencl-transpose-filter` | S17 | `transpose_opencl` sans demi-tour : sonde `TransposeOpenclReversal` fausse | — | L W |
| `0052-opus-allow-5point1-side-inputs` | S15 | opus 5.1 « side » refusé par libopus | — | L W |
| `0053-add-simd-optimized-tonemapx-filter` | S2 S3 | `tonemapx` absent : `IsSwTonemapAvailable` faux, **aucun tonemap logiciel** (HDR servi sans conversion) ; reshaping DV P5 logiciel absent | 0004 0007 | L W |
| `0054-add-ac4-decoder-for-atsc-3-0` | S15 | décodeur AC-4 absent | — | L W |
| `0055-allow-vt-sw-decoder-for-every-codec` | S20 | VideoToolbox | — | — |
| `0056-add-bwdif-videotoolbox-filter` | S20 | VideoToolbox | — | — |
| `0057-add-12bit-decoding-on-videotoolbox` | S20 | VideoToolbox | — | — |
| `0058-fix-the-sub2video-perf-regressions` | S11 | régression de performance sub2video (PGS) | — | L W |
| `0059-allow-vpl-qsv-to-init-with-the-legacy-msdk-path` | S7 | init VPL par le chemin MSDK historique | — | L W |
| `0060-alway-set-videotoolboxenc-pixel-buffer-info` | S20 | VideoToolbox | — | — |
| `0061-add-remove-dovi-hdr10plus-bsf` | S3 | `remove_dovi`/`remove_hdr10plus` absents : retrait DV possible par `dovi_rpu=strip`, retrait **HDR10+ impossible** | — | L W |
| `0062-fix-atenc-layout-samplerate` | S20 | AudioToolbox | — | — |
| `0063-videotoolbox-remove-opengl-compatability` | S20 | VideoToolbox | — | — |
| `0064-use-dynamic-pool-for-vpl-qsv-hwupload` | S7 | pool dynamique pour `hwupload` VPL | — | L W |
| `0065-fix-ass-incorrect-null-copy` | S10 | copie nulle dans le muxer ASS | — | L W |
| `0066-add-first-vframe-only-to-ffprobe` | S14 | pas de `-only_first_vframe` : probe sans trame, **HDR10+ non détecté** (`ProbeResultNormalizer.cs:954`) | — | L W |
| `0067-fix-auto-inserting-sw-color-conv-filters-between-hw-fmts` | S4 S6 S7 | conversion couleur logicielle insérée entre formats matériels (CLI) | — | L W |
| `0068-fix-dummy-hw-frame-missing-hw-frames-ctx` | S4 S6 S7 | trame matérielle factice sans `hw_frames_ctx` (CLI) | — | L W |
| `0069-add-yadif-and-bwdif-opencl-filter-impl` | S16 | `yadif_opencl`/`bwdif_opencl` absents | — | L W |
| `0070-allow-vpp-tonemap-without-hdr10-metadata` | S4 S2 | `tonemap_vaapi` exige les métadonnées HDR10 | — | L |
| `0071-allow-qsvenc-to-bail-out-from-unsupported-low-power-mode` | S7 | repli QSV hors mode basse consommation | — | L W |
| `0072-add-array-of-textures-support-for-d3d11va-hwaccel` | S9 | array of textures D3D11VA | — | W |
| `0073-dont-return-ext-error-for-invalid-vt-frame` | S20 | VideoToolbox | — | — |
| `0074-fix-mapped-hwframe-to-swframe-swscale-conversion` | S4 S6 S7 | conversion swscale des trames matérielles mappées | — | L W |
| `0075-fix-hwupload-filter-cannot-use-devices-created-by-hwaccel` | S4 S6 S7 | `hwupload` avec un périphérique créé par hwaccel | — | L W |
| `0076-fix-seeking-h264-open-gop-videos-with-d3d11va-on-amd` | S9 | seek H.264 open-GOP D3D11VA AMD | — | W |
| `0077-suppress-the-resumed-reading-at-pts-with-rate-warning` | — | journaux de demux seulement | — | — |
| `0078-backport-fixes-for-vulkan-from-upstream` | S8 | backports Vulkan | — | L W |
| `0079-add-fixes-for-vaapi-drm-prime-vulkan-interop` | S8 | interop VAAPI DRM PRIME↔Vulkan | — | L |
| `0080-prefer-vulkan-device-with-higher-api-version` | S8 | choix du périphérique Vulkan | — | L W |
| `0081-signal-vulkan-semaphores-in-drm-prime-reverse-mapping` | S8 | sémaphores Vulkan au mappage inverse DRM PRIME | — | L |
| `0082-add-fixes-and-enhancements-for-vulkan-filters` | S8 | correctifs filtres Vulkan | — | L W |
| `0083-backport-fixes-for-cuda-from-upstream` | S6 | backports CUDA | — | L W |
| `0084-backport-fixes-for-detecting-ac4-streams-in-mpegts` | S14 | absorbé upstream | — | — |
| `0085-fix-hdr10-sidedata-not-removed-in-libplacebo-outlink` | S8 S2 | side data HDR10 non retirée en sortie de libplacebo | — | L W |
| `0086-fix-amd-hevc-vaapi-encoder-extradata-with-overrides` | S12 S4 | extradata HEVC VAAPI AMD (fMP4) | — | L |
| `0087-add-dtsx-detect-for-dts-hd-hra` | S14 | DTS:X non détecté dans DTS-HD HRA | — | L W |
| `0088-add-webp-to-matroskadec-image-mime-types` | S18 | WebP absent des types MIME d'image MKV | — | L W |
| `0089-relax-to-allow-safe-filenames-in-mkv-attachments` | S10 | `-dump_attachment:t ""` (`AttachmentExtractor.cs:291`) : upstream 8+ arrête l'extraction au premier nom non strict (espace, accent…) ; les pièces jointes suivantes, dont les polices de l'ASS, ne sont pas extraites | — | L W |
| `0090-backport-a-fix-to-use-sw-pix-fmt-in-codec-par-if-set` | — | absorbé upstream | — | — |
| `0091-add-d3d11-deinterlace-filter` | S9 S16 | désentrelacement D3D11 | — | W |
| `0092-add-d3d11-video-processor-overlay-transpose-filters` | S9 | overlay/transpose D3D11 | 0091 | W |
| `0093-fix-d3d11-scale-visible-source-rect` | S9 | `scale_d3d11` rectangle source | — | W |
| `0094-add-d3d11-tonemap-filter` | S9 S2 | tonemap D3D11 | 0004 0091 | W |
| `0095-add-scale-d3d11-p010-shader-fallback` | S9 | repli shader P010 de `scale_d3d11` | 0091 | W |
| `0096-fix-full-range-input-for-mjpeg-vaapi-encoder` | S19 | MJPEG VAAPI plage complète | — | L |
| `0097-add-misc-enhancements-to-mfenc-hw-encoder` | S9 | encodeur Media Foundation | — | W |
| `0098-backport-a-fix-to-not-stall-sub2video-on-stream-eof` | S11 | sub2video bloqué en fin de flux | — | L W |
| `0099-fix-qsv-av1-decoder-exporting-hdr-side-data` | S7 S2 | side data HDR du décodeur AV1 QSV | — | L W |
| `0100-backport-trim-bitstream-filter` | — | BSF `trim` : le serveur ne le nomme pas (`noise=drop` pour le trim audio) | — | — |
