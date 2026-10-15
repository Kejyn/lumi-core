# Démarrer avec Lumi Core 0.1.0

Lumi Core fournit sept modules indépendants pour **Unity 6000.3.13f1**. Le jeu
possède sa composition, ses scènes, actions, vues, sons et modèles de sauvegarde.
Windows x64 est la première cible de validation. Ce guide décrit les API ;
chaque projet doit valider son Player, son stockage et ses périphériques cibles.

## Installer le package local

1. Placer le dépôt du package dans un dossier `lumi-core`, voisin du projet Unity.
2. Dans **Window > Package Management > Package Manager**, choisir **Install
   package from disk** et sélectionner `lumi-core/package.json`.
3. Attendre la résolution et la compilation. UPM doit afficher
   `com.lumivale.lumi-core` **0.1.0**, source **Local**.

Pour des dossiers voisins, le manifeste généré contient cette dépendance :

```json
"com.lumivale.lumi-core": "file:../../lumi-core"
```

Le chemin est relatif au dossier `Packages`. La casse `lumi-core` doit être exacte
sur un filesystem sensible à la casse. Ne copier aucun script du package dans
`Assets`. Une dépendance locale suit le contenu du dossier, sans verrouiller son
commit : conserver les commits du package et du jeu utilisés pour chaque livraison.
La [procédure UPM locale officielle](https://docs.unity3d.com/6000.3/Documentation/Manual/upm-ui-local.html)
décrit l'installation depuis `package.json`.

Le package déclare Input System **1.19.0** et uGUI **2.0.0**. Il n'impose ni URP,
ni TextMeshPro dans les vues du consommateur, ni dépendance au Starter. Activer
l'Input System dans les Player Settings du projet pour utiliser ses périphériques.
Les tests demandent séparément Unity Test Framework ; le Starter utilise **1.6.0**.

Si le jeu utilise une assembly definition, ajouter les références aux assemblies
des modules qu'il consomme. Éviter les références Editor dans son assembly runtime.

## Responsabilités et points d'entrée

| Assembly | Responsabilité | API principale | Configuration du jeu |
| --- | --- | --- | --- |
| `Lumivale.Core.Bootstrap` | Ordre, démarrage, rollback et arrêt des services | `ILumiService`, `LumiBootstrap`, `BootstrapCoordinator`, `ServiceContext` | Liste de services et dépendances par ID |
| `Lumivale.Core.SceneFlow` | Chargement Unity exclusif, transitions, événements | `SceneFlowService`, `SceneLoadRequest`, `ISceneTransition` | Chemins de scènes et présentation des transitions |
| `Lumivale.Core.Input` | Clone d'actions, contextes, suspensions, callbacks | `InputService`, `InputContext` | Asset, maps, bindings et contrôles du jeu |
| `Lumivale.Core.UI` | Navigation uGUI, modales, sélection, pause configurable | `UiService`, `UiScreen`, `PauseService`, `InputSystemUiBridge` | Canvas, EventSystem, vues et six actions UI |
| `Lumivale.Core.Settings` | Préférences validées, affichage et stockage explicites | `SettingsService`, `SettingsConfiguration`, `SettingsValues` | Défauts, clé PlayerPrefs, langues et capacités graphiques |
| `Lumivale.Core.Audio` | Musique, fondus non affectés par la pause et pool SFX borné | `AudioService`, `AudioConfiguration` | Mixer, groupes, paramètres exposés, clips préchargés et listener |
| `Lumivale.Core.Persistence` | Slots versionnés, diagnostics et stockage local | `PersistenceService`, `PersistenceConfiguration`, `SaveSchema<T>` | Identité du jeu, DTO, validation et migrations |

Bootstrap ne référence aucun autre module. Les modules fonctionnels référencent
Bootstrap ; UI référence aussi Input. Settings et Audio restent indépendants : leur
synchronisation appartient à la composition du jeu. Un service personnalisé déclare
les IDs qu'il utilise dans `Dependencies` avant de les résoudre via `ServiceContext`.

## Premier Bootstrap et navigation

Créer `GameBootstrap.cs` dans le projet consommateur, puis placer ce composant sur
un GameObject **racine dédié**, dans la scène de démarrage :

```csharp
using System.Collections.Generic;
using Lumivale.Core.Bootstrap;
using Lumivale.Core.SceneFlow;

public sealed class GameBootstrap : LumiBootstrap
{
    protected override IEnumerable<ILumiService> CreateServices()
    {
        yield return new SceneFlowService();
    }
}
```

Le Bootstrap persiste entre scènes. Depuis une méthode `async Task` du jeu, sur le
thread Unity, attendre sa disponibilité puis naviguer :

```csharp
await bootstrap.Ready;
var flow = bootstrap.Coordinator.Context.Get<SceneFlowService>();
await flow.LoadAsync(new SceneLoadRequest("Assets/MyGame/Scenes/Gameplay.unity"), token);
```

`bootstrap` désigne le `GameBootstrap` de la composition et `token` le jeton du
propriétaire de la demande. Ajouter la scène destination à la liste du build. Les
chargements concurrents sont refusés, sans file implicite. Une annulation après
départ Unity peut laisser la destination activée : attendre le drainage et adapter
la vue/contexte à la scène effectivement active. Les hooks de transition ne doivent
pas attendre leur propre Scene Flow ou son arrêt.

Le coordinateur a un seul cycle de vie. Après son arrêt ou un démarrage échoué,
créer une nouvelle composition. Pour quitter volontairement :

```csharp
await bootstrap.ShutdownAsync();
UnityEngine.Application.Quit();
```

Unity n'attend pas les callbacks asynchrones `OnDestroy`/`OnApplicationQuit`.
L'appelant doit attendre l'arrêt lorsqu'il exige une finalisation contrôlée.

## Ajouter Input et UI

Créer un `InputActionAsset` dans le jeu. Cet exemple définit deux maps `Gameplay`
et `UI`, plus une map commune :

```csharp
using System.Collections.Generic;
using Lumivale.Core.Input;
using Lumivale.Core.UI;

var input = new InputService(actions, new Dictionary<InputContext, string[]>
{
    [InputContext.Gameplay] = new[] { "Gameplay", "Common" },
    [InputContext.UI] = new[] { "UI", "Common" },
    [InputContext.Pause] = new[] { "UI", "Common" }
}, initialContext: InputContext.UI);
var ui = new UiService();
var pause = new PauseService("Pause", controlTimeScale: true);
// Add input, ui and pause to the services returned by CreateServices().
```

Le jeu possède `actions`. Toutes les maps citées doivent exister. Dans un Canvas,
créer un `UiScreen` avec son ID, CanvasGroup et premier Selectable. L'EventSystem
de la scène possède un `InputSystemUIInputModule` initialement désactivé, sans
références aux actions source, et un `InputSystemUiBridge` sur ce même objet.
Après `Ready`, la composition relie le clone et enregistre les vues :

```csharp
bridge.Bind(input, new UiActionPaths()); // UI/Navigate, Point, Click, Submit, Cancel, ScrollWheel
IDisposable registration = ui.Register(mainScreen);
input.SetContext(InputContext.UI);
ui.Open(mainScreen.Id);
// When cleaning up the view: registration.Dispose(); bridge.Unbind();
```

Ajouter `using System;` pour le token. Les six chemins sont configurables. Le bridge
possède exclusivement ces actions et ce module tant qu'il est lié. Le jeu dispose
les registrations et tokens de `input.Subscribe(...)` dans `OnDisable`/`OnDestroy`.
Les abonnements directs à `GetAction` doivent être retirés par leur propriétaire.
Différer les changements de contexte demandés par Pause/Cancel jusqu'à `Update`,
après la chaîne native Input System. Les animations de vues en pause utilisent un
temps non affecté par `Time.timeScale`. [Contrat Input](../Runtime/Input/README.md)
et [contrat UI](../Runtime/UI/README.md).

## Ajouter Settings et Audio

Construire les défauts à partir des capacités graphiques disponibles, puis choisir
une clé propre au jeu :

```csharp
using Lumivale.Core.Settings;

var graphics = new UnityGraphicsSettingsBackend();
var capabilities = graphics.GetCapabilities();
var defaults = new SettingsValues
{
    Width = capabilities.CurrentResolution.Width,
    Height = capabilities.CurrentResolution.Height,
    DisplayMode = capabilities.CurrentMode,
    QualityName = capabilities.CurrentQualityName,
    MasterVolume = 1f, MusicVolume = 0.6f, SfxVolume = 0.8f,
    LanguageCode = "en"
};
var settings = new SettingsService(new SettingsConfiguration(defaults,
    "com.my-studio.my-game.preferences", new[] { "en", "fr" }), graphics: graphics);
```

Le jeu possède le mixer et trois groupes distincts du même mixer : Master, Music
et SFX, avec Music/SFX routés sous Master. Exposer leurs volumes sous les noms
`MasterVolume`, `MusicVolume`, `SfxVolume`, ou fournir d'autres noms à la configuration.

```csharp
using Lumivale.Core.Audio;
using AudioConfiguration = Lumivale.Core.Audio.AudioConfiguration;

var audio = new AudioService(new AudioConfiguration(
    mixer, masterGroup, musicGroup, sfxGroup, maxSfxSources: 12));
// Add settings and audio to the services returned by CreateServices(). After Ready:
settings.ApplyGraphics();
var current = settings.Current;
audio.SetVolumes(current.MasterVolume, current.MusicVolume, current.SfxVolume);
audio.PlayMusic(menuMusic, loop: true, fadeInSeconds: 0.25f);
```

L'alias évite l'ambiguïté avec `UnityEngine.AudioConfiguration`. Le clip doit être
`Loaded` avant lecture ; le jeu précharge les clips et possède son AudioListener.
Une liaison propre au jeu peut appliquer les préférences avant la première musique
et écouter `settings.Changed` avec désabonnement symétrique.

Pour Options : éditer une copie de `settings.Current`, puis appeler `Update(draft)`,
`ApplyGraphics()` et `Save()` explicitement au bouton Apply. Back abandonne le
brouillon. Un Reset de brouillon n'enregistre rien. Signaler séparément les échecs
d'application/save ; ne stocker aucune donnée sensible dans PlayerPrefs.
[Contrat Settings](../Runtime/Settings/README.md) et
[contrat Audio](../Runtime/Audio/README.md).

## Ajouter Persistence et un DTO du jeu

Enregistrer un `PersistenceService(new PersistenceConfiguration("my-game"))` dans
le Bootstrap. Choisir une identité stable différente pour chaque jeu, puis définir
le DTO et ses règles dans le projet consommateur :

```csharp
using System;
using Lumivale.Core.Persistence;

[Serializable]
public sealed class CheckpointData
{
    public bool HasCheckpoint;
    public float PositionX;
    public int Counter;

    public static SaveSchema<CheckpointData> CreateSchema()
    {
        return new SaveSchema<CheckpointData>("checkpoint", 1, data =>
        {
            if (data == null || !data.HasCheckpoint || data.Counter < 0 ||
                float.IsNaN(data.PositionX) || float.IsInfinity(data.PositionX))
                throw new ArgumentException("Invalid checkpoint.");
        });
    }
}
```

Capturer la position des GameObjects et le compteur sur le thread Unity, puis
passer uniquement le DTO au service :

```csharp
var schema = CheckpointData.CreateSchema();
var snapshot = new CheckpointData
{
    HasCheckpoint = true, PositionX = player.position.x, Counter = counter
};
var saved = await saves.SaveAsync("slot-1", snapshot, schema, token);
var loaded = await saves.LoadAsync("slot-1", schema, token);
if (loaded.IsSuccess)
{
    // Apply loaded.Data to objects that are still present in the scene.
    // Also display loaded.RecoveredFromBackup when it is true.
}
```

`saves`, `player`, `counter` et `token` appartiennent au jeu. Tester le statut de
chaque résultat ; une annulation produit `OperationCanceledException`. L'application
d'un résultat devenu obsolète après fermeture/changement de scène appartient au jeu.
Faire confirmer la suppression avant `DeleteAsync`. Settings/PlayerPrefs reste
indépendant des sauvegardes.

## Garanties et limites

Persistence écrit dans `Application.persistentDataPath/LumiSaves/game-<gameId>`.
Il sérialise un snapshot borné, écrit/flush le temporaire puis utilise `File.Move`
ou `File.Replace` natifs avec une génération de backup. Il ne supprime jamais le
principal pour contourner un remplacement échoué. Load peut récupérer un backup
validé, sans modifier les fichiers. Les principaux corrompus/incompatibles sont
préservés ; aucun temporaire interrompu n'est promu.

Cela ne garantit pas une transaction universelle, un fsync du répertoire ou une
survie à toute coupure électrique. SHA256 détecte la corruption accidentelle sans
authentifier ni chiffrer. Les DTO JsonUtility ont un contrat strict, sans dictionnaire,
polymorphisme ou référence Unity. La validation/migration métier et les essais du
filesystem cible restent à la charge du jeu. Le
[README Persistence](../Runtime/Persistence/README.md) détaille les statuts,
limites de types/profondeur, annulations, concurrence et garanties exactes.

Les APIs Unity s'utilisent sur le thread principal, sans `.Wait()` ou `.Result`.
Les abonnements de scène ont un propriétaire et un nettoyage explicites. Les essais
automatisés n'établissent pas l'écoute humaine ni le fonctionnement des périphériques
physiques. Consulter [les notes V0.1](RELEASE_NOTES_0.1.0.md) et
[le guide de mise à jour](UPGRADING.md) avant une livraison.

## Exécuter les tests du package

Installer Unity Test Framework dans le projet de validation, puis ajouter au
niveau racine de son manifeste :

```json
"testables": ["com.lumivale.lumi-core"]
```

UPM n'offre pas une opération Client dédiée à cette métadonnée. Conserver le
reste du manifeste, laisser Unity résoudre les packages, puis lancer EditMode et
PlayMode dans Test Runner. Les tests du package ne nécessitent aucun asset du
Starter ; ses intégrations et sessions Play vivent dans le Starter. Les fixtures
Audio natives créent leurs propres assets Editor et demandent l'Editor pour ces
tests. Voir [les tests UPM officiels](https://docs.unity3d.com/6000.3/Documentation/Manual/cus-tests.html).
