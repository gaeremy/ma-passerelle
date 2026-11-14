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

Nom et identifiant passent par une recherche sur le réseau local (jusqu'à 8 secondes), que
certains réseaux bloquent (Wi-Fi d'établissement, réseaux séparés). `--to` accepte donc aussi
une **adresse IPv4**, éventuellement suivie du port (`192.168.1.20` ou `192.168.1.20:7000`) :
Ma Passerelle interroge alors directement l'appareil, sans recherche, et le partage démarre
plus vite. Dans ce cas, `searching` est remplacé par :

```json
{"event":"asking","address":"192.168.1.20","port":7000}
```

Si rien ne répond dans les 5 secondes : erreur `not_found` (code 3). Le jumelage est retrouvé
par l'identifiant que renvoie l'appareil ; l'appelant peut donc mémoriser l'adresse donnée par
`discover` et s'en servir ensuite.

## Commandes

### `discover [--seconds N]`

Écoute le réseau pendant `N` secondes (6 par défaut, 60 au plus), puis liste les appareils.

```json
{"event":"searching","seconds":6}
{"event":"device","id":"A1B2C3D4E5F6","name":"Salle 12","model":"AppleTV11,1","address":"192.168.1.20","mirroring":true,"audio":true,"paired":true}
{"event":"done","count":1}
```

| Champ | Sens |
|---|---|
| `mirroring` | l'appareil accepte le partage d'écran |
| `audio` | l'appareil accepte le son |
| `paired` | un jumelage est déjà enregistré pour cet appareil |
| `address` | adresse IPv4, ou `null` si elle n'est pas encore connue |

### `screens`

Liste les écrans branchés. L'écran principal porte toujours le numéro 1.

```json
{"event":"screen","index":1,"name":"\\\\.\\DISPLAY1","width":1920,"height":1080,"x":0,"y":0,"primary":true}
{"event":"done","count":1}
```

### `windows`

Liste les fenêtres partageables, comme dans Alt+Tab.

```json
{"event":"window","id":262520,"title":"Mon Estrade","process":"MonEstrade","width":1129,"height":635,"minimized":false}
{"event":"done","count":1}
```

`id` n'est valable que tant que la fenêtre existe : relancer `windows` avant chaque partage.

### `pair --to <nom|id> [--pin NNNN]`

Jumelle le PC avec l'appareil. À faire une seule fois par appareil et par compte Windows.

1. Ma Passerelle émet `pin_required` : le téléviseur affiche alors un code.
2. Le programme appelant écrit ce code sur l'entrée standard, suivi d'un retour à la ligne
   (ou le fournit d'avance avec `--pin`). Délai : 120 secondes.
3. Ma Passerelle émet `paired`.

```json
{"event":"searching","target":"Salle 12"}
{"event":"pin_required","id":"A1B2C3D4E5F6","name":"Salle 12"}
{"event":"paired","id":"A1B2C3D4E5F6","name":"Salle 12"}
```

### `unpair --to <nom|id>`

Oublie le jumelage. Émet `unpaired`. L'appareil n'a pas besoin d'être allumé si on donne
son identifiant.

### `mirror --to <nom|id> [options]`

Partage un écran ou une fenêtre jusqu'à ce qu'on l'arrête. Sans précision, c'est l'écran principal.

Quoi partager (une seule de ces options) :

| Option | Sens |
|---|---|
| `--screen N` | l'écran numéro `N` de la liste `screens` |
| `--window ID` | la fenêtre d'identifiant `ID` de la liste `windows` |
| `--window-title TEXTE` | la première fenêtre dont le titre contient `TEXTE` |

Une fenêtre est envoyée dans une image 16:9, centrée, avec des bandes noires autour. On peut
la redimensionner ou la déplacer pendant le partage. Si on la ferme, le partage s'arrête
(`reason` = `source_closed`). Réduite dans la barre des tâches, elle reste figée sur sa
dernière image.

Réglages :

| Option | Défaut | Sens |
|---|---|---|
| `--resolution R` | `auto` | taille maximale de l'image : `720`, `1080`, `2160` (4K) ou `auto` (= 1080) |
| `--fps N` | 30 | images par seconde : 30 ou 60 (toute valeur de 5 à 60 est acceptée) |
| `--mbps N` | 0 | débit vidéo en Mbit/s ; 0 = choisi automatiquement |
| `--no-audio` | | n'envoie pas le son du PC |
| `--no-cursor` | | n'affiche pas le pointeur de la souris |
| `--backend B` | `graphics` | méthode de capture ; `duplication` est l'ancienne méthode, pour un écran seulement et sans pointeur, à essayer si l'image reste noire |
| `--minutes M` | | arrêt automatique après `M` minutes, vérifié toutes les 10 secondes |
| `--no-stdin` | | n'écoute pas l'entrée standard (arrêt par `--minutes` ou Ctrl+C) |

Événements, dans l'ordre :

```json
{"event":"searching","target":"Salle 12"}
{"event":"connecting","id":"A1B2C3D4E5F6","name":"Salle 12","address":"192.168.1.20"}
{"event":"stage","message":"..."}
{"event":"streaming","id":"A1B2C3D4E5F6","name":"Salle 12","source":"screen","source_name":"\\\\.\\DISPLAY1","audio":true}
{"event":"video","width":1920,"height":1080,"fps":30,"mbps":6,"encoder":"H.264 hardware (NVIDIA H.264 Encoder MFT)"}
{"event":"status","frames":298,"seconds":10,"audio":true}
{"event":"stopped","reason":"command","frames":912,"seconds":31}
```

- `stage` : étapes techniques de la connexion et de la capture. Le texte est destiné au
  journal, pas à l'affichage ; il peut changer d'une version à l'autre.
- `streaming` : l'image part vers le téléviseur. C'est le moment d'afficher « partage en cours ».
- `video` : la taille réellement envoyée, le débit et l'encodeur utilisé. Un écran n'est jamais agrandi :
  un écran 1080p avec `--resolution 2160` reste envoyé en 1080p.
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
| `disconnected` | le téléviseur a coupé (veille, extinction, autre source) ; précédé d'un `error` `disconnected` |
| `source_closed` | la fenêtre partagée a été fermée, ou l'écran débranché |
| `capture_failed` | la capture d'écran a échoué ; précédé d'un `error` `capture_failed` |
| `stalled` | plus aucune image envoyée depuis 20 secondes ; précédé d'un `error` `stream_stalled` |
| `error` | le partage a échoué en cours de route ; suivi d'un `error` qui dit pourquoi |

Si le programme appelant se ferme ou plante, l'entrée standard se ferme et le partage s'arrête
tout seul. **Attention :** lancé avec une entrée standard vide ou redirigée depuis `NUL`,
le partage s'arrête donc immédiatement ; utiliser `--no-stdin` dans ce cas.

## Erreurs

```json
{"event":"error","code":"not_paired","message":"Salle 12 n'est pas encore jumelé : lancer d'abord « pair »"}
```

`message` est une phrase en français, affichable telle quelle. `code` est stable.

| Code de sortie | `code` | Sens |
|---|---|---|
| 0 | | réussite |
| 1 | `not_supported`, `capture_failed` | l'appareil n'accepte pas le partage d'écran, ou la capture a échoué |
| 2 | `usage` | commande ou option incorrecte |
| 3 | `not_found`, `source_not_found` | appareil introuvable sur le réseau ; écran ou fenêtre introuvable |
| 4 | `not_paired` | aucun jumelage pour cet appareil |
| 5 | `pairing_failed`, `pin_missing` | code refusé, ou non fourni à temps |
| 6 | `connection_failed`, `disconnected`, `stream_stalled` | connexion impossible, ou partage interrompu |

`unknown_command` signale une ligne non reconnue sur l'entrée standard ; le partage continue.

## Jumelages

Les jumelages sont enregistrés dans `%APPDATA%\MaPasserelle\credentials.dat`, chiffrés pour
le compte Windows courant (DPAPI). Ils ne sont utilisables ni par un autre compte, ni sur un
autre PC.

Si un jumelage existe déjà pour l'appareil dans WinPlay sur le même compte
(`%APPDATA%\WinPlay`), Ma Passerelle le reprend à la première utilisation. Conséquence :
après un `unpair`, ce jumelage WinPlay serait repris de nouveau.

## Limites connues

- Un seul écran ou une seule fenêtre à la fois ; pas de zone libre ni de bureau étendu sur plusieurs écrans.
- Le choix d'un écran autre que le principal et la 4K réelle n'ont pas encore été vérifiés (PC de test à un seul écran 1080p).
- Sur Windows 10, un liseré jaune entoure la zone partagée ; Windows 11 permet de le retirer.
- Un seul téléviseur par lancement.
- La coupure par le téléviseur (`disconnected`) est reconnue à partir des messages de la bibliothèque WinPlay ; à revoir à chaque mise à jour de celle-ci.
