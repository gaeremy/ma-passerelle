# Provenance

Ce dossier est une copie de [WinPlay](https://github.com/dineshdhotrad/WinPlay),
© 2026 Dinesh Dhotrad, GPL-3.0-or-later.

| | |
|---|---|
| Version | 0.1.1 |
| Branche | `master` |
| Date de reprise | 9 novembre 2026 (archive zip de la branche ; le commit exact n'a pas été noté) |
| Modifications | aucune dans le code ; seule la version de .NET visée par les deux fichiers `.csproj` passe de 8 à 10 (11 novembre 2026, fin du support de .NET 8) |

## Ce qui est repris

- `src/WinPlay.Core`
- `tests/WinPlay.Core.Tests`
- `LICENSE`, `THIRD_PARTY_NOTICES.md`, `Directory.Build.props`, `.editorconfig`

## Ce qui a quitté ce dossier

`src/WinPlay.Capture` a d'abord été repris ici tel quel, puis déplacé le 10 novembre 2026 dans
`src/MaPasserelle.Capture` pour y être modifié (voir l'historique git de ce dossier) :

- espace de noms `MaPasserelle.Capture` ;
- `ScreenMirrorSource` devient `MirrorSource`, avec le choix de l'écran ou d'une fenêtre,
  une résolution plafonnée et un signal de fin de source ;
- nouvelle capture par Windows.Graphics.Capture (`GraphicsCapture`), avec le pointeur ;
- `DesktopDuplicator` accepte n'importe quel écran ;
- `GpuColorConverter` garde les proportions de la source (bandes noires) ;
- nouveau `DesktopCatalog` qui liste écrans et fenêtres.

Les encodeurs H.264 (`HardwareH264Encoder`, `MediaFoundationH264Encoder`) sont inchangés,
à l'espace de noms près.

## Ce qui n'est pas repris

L'application graphique (`src/WinPlay.App`), l'outil de test (`tools/WinPlay.DiscoveryCli`),
l'installeur, la documentation et les images de WinPlay.

## Mettre à jour

Remplacer les deux dossiers repris par ceux de la nouvelle version, relancer les tests,
puis mettre à jour ce fichier. Toute modification locale doit être listée ici, avec sa date.
