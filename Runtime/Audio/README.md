# Audio

Module natif Unity 6000.3.13f1, assembly `Lumivale.Core.Audio`. Il référence
uniquement Bootstrap et UnityEngine. Il ne dépend ni de Settings, ni d'Input,
ni d'UI, ni d'un Render Pipeline. Les mixers et clips appartiennent au projet
consommateur ; aucun son ni mixer de démonstration n'est inclus dans le Core.

## Configuration et cycle de vie

Le projet fournit un `AudioConfiguration` avec son `AudioMixer`, trois groupes
distincts Master, Music et SFX, et les noms de leurs paramètres de volume exposés.
Music et SFX doivent être routés sous Master dans le mixer du projet. Les noms
par défaut sont `MasterVolume`, `MusicVolume` et `SfxVolume`. Ils sont configurables.
Tous les groupes doivent appartenir au même mixer. Le plafond des sources SFX
vaut 12 par défaut ; la configuration accepte entre 1 et 128 sources.

```csharp
using Lumivale.Core.Audio;
using Lumivale.Core.Bootstrap;
using AudioConfiguration = Lumivale.Core.Audio.AudioConfiguration;

var audio = new AudioService(new AudioConfiguration(
    mixer, masterGroup, musicGroup, sfxGroup, maxSfxSources: 12));
var bootstrap = new BootstrapCoordinator(new ILumiService[] { audio });
await bootstrap.InitializeAsync();
audio.SetVolumes(0.8f, 0.6f, 1f);
audio.PlayMusic(menuMusic, loop: true, fadeInSeconds: 0.3f);
// …
await bootstrap.ShutdownAsync();
```

L'alias distingue cette configuration de `UnityEngine.AudioConfiguration`, qui
décrit la configuration DSP native lorsque UnityEngine est aussi importé.

Le service possède `Id = "audio"` et aucune dépendance de service. Une composition
Starter peut déclarer un service d'intégration dépendant de `settings` et `audio`
pour synchroniser les préférences avant de démarrer la musique. Cette dépendance
reste dans le projet ; les deux modules Core demeurent indépendants.

Appelez toutes les API depuis le thread principal Unity. L'initialisation requiert
Play mode et crée un GameObject racine persistant avec une source musicale et un
`LumiAudioRunner`. Elle attend une frame avant d'appliquer les volumes : Unity
déconseille `AudioMixer.SetFloat` pendant `Awake` et `OnEnable`. Le Bootstrap attend
cette tâche avant de signaler qu'il est prêt. Les appels d'initialisation simultanés
partagent la même tâche. Le jeton du premier appel contrôle ce démarrage.

Les trois paramètres exposés sont validés avant toute écriture. Une erreur de
configuration ou une annulation nettoie les objets partiellement créés. L'arrêt
durant l'initialisation empêche toute application tardive. Un jeton d'arrêt déjà
annulé refuse l'arrêt avant mutation ; une fois commencé, le nettoyage termine
sans interruption. Chaque instance possède un seul cycle de vie : créez un
nouveau service après son arrêt.

`ShutdownAsync` détache le runner, arrête les sources, libère les références de
clips et rend le root inactif immédiatement. Il détruit ensuite les objets appartenant
au service et attend une frame en Play mode. Les overrides des trois paramètres du
mixer sont retirés via `ClearFloat`. Le projet doit laisser ces paramètres à la
possession exclusive de ce service pendant son cycle de vie ; les snapshots ne
pilotent plus un paramètre exposé après `SetFloat`.
`GetFloat` peut encore exposer l'ancienne valeur immédiatement après ce retrait ;
le contrôle live a retrouvé les valeurs du snapshot après quelques frames natives.
Le nettoyage des sources et références ne dépend pas de cette observation différée.

Le service ne crée aucun singleton, AudioListener ou abonnement global. Les scènes
du projet conservent leur AudioListener et leurs propres sources. Aucune ressource
native ne doit survivre à un arrêt explicitement attendu, y compris dans les sessions
sans Domain Reload. Les limites générales du Bootstrap à la fermeture immédiate
du processus restent applicables.

## Volumes

`SetVolumes(master, music, sfx)` prend trois valeurs normalisées finies entre 0 et 1.
Les valeurs hors intervalle, `NaN` et les infinis sont refusés avant mutation.
`MasterVolume`, `MusicVolume` et `SfxVolume` exposent les valeurs acceptées.

`AudioVolume.NormalizedToDecibels` utilise `20 * log10(volume)` avec un plancher de
−80 dB. Zéro retourne directement −80 dB sans appeler `log10(0)`. Zéro mute également
les sources possédées de la catégorie concernée : le silence est explicite.
Master à zéro mute la musique et tous les SFX. Revenir au-dessus de zéro rétablit
leur état de mute. Les gains individuels et le gain du fondu ne sont pas multipliés
par les volumes de préférences ; ils restent dans `AudioSource.volume`.

Les sources extérieures au service ne sont pas modifiées. Celles routées dans le
mixer du projet reçoivent néanmoins naturellement son atténuation, avec le plancher
de −80 dB : le service ne possède pas leur propriété `mute`.

## Musique

```csharp
audio.PlayMusic(track, loop: true, fadeInSeconds: 0.25f, volume: 0.7f);
audio.StopMusic(fadeOutSeconds: 0.25f);
```

Une seule source musicale est utilisée. `PlayMusic` arrête immédiatement le morceau
précédent et démarre le nouveau, avec boucle facultative et fondu entrant. Une demande
de remplacement recommence le clip, même si c'est le même morceau. `StopMusic()`
arrête immédiatement ; avec une durée positive, il commence au gain actuel et
libère le clip à la fin du fondu. Les demandes successives remplacent le seul état
de fondu : la dernière demande gagne, sans tâches musicales concurrentes ni crossfade.

Le runner utilise `Time.unscaledDeltaTime` : les fondus fonctionnent à
`Time.timeScale = 0`. Le service ne change ni timeScale, ni `AudioListener.pause`.
Une pause native de l'AudioListener a ses propres effets Unity sur la lecture et
l'horloge DSP ; les fondus restent pilotés par le temps non affecté par timeScale.
`CurrentMusic` et `IsMusicPlaying` permettent de consulter la lecture courante.

Les clips doivent être valides et avoir `loadState == Loaded` avant un appel.
Préchargez les données avec l'importeur ou `AudioClip.LoadAudioData`, puis attendez
la fin du chargement. Une référence absente, un clip non chargé, un gain individuel
invalide ou une durée de fondu non finie/négative sont refusés avant remplacement.

## Effets sonores

```csharp
bool accepted2D = audio.PlaySfx2D(click, volume: 0.8f);
bool accepted3D = audio.PlaySfx3D(impact, worldPosition, volume: 0.5f);
```

Le pool alloue ses sources au premier besoin et réutilise celles dont la lecture
est terminée. Si toutes les sources du plafond configuré sont occupées, l'appel
retourne `false`, sans interruption ni vol d'une source existante. Les appels
acceptés retournent `true`. `ActiveSfxCount` et `AllocatedSfxSourceCount` permettent
d'observer le pool. Les sources sont libérées à l'arrêt du service.

Chaque lecture remet le clip, le gain, la position et le caractère 2D/3D sur la
source réutilisée. Les sources 3D utilisent le rolloff logarithmique natif, avec
une distance minimale de 1, maximale de 30 et Doppler désactivé. Cette V0.1 ne
fournit ni suivi d'un Transform mobile, ni middleware, ni spatialiseur avancé.
Les positions non finies sont rejetées. Un listener natif appartient aux scènes
du projet consommateur.

## Tests

- `Tests/Audio/Editor`, assembly `Lumivale.Core.Audio.Tests.Editor` : 11 tests de conversion,
  bornes et références de configuration manquantes.
- `Tests/Audio/Runtime`, assembly `Lumivale.Core.Audio.Tests.PlayMode` : 15 tests du mixer natif,
  lecture, volumes, fondus en pause, pool 2D/3D, scènes, annulation et nettoyage.

Les tests PlayMode construisent un mixer temporaire dans un dossier Assets à GUID
et des clips en mémoire, puis suppriment leurs propres ressources. Un clip WAV
temporaire vérifie le refus d'un clip déchargé. Ils ne référencent aucun asset du
Starter. La création de mixer utilise les API internes de l'Editor uniquement dans
la fixture `#if UNITY_EDITOR` ; les tests natifs du mixer nécessitent donc l'Editor.
Le runtime de ce module n'utilise aucune réflexion ni API UnityEditor.

Référence : [AudioMixer.SetFloat, Unity 6.3](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Audio.AudioMixer.SetFloat.html).
