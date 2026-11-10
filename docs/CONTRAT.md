# Contrat de pilotage (version 1)

Ce document décrit comment un autre programme, par exemple Mon Estrade, pilote Ma Passerelle.
Les deux programmes restent séparés : l'un lance `mapasserelle.exe`, lit sa sortie standard
et écrit sur son entrée standard. Il n'y a ni bibliothèque partagée ni mémoire commune.

## Principes

- Une commande par lancement : `mapasserelle <commande> [options] --json`.
- Avec `--json`, chaque événement est **un objet JSON sur une ligne** de la sortie standard,
  en UTF-8 sans BOM. Le champ `event` donne son type.
- Sans `--json`, la même information sort en texte lisible, pour un usage à la main.
- L'entrée standard est lue en UTF-8, une commande par ligne.
- Le code de sortie indique le résultat. Une erreur produit toujours un événement `error`
  avant la sortie.
- De nouveaux champs ou événements peuvent être ajoutés sans changer de version :
  le programme appelant doit ignorer ce qu'il ne connaît pas.

`mapasserelle version --json` renvoie la version du contrat :

```json
{"event":"version","name":"Ma Passerelle","version":"0.1.0","contract":1}
```

## Désigner un appareil

`--to` accepte le **nom** de l'appareil (sans tenir compte de la casse) ou son **identifiant**
(`id`, renvoyé par `discover`). L'identifiant est préférable : il ne change pas si
l'utilisateur renomme son téléviseur.

## Commandes

### `discover [--seconds N]`

Écoute le réseau pendant `N` secondes (6 par défaut, 60 au plus), puis liste les appareils.

```json
{"event":"searching","seconds":6}
{"event":"device","id":"E69BEC25FFA7","name":"Séjour","model":"AppleTV11,1","address":"10.0.3.104","mirroring":true,"audio":true,"paired":true}
{"event":"done","count":1}
```

| Champ | Sens |
|---|---|
| `mirroring` | l'appareil accepte le partage d'écran |
| `audio` | l'appareil accepte le son |
| `paired` | un jumelage est déjà enregistré pour cet appareil |
| `address` | adresse IPv4, ou `null` si elle n'est pas encore connue |

### `pair --to <nom|id> [--pin NNNN]`

Jumelle le PC avec l'appareil. À faire une seule fois par appareil et par compte Windows.

1. Ma Passerelle émet `pin_required` : le téléviseur affiche alors un code.
2. Le programme appelant écrit ce code sur l'entrée standard, suivi d'un retour à la ligne
   (ou le fournit d'avance avec `--pin`). Délai : 120 secondes.
3. Ma Passerelle émet `paired`.

```json
{"event":"searching","target":"Séjour"}
{"event":"pin_required","id":"E69BEC25FFA7","name":"Séjour"}
{"event":"paired","id":"E69BEC25FFA7","name":"Séjour"}
```

### `unpair --to <nom|id>`

Oublie le jumelage. Émet `unpaired`. L'appareil n'a pas besoin d'être allumé si on donne
son identifiant.

### `mirror --to <nom|id> [options]`

Partage l'écran principal jusqu'à ce qu'on l'arrête.

| Option | Défaut | Sens |
|---|---|---|
| `--fps N` | 30 | images par seconde, de 5 à 60 |
| `--mbps N` | 0 | débit vidéo en Mbit/s ; 0 = choisi automatiquement |
| `--no-audio` | | n'envoie pas le son du PC |
| `--minutes M` | | arrêt automatique après `M` minutes |
| `--no-stdin` | | n'écoute pas l'entrée standard (arrêt par `--minutes` ou Ctrl+C) |

Événements, dans l'ordre :

```json
{"event":"searching","target":"Séjour"}
{"event":"connecting","id":"E69BEC25FFA7","name":"Séjour","address":"10.0.3.104"}
{"event":"stage","message":"..."}
{"event":"streaming","id":"E69BEC25FFA7","name":"Séjour","fps":30,"mbps":0,"audio":true}
{"event":"status","frames":298,"seconds":10,"audio":true}
{"event":"stopped","reason":"command","frames":912,"seconds":31}
```

- `stage` : étapes techniques de la connexion et de la capture. Le texte est destiné au
  journal, pas à l'affichage ; il peut changer d'une version à l'autre.
- `streaming` : l'image part vers le téléviseur. C'est le moment d'afficher « partage en cours ».
- `status` : émis toutes les 10 secondes, et à la demande.

Pendant le partage, l'entrée standard accepte :

| Ligne | Effet |
|---|---|
| `status` | émet tout de suite un événement `status` |
| `stop` | arrête le partage proprement |

Le champ `reason` de `stopped` vaut :

| Valeur | Cause |
|---|---|
| `command` | la ligne `stop` a été reçue |
| `stdin_closed` | l'entrée standard a été fermée (le programme appelant s'est arrêté) |
| `signal` | Ctrl+C |
| `time_limit` | la durée de `--minutes` est écoulée |
| `stalled` | plus aucune image envoyée depuis 20 secondes ; précédé d'un `error` `stream_stalled` |

Si le programme appelant se ferme ou plante, l'entrée standard se ferme et le partage s'arrête
tout seul. **Attention :** lancé avec une entrée standard vide ou redirigée depuis `NUL`,
le partage s'arrête donc immédiatement ; utiliser `--no-stdin` dans ce cas.

## Erreurs

```json
{"event":"error","code":"not_paired","message":"Séjour n'est pas encore jumelé : lancer d'abord « pair »"}
```

`message` est une phrase en français, affichable telle quelle. `code` est stable.

| Code de sortie | `code` | Sens |
|---|---|---|
| 0 | | réussite |
| 1 | `not_supported` | l'appareil n'accepte pas le partage d'écran |
| 2 | `usage` | commande ou option incorrecte |
| 3 | `not_found` | appareil introuvable sur le réseau |
| 4 | `not_paired` | aucun jumelage pour cet appareil |
| 5 | `pairing_failed`, `pin_missing` | code refusé, ou non fourni à temps |
| 6 | `connection_failed`, `stream_stalled` | connexion impossible, ou partage interrompu |

`unknown_command` signale une ligne non reconnue sur l'entrée standard ; le partage continue.

## Jumelages

Les jumelages sont enregistrés dans `%APPDATA%\MaPasserelle\credentials.dat`, chiffrés pour
le compte Windows courant (DPAPI). Ils ne sont utilisables ni par un autre compte, ni sur un
autre PC.

Si un jumelage existe déjà pour l'appareil dans WinPlay sur le même compte
(`%APPDATA%\WinPlay`), Ma Passerelle le reprend à la première utilisation. Conséquence :
après un `unpair`, ce jumelage WinPlay serait repris de nouveau.

## Limites connues

- Seul l'écran principal est partagé ; on ne peut pas encore choisir un autre écran ou une fenêtre.
- Un seul téléviseur par lancement.
- La détection d'un partage interrompu (`stalled`) repose sur l'absence d'images pendant
  20 secondes ; elle n'a pas encore été vérifiée en coupant réellement le téléviseur.
