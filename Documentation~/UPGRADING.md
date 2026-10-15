# Mettre à jour Lumi Core

Version de package actuelle : **0.1.0**, Unity de référence **6000.3.13f1**.
Une version affichée dans `package.json` ne constitue pas à elle seule une release
validée. Vérifier la compatibilité du projet consommateur avant une livraison.

## Dépendance locale reproductible

1. Enregistrer les commits du jeu et du package qui fonctionnent ensemble ; une
   dépendance `file:` ne verrouille pas le commit du dossier voisin.
2. Conserver les changements locaux et créer un point de restauration avant de
   changer la version du package. Garder les fichiers `.meta` avec leurs sources.
3. Lire CHANGELOG et les contrats des modules utilisés. Garder Unity/Input System/
   uGUI aux versions validées tant qu'une montée de version n'a pas été testée.
4. Mettre le dossier local à jour, ouvrir le projet avec Unity 6000.3.13f1 et
   attendre résolution UPM/compilation. Vérifier le chemin Local réellement résolu.
5. Relancer les tests du package et du jeu, dont sessions sans Domain Reload,
   annulations/concurrence, UI/Input, volumes et sauvegardes. Produire puis tester
   le Player de la plateforme cible avant de livrer.
6. Conserver les nouveaux commits et résultats ensemble. En cas de régression,
   restaurer les commits de sources/configurations choisis ; ne pas supprimer les
   données persistantes pour masquer un problème de compatibilité.

Les règles du bridge Input System 1.19 et l'outillage Editor de création des mixers
justifient une validation particulière lors d'un changement Unity/Input System.
Les APIs publiques n'ont pas vocation à changer silencieusement.

## Préférences et sauvegardes existantes

Garder stables la clé PlayerPrefs, l'identité du jeu, les IDs de modèle et slots.
Un renommage peut créer un nouveau stockage ou rendre les anciennes données
inaccessibles. Chaque jeu utilise ses propres identités et chemins.

Settings conserve un document inconnu/invalide jusqu'à un `Save()` explicite.
Persistence distingue version du format et version métier. Pour une évolution de
DTO, charger avec l'ancien schema reconnu, convertir dans le jeu, valider, puis
enregistrer avec le nouveau schema. Tester cette migration sur des copies isolées
des fichiers. Garder une copie des originaux et traiter les statuts Corrupt/
Incompatible ; ne pas remplacer implicitement une version future ni un autre modèle.
Un changement de version du package ne fournit pas une migration métier automatique.

## Installation depuis une révision Git

Lorsque le dépôt du package est accessible, UPM peut l'installer depuis Git :
`package.json` se trouve à sa racine. Exemple à adapter avec l'URL du dépôt et
une révision réellement publiée :

```json
"com.lumivale.lumi-core": "https://github.com/<owner>/lumi-core.git#<commit-sha>"
```

Les valeurs entre `<...>` sont des paramètres à remplacer. Pour un dépôt privé,
configurer l'authentification Git de la machine sans inclure de credential dans
l'URL ou le manifeste. Utiliser **Install package from git URL** dans Package
Manager, puis vérifier le lockfile et le commit résolu. Préférer une révision
immuable à une branche mutable pour une livraison reproductible.
La [documentation UPM Git](https://docs.unity3d.com/6000.3/Documentation/Manual/upm-git.html)
décrit les URLs, révisions et sous-dossiers.

Les versions du Starter et du Core sont suivies dans deux dépôts indépendants.
Enregistrer les deux révisions compatibles ; un nom de version commun ne lie pas
automatiquement leurs historiques.
