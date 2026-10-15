# Settings

`Lumivale.Core.Settings` fournit des préférences locales, validées, indépendantes
des sauvegardes de gameplay. L'assembly référence seulement Bootstrap et UnityEngine.
Elle ne référence ni Audio, ni Input/UI, ni Localization, ni le Starter.

## Composition et API

```csharp
using Lumivale.Core.Settings;
using UnityEngine;

var backend = new UnityGraphicsSettingsBackend();
var available = backend.GetCapabilities();
var defaults = new SettingsValues
{
    Width = available.CurrentResolution.Width,
    Height = available.CurrentResolution.Height,
    DisplayMode = available.CurrentMode,
    QualityName = available.CurrentQualityName,
    MasterVolume = 0.8f,
    MusicVolume = 0.6f,
    SfxVolume = 0.8f,
    LanguageCode = "en"
};
var settings = new SettingsService(new SettingsConfiguration(defaults,
    "com.my-studio.my-game.preferences", new[] { "en", "fr" }), graphics: backend);
// Register settings in the project's BootstrapCoordinator / LumiBootstrap.
```

`SettingsService` implémente `ILumiService`, avec `Id = "settings"` et aucune
dépendance de service. Son initialisation charge les préférences ; elle ne modifie
pas implicitement l'affichage et n'écrit rien dans le stockage. La composition du
projet appelle explicitement `ApplyGraphics()` après le démarrage si elle souhaite
appliquer les préférences chargées, avant de lancer le gameplay.

| API | Comportement |
| --- | --- |
| `Current`, `Defaults` | Retourne une copie indépendante ; service initialisé requis. |
| `Capabilities` | Lit les choix graphiques actuels sans mutation. |
| `Update(values)` | Copie et valide les valeurs, notifie les changements effectifs ; retourne le snapshot commis par cet appel. |
| `ResetDefaults()` | Restaure les défauts actuellement valides en mémoire ; notification si changement. |
| `ApplyGraphics()` | Revalide les choix selon les capacités actuelles puis applique résolution, mode et qualité. |
| `Save()` | Écrit et flush explicitement le document JSON des valeurs courantes. |
| `LastLoadStatus`, `LastLoadMessage` | Distingue absence, chargement, réparation, document invalide, version inconnue et erreur de lecture. |

Le menu d'un projet peut éditer un brouillon obtenu par `Current`, appeler
`Update`, `ApplyGraphics`, puis `Save` au bouton Apply, ou abandonner simplement le
brouillon au bouton Back. `Update`/Reset ne déclenchent ni application graphique ni
sauvegarde automatiques. Une composition Starter peut écouter `Changed` et appeler
les volumes Audio ; aucun couplage Settings → Audio n'est introduit dans Core.

Chaque observateur `Changed` reçoit sa propre copie. Ses exceptions sont transmises
à `ObserverFailed`, sans bloquer les autres observateurs. Un observateur qui arrête
le service ou effectue un nouvel `Update` interrompt la diffusion de l'ancien snapshot :
un abonné Audio suivant ne reçoit donc pas une valeur devenue obsolète. Le snapshot
retourné par Update reste celui commis par cet appel, même si un observateur change
ensuite l'état. Désabonnez les composants de scène dans `OnDisable`/`OnDestroy`.

## Validation

- Les volumes sont normalisés dans `[0, 1]`. Les valeurs finies hors bornes sont
  clampées ; NaN et les infinis utilisent le défaut validé correspondant.
- Les langues peuvent être limitées par une liste configurable, sans traductions
  ou tables. Une valeur absente, invalide ou non autorisée reprend la langue par
  défaut. Les codes autorisés sont comparés sans tenir compte de la casse et
  renvoyés selon l'orthographe configurée. Sans liste, une validation syntaxique
  simple accepte les codes de type `en`, `fr` ou `en-US` ; ce n'est pas un parseur BCP 47.
- Les résolutions largeur/hauteur sont dédupliquées depuis `Screen.resolutions`.
  La taille actuelle de la fenêtre est également un choix valide, notamment quand
  Unity ne fournit aucune résolution disponible. Aucun format fixe n'est imposé.
- Une résolution indisponible reprend le défaut valide, sinon la résolution
  courante valide, sinon le premier choix fourni par le backend. Le même principe
  s'applique aux modes et profils de qualité.
- La qualité est persistée **par nom**, afin qu'un réordonnancement des profils du
  projet ne change pas le choix sauvegardé. Un profil renommé ou retiré reprend un
  défaut valide. Le backend retrouve son index au moment de l'application.
- `ApplyGraphics` relit les capacités : un écran ou un profil retiré depuis le
  chargement est validé à nouveau. Une exception d'application ou de Save est
  transmise à l'appelant ; l'interface du projet peut alors signaler l'échec.

L'adaptateur natif propose Windowed et FullScreenWindow sur desktop, ExclusiveFullScreen
sur Windows, et MaximizedWindow sur macOS. Les autres plateformes utilisent
FullScreenWindow ; WebGL propose Windowed et FullScreenWindow, sous réserve des
restrictions du navigateur. En Windows/macOS Editor, les choix correspondent à la
cible desktop ; le Game View ne prouve pas un passage effectif en plein écran natif.
Les adaptations spécifiques à une plateforme peuvent fournir un autre
`IGraphicsSettingsBackend`. Cet adaptateur et `ISettingsStorage` permettent également
des tests déterministes, sans modifier l'écran ou le stockage réel.

Cette V0.1 ne choisit pas de fréquence de rafraîchissement. `Screen.SetResolution`
utilise la politique Unity ; les changements de taille se terminent en fin de frame.
Les contraintes du Game View ou de la plateforme peuvent empêcher d'observer la
taille souhaitée dans l'Editor. Aucun Render Pipeline n'est imposé.

## Stockage et mises à jour

L'adaptateur par défaut est `PlayerPrefsSettingsStorage`. Il stocke **un seul string
JSON** dans la clé exacte de `SettingsConfiguration.StorageKey` ; la clé par défaut
est `com.lumivale.preferences`. Chaque jeu doit choisir sa propre clé/namespace.
PlayerPrefs est local, non chiffré, et ne doit contenir aucune donnée sensible.
Le module n'utilise ni fichier de sauvegarde de gameplay ni service Persistence.

Le document possède un numéro de schéma explicite, séparé de la clé :

```json
{
  "schemaVersion": 1,
  "values": {
    "Width": 1280,
    "Height": 720,
    "DisplayMode": 3,
    "QualityName": "High",
    "MasterVolume": 0.8,
    "MusicVolume": 0.6,
    "SfxVolume": 0.8,
    "LanguageCode": "en"
  }
}
```

Pour un schéma 1, les champs absents conservent les défauts configurés via
`JsonUtility.FromJsonOverwrite` ; un volume explicitement à zéro reste zéro.
Les choix invalides sont réparés en mémoire. Une clé absente reprend les défauts.
Un JSON corrompu, un objet values absent ou une version inconnue/obsolète reprend
les défauts et expose un statut détaillé. **L'initialisation ne réécrit jamais le
document**, afin de préserver notamment les données d'une version future. Seul un
`Save()` explicite remplace ce document. Aucune migration implicite d'un schéma
inconnu n'est tentée ; une future version devra fournir sa migration intentionnelle.
La présence réelle de l'objet `values` est vérifiée au niveau racine, après parsing
Unity : JsonUtility peut instancier ce champ même lorsqu'il manque ou vaut null.
Un texte cité ou un objet imbriqué contenant « values » ne satisfait pas cette vérification.

Le numéro de schéma doit être un entier JSON représentable par `int` : une chaîne
`"1"`, un booléen ou un nombre fractionnaire ne sont pas acceptés comme version 1.
Pour les documents V1, Width, Height et DisplayMode doivent aussi être des entiers ;
les volumes doivent être des nombres JSON, et les noms de qualité/langue des chaînes.
Un `null` de qualité/langue utilise le défaut validé, alors qu'un volume `null`
invalide le document. Les champs connus répétés, ainsi que plusieurs objets `values`,
sont refusés comme ambigus. Les champs inconnus, leurs objets imbriqués et leurs
textes sont ignorés. Cette vérification ciblée évite les conversions silencieuses
de JsonUtility, qui peut transformer une valeur numérique fournie en chaîne,
booléen ou objet en zéro. Un document de type incompatible expose `InvalidData`,
reprend les défauts en mémoire et demeure inchangé dans le stockage. Les règles
V1 ne sont pas appliquées aux valeurs d'un schéma entier inconnu ou futur.

`ShutdownAsync` retire les observers et l'état en mémoire, même avec un jeton annulé.
Il ne sauvegarde pas implicitement. Un nouveau service rechargera le dernier Save.
L'implémentation ne possède aucun singleton, registre statique mutable ou abonnement
global Unity, et reste compatible avec les sessions sans Domain Reload.
Appelez toutes les API sur le thread principal Unity.

## Tests

`Tests/Editor/SettingsServiceTests.cs` utilise des adaptateurs contrôlés pour tester
les défauts, copies, validation, JSON absent/corrompu/partiel/versionné, langues,
application explicite, profils réordonnés, changement de capacités, erreurs,
notifications réentrantes et cycle de vie. Un test utilise réellement PlayerPrefs
avec des clés GUID isolées puis nettoyées. Aucun test unitaire ne dépend du Starter.
