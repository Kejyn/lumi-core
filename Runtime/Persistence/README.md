# Persistence — V0.1

Assembly `Lumivale.Core.Persistence`, dépendance déclarée : Bootstrap uniquement.
Aucune référence à Settings, Audio, Input, UI, Scene Flow ou au Starter. Aucun
singleton ni GameObject. Settings conserve son stockage PlayerPrefs indépendant.

## API publique

Le jeu définit son DTO concret `[Serializable]`, son identifiant de modèle stable,
une version positive et ses règles de validité. Exemple :

```csharp
[Serializable]
public sealed class CheckpointData
{
    public float PositionX;
    public int CompletedLevels;
}

var saves = new PersistenceService(new PersistenceConfiguration("my-game"));
// Register saves among the Bootstrap ILumiService instances, then wait for Ready.
var schema = new SaveSchema<CheckpointData>("checkpoint", 1, data =>
{
    if (data == null || data.CompletedLevels < 0)
        throw new ArgumentException("Invalid checkpoint.");
});
var saved = await saves.SaveAsync("slot-1", capturedDto, schema, token);
var loaded = await saves.LoadAsync("slot-1", schema, token);
if (loaded.IsSuccess) ApplyCheckpoint(loaded.Data); // Owned by the game.
var exists = await saves.ExistsAsync("slot-1", token);
var slots = await saves.ListSlotsAsync(token);
var deleted = await saves.DeleteAsync("slot-1", token); // Confirmation is owned by the game.
```

L'exemple suppose les imports `System`, `Lumivale.Core.Persistence`, ainsi que
les variables de capture/annulation et la méthode d'application du consommateur.

| Méthode | Résultat |
| --- | --- |
| SaveAsync<T>(slot, data, schema, token) | PersistenceResult<bool> : true après commit |
| LoadAsync<T>(slot, schema, token) | PersistenceResult<T> : DTO validé |
| DeleteAsync(slot, token) | PersistenceResult<bool> : true si slot existant supprimé |
| ExistsAsync(slot, token) | PersistenceResult<bool> : état de l'enveloppe |
| ListSlotsAsync(token) | PersistenceResult<IReadOnlyList<SaveSlotInfo>> : entrées triées |

`PersistenceResult<T>` expose `Status`, `Data`, `Metadata`, `RecoveredFromBackup`,
`Message`, `Exception`, `IsSuccess`. `SaveSlotMetadata` expose `SlotId`, `SavedAtUtc`
(`DateTimeKind.Utc`), `ModelId`, `DataVersion`.

| Statut | Signification |
| --- | --- |
| Success | Opération terminée ; consulter le flag de récupération |
| NotFound | Aucun principal/backup ; Delete peut aussi nettoyer un temporaire isolé |
| Corrupt | UTF-8, JSON, intégrité ou DTO invalide sans récupération réussie |
| Incompatible | Format, modèle ou version demandée incompatible |
| StorageError | I/O, permissions, verrou ou adaptateur ; diagnostic disponible |
| InvalidData | Entrée, DTO, identifiant ou taille invalide avant écriture |
| NotInitialized | Initialisation non terminée |
| ServiceStopping | Arrêt engagé ou service arrêté |

Une annulation produit une tâche annulée (`OperationCanceledException` à l'await).
Configuration/schema invalide : exception à la construction. Appel depuis un autre
thread que celui de construction : `InvalidOperationException`.

Exists/List ne connaissent pas de schema T : un format V1 d'une version de données
future apparaît lisible dans leur résultat ; Load/Save vérifient la compatibilité
avec le modèle demandé. La liste conserve les slots corrompus et les erreurs
individuelles au lieu de les cacher. Exists vérifie l'enveloppe, pas la validité
métier du DTO.

## Identités et stockage

Configurer une identité de jeu explicite et stable. Chemin par défaut :
`Application.persistentDataPath/LumiSaves/game-<gameId>/slot-<slotId>.json`.
Un root absolu alternatif permet les tests ou l'adaptation de plateforme. Le chemin
Unity est capturé à la construction sur le thread principal.

IDs jeu/modèle/slot : 1 à 64 caractères ASCII minuscules, premier caractère lettre
ou chiffre, suivants lettres/chiffres/`-`/`_`. Noms de périphériques Windows réservés
interdits. Aucune entrée non validée ne construit un chemin. Les préfixes de fichier
et l'identité de l'enveloppe renforcent l'isolation.

`FileSaveStorage` possède `.lumi-save.lock` avec `FileShare.None` jusqu'à Dispose.
Un autre propriétaire du même dossier est refusé. Les fichiers et le dossier
possédés ne peuvent être des reparse points. Les parents de root sont des emplacements
fiables choisis par le projet ; ceci n'est pas un sandbox contre un processus
malveillant remplaçant les chemins pendant une opération. L'exclusivité native
entre processus doit être vérifiée sur les autres plateformes.

Taille maximale UTF-8 : **4 Mio**, configurable ; lectures bornées. `.bak` et `.tmp`
restent dans le même dossier. Delete ne supprime pas les autres fichiers. Un `.tmp`
isolé n'apparaît pas dans List ; Delete explicite du slot le nettoie aussi.

## Écriture et récupération

1. Le jeu capture un DTO ; le service valide et sérialise immédiatement, avant
   toute attente disque. Le snapshot reste indépendant des mutations ultérieures.
2. Refus d'un principal corrompu, d'un autre modèle ou d'une version future.
   Un principal de même version est revalidé selon le schema courant.
3. Écriture du `.tmp` par worker, UTF-8 strict et borné, `Flush(true)`, fermeture.
4. Dernier checkpoint d'annulation. Principal existant :
   `File.Replace(tmp, primary, backup)` ; première écriture : `File.Move(tmp, primary)`.
5. Success seulement après retour de la finalisation native.

Le remplacement avec backup est réellement testé sous **Windows / Unity Editor
6000.3.13f1** : trois générations, partage de fichier refusant le remplacement,
échec du temporaire et conservation des octets précédents. Aucune branche ne
supprime le principal pour contourner un remplacement non supporté. Une telle
plateforme renvoie StorageError : fournir et tester un ISaveStorage approprié.

Un échec natif peut laisser le principal absent et la version précédente en backup.
Load inspecte le backup si le principal est absent/corrompu, valide enveloppe,
checksum, version et DTO. Il retourne Success avec RecoveredFromBackup=true,
**sans réécrire de fichier**. Un principal incompatible n'est jamais masqué par
un backup ancien. Un temporaire interrompu, même JSON complet, n'est jamais promu.

Save refuse un principal corrompu et conserve son backup. Après récupération,
le jeu doit proposer une décision explicite de suppression/export avant remplacement.
Delete enlève backup puis temporaire puis principal pour éviter la résurrection.
Une interruption/erreur de Delete peut laisser le principal ou avoir déjà retiré
le backup : ce n'est pas une transaction multi-fichiers.

Flush(true) demande le flush des buffers OS ; aucun fsync du répertoire n'est
effectué. Il n'y a pas de garantie universelle contre les coupures électriques,
pannes de support ou particularités du filesystem. Espace/permissions insuffisants
sont diagnostiqués sans autoriser la suppression du principal. SHA256 détecte la
corruption accidentelle ; ce n'est ni chiffrement ni authentification.

## Concurrence et cycle de vie

Construire et appeler le service sur le thread principal Unity. Validateurs et
JsonUtility y restent ; seuls strings et System.IO passent sur workers. Une grosse
sérialisation peut encore prendre du temps sur le thread principal : la V0.1 borne
les fichiers, sans infrastructure de tâches avancée. Aucun worker n'accède aux
GameObjects.

Toutes les opérations acceptées attendent un SemaphoreSlim commun, même entre
slots différents. Elles s'exécutent une à la fois ; aucun FIFO strict entre
demandes simultanées n'est garanti. Une lecture attend l'écriture engagée. Des
jeux avec dossiers distincts utilisent des services indépendants. Le snapshot
Save précède l'attente ; le timestamp correspond à la préparation du commit.

L'annulation empêche l'écriture avant commit. Après le dernier checkpoint, un
commit réussi retourne Success même si le token est annulé entre-temps. Le fichier
réellement enregistré ne doit pas être annoncé annulé. Le nettoyage du temporaire
ne masque pas une erreur initiale ou un commit réussi.

Shutdown refuse un token déjà annulé ; une fois engagé, il interdit les nouvelles
opérations, annule avant commit et attend tous les travaux avant de libérer le
verrou du dossier. Les appels répétés partagent la même tâche. Créer une nouvelle
instance après arrêt/échec d'initialisation. Bootstrap assure rollback et ordre
inverse. Aucun état statique mutable de service entre sessions sans Domain Reload.

Unity n'attend pas l'async OnDestroy/OnApplicationQuit. **Attendre explicitement
LumiBootstrap.ShutdownAsync avant une sortie contrôlée de Play ou du processus**.
Une fermeture immédiate peut arrêter les continuations Unity ; l'arrêt coopératif
seul ne garantit pas la finalisation. Aucun timeout ne libère un verrou alors que
son worker écrit encore.

## Sérialisation, versions et extensions

Enveloppe V1 : formatVersion, gameId, slotId, savedAtUtc, modelId, dataVersion,
payloadJson, sha256. Le checksum couvre métadonnées et payload avec longueurs UTF-8
explicites. Format de fichier et version positive du modèle sont distincts.

UnityJsonSaveSerializer utilise JsonUtility avec vérification stricte du JSON et
des types avant les coercitions natives. Contrat V0.1 :

- Racine : classe C# concrète Serializable. Champs publics ou SerializeField ;
  propriétés, statiques et NonSerialized ignorés ; readonly refusés.
- Numériques finis, bool, string, enums <=32 bits, DTO/classes/structs imbriqués,
  Vector2/Vector3/Quaternion, tableaux 1D et List<T> de types supportés.
- Pas de dictionnaires, containers imbriqués, tableaux multidimensionnels,
  polymorphisme, SerializeReference, UnityEngine.Object, DateTime/Guid/decimal,
  ISerializationCallbackReceiver ou aliases FormerlySerializedAs.
- Pas de types déclarés récursifs ; profondeur du schema <=7 même pour des champs
  null, pour éviter les limites natives. Grammaire JSON bornée à128 séparément.
- Champs manquants : défauts natifs/initialiseurs. Le validateur du jeu doit refuser
  les champs obligatoires absents et valeurs métier invalides.
- Classes inline : null et identité partagée non garantis. Préférer un flag de
  présence, des DTO simples et collections vides. Pas de roundtrip de graphes.

Format inconnu ou schema de jeu non identique : refus sans modification. Extension
de migration explicite : Load avec DTO/schema ancien reconnu, conversion dans le
projet, validation puis Save avec nouvelle version. Aucun registre ni migration
automatique. Save peut remplacer une ancienne version après contrôle d'intégrité ;
sa validité métier requiert que le jeu utilise l'ancien schema lors de sa migration.

ISaveSerializer et ISaveStorage permettent des adaptations ciblées. Le serializer
produit un objet JSON strict et refuse les données non supportées. Le storage
respecte annulation précommit, réussite réelle après commit, diagnostic et
exclusivité jusqu'à Dispose. Aucun adaptateur cloud/WebGL n'est fourni.

## Validation et références

Les tests EditMode du package couvrent le service, le disque et la sérialisation.
Les tests d'intégration du Starter couvrent les interactions et les sessions sans
Domain Reload. Exécuter les suites concernées sur la plateforme cible.
macOS, Linux, mobiles, consoles, WebGL, IL2CPP et coupures physiques non validés.
WebGL nécessite un adaptateur et une stratégie de synchronisation spécifiques.

Sources primaires : [JsonUtility Unity](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/JsonUtility.html),
[règles de sérialisation](https://docs.unity3d.com/6000.3/Documentation/Manual/script-serialization-rules.html),
[File.Replace](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.replace),
[Flush](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush),
[ReplaceFileW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew).
