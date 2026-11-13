# Politique de confidentialité — Ma Passerelle

*Dernière mise à jour : 14 novembre 2026*

Ma Passerelle est un logiciel libre pour Windows qui partage l'écran du PC sur un appareil compatible Apple TV du réseau local. Éditeur : GR14.

## En bref
- Ma Passerelle ne collecte aucune donnée personnelle et n'envoie rien à l'éditeur.
- Pas de compte, pas de publicité, pas de mesure d'audience, pas de pistage.
- Tout se passe sur votre réseau local, entre votre PC et l'appareil de projection que vous choisissez.

## Ce qui circule sur le réseau local
- **Recherche des appareils** : Ma Passerelle écoute les annonces des appareils compatibles du réseau local (Bonjour/mDNS). Rien ne sort du réseau local.
- **Partage** : l'image de l'écran ou de la fenêtre que vous avez choisie, et le son du PC si l'option est activée, sont envoyés chiffrés à cet appareil seulement, et seulement pendant le partage.

Ma Passerelle ne se connecte pas à Internet.

## Ce qui est enregistré sur votre PC
- **Clés de jumelage** : après la saisie du code, une clé permet de se reconnecter sans nouveau code. Elle est enregistrée dans votre profil Windows (%APPDATA%\MaPasserelle), protégée par Windows (DPAPI), et n'est jamais transmise à l'éditeur.
- **Réglages** : qualité, fluidité, son, pointeur, démarrage avec Windows (fichier reglages.json au même endroit).

Aucun enregistrement de l'écran n'est conservé.

## Supprimer vos données
La commande `mapasserelle unpair --to <appareil>` supprime la clé de jumelage d'un appareil. Pour tout effacer, supprimez le dossier %APPDATA%\MaPasserelle.

## Mon Estrade
Mon Estrade peut lancer Ma Passerelle pour démarrer ou arrêter un partage. Les deux programmes échangent uniquement les commandes et l'état du partage, sur votre PC. Aucune donnée d'élève n'est transmise à Ma Passerelle.

## Contact
Questions ou signalements : https://github.com/gaeremy/mon-estrade-site/issues
