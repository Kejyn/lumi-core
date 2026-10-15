# Lumi Core 0.1.0 — notes de release proposées

**Version candidate.** Le package cible Unity **6000.3.13f1** et Windows comme
première plateforme. Valider le Player et les périphériques de chaque projet
consommateur avant livraison.

## Contenu

Input inclut les captures de bindings par GUID, l'annulation/timeout, l'import/export
validé et le reset des overrides de chemin. Le stockage et la réservation des
contrôles restent propres au consommateur. Voir le
[contrat Input](../Runtime/Input/README.md).

- Bootstrap : services à dépendances déclarées, ordre déterministe, démarrage
  partagé, rollback et arrêt inverse ; hôte persistant protégé contre les doublons.
- Scene Flow : chargement Unity asynchrone exclusif, Single/Additive, transitions
  configurables, progression et événements terminaux, drainage après annulation.
- Input : clone de l'asset consommateur, contextes Gameplay/UI/Pause configurables,
  suspensions imbriquées et callbacks disposables.
- UI : navigation uGUI, historique, modales, sélection EventSystem, bridge Input
  System et pause avec contrôle facultatif de `Time.timeScale`.
- Settings : snapshots validés, préférences versionnées dans une clé propre au jeu,
  application graphique et sauvegarde explicites.
- Audio : mixer du consommateur, musique persistante avec fondus non affectés par
  la pause, volumes normalisés et pool SFX 2D/3D borné.
- Persistence : DTO et versions propres au jeu, slots diagnostiqués, intégrité,
  temporaire flushé, remplacement natif, backup et arrêt drainant les opérations.

Le package dépend uniquement de **Input System 1.19.0** et **uGUI 2.0.0** en UPM.
Bootstrap reste indépendant ; Settings et Audio ne se référencent pas. Aucun asset,
style de jeu ou script Starter n'est requis par le runtime Core.

## Garanties à connaître

Les APIs Unity demandent le thread principal et un propriétaire clair des callbacks
et registrations. Un chargement Unity déjà commencé peut activer sa destination
même après annulation de la demande. Attendre un arrêt explicite avant une sortie
contrôlée ; Unity n'attend pas les continuations d'OnApplicationQuit.

Persistence préserve les principaux corrompus/incompatibles et ne promeut pas un
temporaire interrompu. Le backup permet une récupération diagnostiquée sans
réécriture. Ce mécanisme n'est ni une transaction universelle, ni une garantie
physique contre les coupures électriques, ni un stockage chiffré/authentifié.
Les migrations métier appartiennent au jeu. Voir
[le contrat exact](../Runtime/Persistence/README.md).

## Installation et validation

L'installation locale utilise un dossier package voisin du projet Unity. Une
installation Git doit référencer une révision accessible et immuable ; voir
[Démarrer](GETTING_STARTED.md) et [Mettre à jour](UPGRADING.md).

Une compilation ou des tests automatisés ne suffisent pas à valider toutes les
plateformes. Vérifier notamment le backend choisi, le filesystem cible, l'écoute,
les périphériques physiques et les cycles de vie réels du Player. Aucun tag
définitif n'est établi par les présentes notes.
