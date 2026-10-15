# Lumi Core

Package UPM réutilisable de Lumivale Studio, `com.lumivale.lumi-core` **0.1.0**.
Version cible : **Unity 6000.3.13f1**. Le package fournit sept modules techniques
indépendants des scènes, assets et règles propres au jeu. La version reste candidate
à une validation de release.

## Guides V0.1

- [Installation, composition et exemples minimaux](Documentation~/GETTING_STARTED.md).
- [Mise à jour, identités de données et future installation Git](Documentation~/UPGRADING.md).
- [Notes de release proposées 0.1.0](Documentation~/RELEASE_NOTES_0.1.0.md).

## Installation locale

Placer le dépôt dans un dossier `lumi-core`, voisin du projet Unity. Dans Package
Manager, choisir **Install package from disk** et sélectionner son `package.json`.
Pour cette disposition, `Packages/manifest.json` du projet contient :

```json
"com.lumivale.lumi-core": "file:../../lumi-core"
```

Le chemin relatif est résolu depuis le dossier `Packages` du projet.
Le package déclare **com.unity.inputsystem 1.19.0** et **com.unity.ugui 2.0.0**,
déjà installés dans le projet de référence. Les tests utilisent son Unity Test Framework.

## Architecture et périmètre

| Module | État | Dépendances actuelles |
| --- | --- | --- |
| Bootstrap | Implémenté | UnityEngine pour l'hôte ; logique de coordination en C# |
| Scene Flow | Implémenté | Bootstrap, UnityEngine.SceneManagement |
| Input | Implémenté | Bootstrap, Unity.InputSystem |
| Settings | Implémenté | Bootstrap, UnityEngine |
| Audio | Implémenté | Bootstrap, UnityEngine.Audio |
| UI | Implémenté | Bootstrap, Input, Unity.InputSystem, UnityEngine.UI |
| Persistence | Implémenté | Bootstrap, JsonUtility, System.IO |

Les sept modules ont leurs propres assemblies. Bootstrap ne référence aucun autre
module ; Scene Flow et Input restent indépendants de UI. UI déclare explicitement Input.
Settings et Audio ne se référencent pas ; le projet compose leur synchronisation.
Persistence reste indépendant de Settings et de tous les autres modules fonctionnels.

## Persistence

`PersistenceService` gère plusieurs slots de gameplay dans un dossier explicitement
identifié par jeu sous `Application.persistentDataPath`. `SaveSchema<T>` définit
le modèle, sa version et un validateur propres au consommateur. Save, Load, Delete,
Exists et List sont asynchrones avec des résultats diagnostiques distincts.

Les snapshots sont sérialisés sur le thread principal ; les opérations disque
sont exécutées sur workers et sérialisées par service. L'arrêt annule les travaux
avant commit, attend les écritures engagées et libère le verrou du dossier.
Le temporaire est flushé puis remplacé par les opérations natives ; un backup
permet la récupération sans promouvoir un brouillon interrompu. Les versions
incompatibles et les fichiers corrompus sont préservés jusqu'à suppression explicite.

L'API, les garanties Windows réellement testées et les limites JsonUtility sont
documentées dans [Runtime/Persistence/README.md](Runtime/Persistence/README.md).
Le Starter fournit trois slots et un DTO cube/compteur, hors du Core.

Le package possède ses tests unitaires EditMode et PlayMode. Le projet `Lumi-start` possède les
assemblies de démonstration, d'outillage Editor et de tests d'intégration.
La composition propre à un jeu reste dans son projet, hors du package.

## Bootstrap

`ILumiService` expose un `Id`, des identifiants de `Dependencies`, `InitializeAsync`
et `ShutdownAsync`. `ServiceContext.Get<T>()` retrouve le service unique correspondant
au type demandé et refuse les types absents ou ambigus.

`BootstrapCoordinator` valide tout le graphe avant de démarrer un service : identifiants
vides ou dupliqués, dépendances manquantes, auto-dépendances et cycles sont refusés.
Le tri topologique choisit à chaque étape le premier service enregistré dont les
dépendances sont satisfaites. L'ordre est ainsi déterministe.

```csharp
using Lumivale.Core.Bootstrap;
using Lumivale.Core.SceneFlow;

var coordinator = new BootstrapCoordinator(new ILumiService[]
{
    new SceneFlowService()
});
await coordinator.InitializeAsync(cancellationToken);
var scenes = coordinator.Context.Get<SceneFlowService>();
await scenes.LoadAsync(new SceneLoadRequest("Assets/Scenes/Level.unity"), cancellationToken);
await coordinator.ShutdownAsync();
```

Les appels d'initialisation en cours partagent une seule tâche : le jeton du premier
appel contrôle ce démarrage. Une erreur ou annulation entraîne l'arrêt en ordre inverse,
y compris du service partiellement initialisé. L'arrêt tente tous les services et
agrège les erreurs. Un arrêt pendant le démarrage l'annule et attend son rollback.
Un jeton d'arrêt déjà annulé refuse l'arrêt avant toute mutation ; après engagement de
l'arrêt, le nettoyage se poursuit avec un jeton non annulable.

Un coordinateur possède **un seul cycle de vie**. Après `Stopped` ou `Faulted`, créez
un nouveau coordinateur au lieu de réinitialiser les mêmes services implicitement.

Pour une composition Unity, dérivez `LumiBootstrap` et surchargez `CreateServices()`.
L'hôte doit être sur un GameObject racine. Il persiste via `DontDestroyOnLoad`, expose
`Ready`, `IsReady`, `Coordinator` et un arrêt explicite awaitable. Un deuxième hôte
n'initialise aucun service et détruit son propre composant. Les autres composants
du GameObject dupliqué restent intacts. Si l'hôte précédent a été détruit pendant un
arrêt asynchrone, son remplacement attend cet arrêt avant d'initialiser ses services.
Une erreur de cet arrêt empêche le démarrage du remplacement.

## Scene Flow

`SceneFlowService.LoadAsync(SceneLoadRequest, CancellationToken)` charge une scène
présente dans la liste de scènes du profil de build. Préférez un chemin complet
`Assets/.../*.unity` pour éviter les noms ambigus. `Single` est le mode par défaut ;
`Additive` est aussi accepté, sans changer implicitement la scène active.

- `ProgressChanged` : progression monotone entre 0 et 1 pour chaque chargement accepté.
- `LoadCompleted` : notification unique après chargement et transition réussis.
- `LoadFailed` : requête, exception et code précis (`Busy`, `SceneUnavailable`, etc.).
- `IsLoading` : verrou maintenu pendant le chargement, la transition et le nettoyage.
- `ObserverFailed` : erreurs des abonnés isolées du résultat de navigation.

Une requête concurrente est refusée immédiatement. Aucun chargement n'est mis en file.
Les hooks `ISceneTransition.BeforeLoadAsync`, `AfterLoadAsync` et `RecoverAsync`
permettent de fournir une présentation spécifique au projet. Les implémentations
incluses sont immédiates ou utilisent des délais configurables en temps réel :

```csharp
var transition = new DelaySceneTransition(
    System.TimeSpan.FromMilliseconds(150),
    System.TimeSpan.FromMilliseconds(150));
await scenes.LoadAsync(new SceneLoadRequest("Assets/Scenes/Level.unity",
    transition: transition), cancellationToken);
```

La composition du projet peut utiliser ces hooks pour suspendre Input et fermer UI.
Scene Flow reste indépendant de ces modules.
`ISceneLoader` et `ISceneLoadOperation` permettent des adaptateurs et des tests isolés.
L'adaptateur inclus appelle réellement `SceneManager.LoadSceneAsync` et attend les
frames Unity avec `Awaitable.NextFrameAsync`.

## Input et UI

Input fournit aussi `BeginRebind(actionPath, bindingId, controlsPath, finished)` :
une capture par service, suspension des maps pendant la capture, annulation Esc/B,
timeout natif et nettoyage à l'arrêt. `ExportBindingOverrides`,
`ImportBindingOverrides` et `ResetBindingOverrides` manipulent les chemins du clone,
avec validation avant mutation. Le jeu choisit les bindings autorisés, les règles
de collision et le stockage. Voir [le contrat Input](Runtime/Input/README.md).
La présentation des bindings et leur stockage appartiennent au projet consommateur.

`InputService` reçoit un `InputActionAsset`, les maps associées aux trois contextes
`Gameplay`, `UI` et `Pause`, et un contexte initial. Il clone l'asset, valide les
références avant mutation, gère `SetContext`, les leases imbriquées `Suspend()` et les
tokens `Subscribe(...)`. L'arrêt retire les callbacks et détruit le clone ; les
anciens tokens ne peuvent pas affecter un nouveau cycle. Les projets définissent
librement leurs actions et bindings clavier/souris/manette.

`UiService` enregistre des `UiScreen` uGUI et expose `Open`, `Close`, `Back`,
`OpenModal`, `CloseModal` et `CloseAll`. Il gère l'historique, les modales et la
restauration de sélection via EventSystem. Les vues restent actives : leur CanvasGroup
contrôle visibilité, interaction et raycasts. Les registrations sont disposables.

`PauseService` dépend d'Input et UI. Il ouvre une modale Pause, mémorise le contexte
et restaure cet état à la reprise, fermeture, désenregistrement ou arrêt. Son option
`controlTimeScale` vaut **false** par défaut ; lorsqu'elle est activée, elle restaure
exactement la valeur précédente. UI fonctionne avec le temps non affecté par la pause.

`InputSystemUiBridge.Bind(input, paths)` relie un InputSystemUIInputModule au clone
du service, avec six chemins d'actions UI configurables. Le bridge possède exclusivement
ces six actions et son module pendant le binding ; aucun second module ne doit utiliser
les mêmes actions. Il retire ses callbacks et références sur `Unbind`/destruction/arrêt.
Il active `wantsInitialStateCheck` sur l'action Point du clone et réapplique son premier
binding à l'identique via l'API publique pour reconstruire le cache Input System 1.19.
Un pointeur immobile conserve ainsi sa position après un changement de scène ; l'asset
source reste intact.

Les contrats détaillés et exemples sont dans [Runtime/Input/README.md](Runtime/Input/README.md)
et [Runtime/UI/README.md](Runtime/UI/README.md). Le package ne fournit aucun style,
Canvas, EventSystem, prefab ni contrôleur de jeu.

## Settings et Audio

`SettingsService` reçoit des défauts, une clé de préférences et éventuellement des
adaptateurs de stockage et de capacités graphiques. Il charge un document JSON
versionné dans PlayerPrefs, valide les valeurs et expose des snapshots indépendants.
`Update` et `ResetDefaults` modifient la mémoire ; `ApplyGraphics` applique explicitement
résolution, mode et qualité par nom ; `Save` enregistre explicitement les préférences.
Un document invalide ou d'une version inconnue est préservé jusqu'au prochain Save.
Les langues sont des codes préférés, sans traduction ou module Localization.

`AudioService` reçoit le mixer, ses groupes Master/Music/SFX, trois paramètres exposés
et un plafond de sources SFX. Il possède une source musicale persistante et un pool
2D/3D borné. Les volumes normalisés sont convertis en dB ; zéro mute les sources
possédées, sans écraser leur gain individuel. Les fondus utilisent le temps non
affecté par la pause ; la dernière demande musicale remplace la précédente.
Les clips doivent être préchargés. L'arrêt libère les clips, sources et overrides du mixer.

Le Starter charge Settings, initialise Audio, puis synchronise les volumes avant
de lancer la musique. Les sons et mixers appartiennent au projet consommateur.
Les contrats, le stockage et les limites natives sont détaillés dans
[Runtime/Settings/README.md](Runtime/Settings/README.md) et
[Runtime/Audio/README.md](Runtime/Audio/README.md).

## Limites et règles d'utilisation

- Appelez le cycle de vie et Scene Flow depuis le thread principal Unity ; aucun
  `Wait()`, `.Result` bloquant ou `ConfigureAwait(false)` autour de leurs appels.
- L'annulation avant le démarrage Unity évite le chargement. Une fois démarré, le
  chargement moteur est laissé jusqu'à l'activation avant de libérer le verrou ; la
  destination peut donc être active alors que la tâche est annulée.
- L'arrêt de Scene Flow annule la requête et attend son nettoyage avant de terminer.
- Les hooks et services personnalisés doivent terminer leur nettoyage. Aucun timeout
  ne libère artificiellement le verrou pendant qu'une opération Unity est encore active.
- Un service ne doit pas attendre les tâches de démarrage ou d'arrêt de son propre
  coordinateur pendant son callback de cycle de vie : cela créerait une dépendance circulaire.
- Un hook de transition ne doit pas attendre le chargement ou l'arrêt de son propre
  Scene Flow : celui-ci attend déjà le hook. Utilisez les événements terminaux pour
  enchaîner une navigation, une fois le verrou libéré.
- Les événements retiennent fortement leurs abonnés. Un composant de scène doit
  se désabonner dans `OnDisable` ou `OnDestroy`, auprès du service auquel il s'est
  abonné. Conservez les delegates nécessaires pour retirer les lambdas. Bootstrap
  et Scene Flow ne suppriment pas automatiquement leurs abonnements à l'arrêt.
  Input retire les callbacks créés par `Subscribe` et ses observers de cycle de vie ;
  UI libère ses registrations et observers à l'arrêt. Les abonnements directs aux
  actions obtenues par `GetAction` restent à la charge de leur propriétaire.
- Un adaptateur `ISceneLoadOperation` doit maintenir l'accès à son état, l'activation
  et l'attente de frames jusqu'à sa fin, même après annulation. Une exception dans
  ce contrat pendant le drainage empêche de garantir la sérialisation du moteur.
- `OnDestroy` et `OnApplicationQuit` lancent un arrêt coopératif ; le moteur ne garantit
  pas l'achèvement asynchrone lors de la fermeture du processus. Pour un arrêt ordonné,
  attendez explicitement `ShutdownAsync()` avant de quitter.
- Les événements de fin sont émis après la libération du verrou, une fois l'opération,
  la transition et le nettoyage terminés. Ils peuvent lancer la navigation suivante.
  Les notifications d'échec imbriquées sont limitées pour empêcher une récursion de retries.

## Tests et projet de référence

Le package possède ses propres tests sans dépendance aux assets du Starter :

- `Tests/Editor` : Bootstrap, Scene Flow et Settings en EditMode.
- `Tests/Runtime` : Input, rebinding, activité des périphériques et UI en PlayMode,
  avec des périphériques virtuels isolés via l'`InputTestFixture` officiel.
- `Tests/Audio/Editor` et `Tests/Audio/Runtime` : conversions, configuration et
  comportement Audio natif.
- `Tests/Persistence/Editor` : service, sérialisation et stockage dans des dossiers
  temporaires isolés.

Installer Unity Test Framework dans le projet consommateur et ajouter ce champ
au niveau racine de son `Packages/manifest.json` :

```json
"testables": ["com.lumivale.lumi-core"]
```

Après résolution UPM et compilation, ouvrir **Window > General > Test Runner**.
Choisir **EditMode** ou **PlayMode**, puis exécuter les suites concernées par la
modification. Les fixtures Audio natives utilisent l'Editor pour préparer leurs
assets. Une découverte de tests ne constitue pas un résultat d'exécution.

Le projet de référence `lumi-start` démarre dans
`Assets/Game/Scenes/GameMenu.unity`. Ses tests d'intégration se trouvent sous
`Assets/Game/Tests` : navigation Unity, UI/Input, sauvegardes et sessions répétées
sans Domain Reload. Ils restent dans le projet qui possède ces scènes et assets.
Les tests virtuels ne remplacent pas les essais sur la plateforme et les
périphériques physiques visés.

## Dépôts et reproductibilité

Le package et `Lumi-start` ont des dépôts Git indépendants. Conservez les sources,
tests et `.meta` ; les caches Unity et fichiers IDE sont ignorés. Clonez ou placez
les deux dépôts côte à côte, avec le dossier package nommé exactement `lumi-core`
sur les systèmes sensibles à la casse. Le manifeste du Starter pointe sur ce
dossier ; il ne contient pas une seconde copie des scripts du Core.

## Références Unity 6.3

- [Chemins UPM locaux](https://docs.unity3d.com/6000.3/Documentation/Manual/upm-localpath.html)
- [Tests d'un package UPM](https://docs.unity3d.com/6000.3/Documentation/Manual/cus-tests.html)
- [Activation des opérations de scène](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/AsyncOperation-allowSceneActivation.html)
