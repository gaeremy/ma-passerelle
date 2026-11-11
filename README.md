# Ma Passerelle

<img src="assets/icon/ma-passerelle-128.png" alt="Icône de Ma Passerelle" width="96" align="right">

Partage l'écran d'un PC Windows sur un téléviseur **compatible Apple TV**, image et son.
Compagnon de Mon Estrade, utilisable aussi tout seul.

Ma Passerelle est un programme en ligne de commande. Il trouve les téléviseurs du réseau,
se jumelle une fois avec un code affiché à l'écran, puis envoie un écran ou une fenêtre,
pointeur de la souris compris.
L'encodage vidéo utilise la carte graphique quand elle le permet (Intel, NVIDIA ou AMD),
sans réglage.

## Deux façons de s'en servir

- **L'application** (`MaPasserelle.exe`) se loge dans la zone de notification, près de l'horloge.
  Un clic sur son icône ouvre un panneau : choisir le téléviseur, l'écran ou la fenêtre, puis
  « Partager ». Le jumelage se fait depuis ce même panneau, une seule fois par téléviseur.
  Un clic droit sur l'icône permet de partager, d'arrêter ou de quitter.
- **La ligne de commande** (`mapasserelle.exe`), décrite ci-dessous, sert aux autres programmes
  (Mon Estrade) et aux essais.

## Utilisation en ligne de commande

```
mapasserelle discover
mapasserelle pair --to "Séjour"
mapasserelle mirror --to "Séjour"
mapasserelle mirror --to "Séjour" --window-title "Mon Estrade" --resolution 720
```

Pendant le partage, taper `stop` puis Entrée pour arrêter, ou `status` pour voir où on en est.
`mapasserelle help` liste toutes les options.

Pour piloter Ma Passerelle depuis un autre programme, ajouter `--json` :
chaque événement sort alors sur une ligne JSON. Le contrat complet est dans
[docs/CONTRAT.md](docs/CONTRAT.md).

## Compiler

Il faut Windows 10 ou 11 (x64 ou ARM64) et le SDK .NET 10.

```
dotnet build src/MaPasserelle/MaPasserelle.csproj -c Release -p:Platform=x64
dotnet test vendor/WinPlay/tests/WinPlay.Core.Tests/WinPlay.Core.Tests.csproj -c Release
```

L'exécutable est produit dans `src/MaPasserelle/bin/x64/Release/net10.0-windows10.0.22621.0/win-x64/`. Il est autonome : .NET y est embarqué, rien à installer sur le PC qui le lance.

## Organisation du dépôt

| Dossier | Contenu |
|---|---|
| `src/MaPasserelle` | Le programme : commandes, événements, gestion des jumelages. |
| `src/MaPasserelle.Capture` | La capture d'écran et de fenêtre et l'encodage vidéo, dérivés de WinPlay. |
| `vendor/WinPlay` | Le protocole, repris tel quel de [WinPlay](https://github.com/dineshdhotrad/WinPlay). Voir [UPSTREAM.md](vendor/WinPlay/UPSTREAM.md). |
| `assets/icon` | L'icône (SVG, PNG, ICO). |
| `docs` | Le contrat de pilotage. |

## Licence

Ma Passerelle est un logiciel libre distribué sous licence **GPL-3.0-or-later**
(voir [LICENSE](LICENSE)). Il s'appuie sur WinPlay de Dinesh Dhotrad, sous la même licence ;
les crédits détaillés sont dans [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Ma Passerelle est un programme indépendant. Mon Estrade le lance comme un processus séparé
et échange avec lui uniquement par la ligne de commande et les flux standard.

Apple TV et AirPlay sont des marques d'Apple Inc. Ce projet n'est ni affilié à Apple,
ni approuvé par Apple, et ne contient aucun code ni aucune clé d'Apple.
