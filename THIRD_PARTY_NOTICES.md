# Crédits et licences tierces

Ma Passerelle est distribué sous **GPL-3.0-or-later** (voir [LICENSE](LICENSE)).

## WinPlay — GPL-3.0-or-later

- Source : <https://github.com/dineshdhotrad/WinPlay> (© 2026 Dinesh Dhotrad)
- Utilisé dans : `vendor/WinPlay/**` et `src/MaPasserelle.Capture/**`
- La bibliothèque `WinPlay.Core` (protocole, jumelage, chiffrement) et ses tests sont repris
  **sans modification** dans `vendor/WinPlay`.
- `src/MaPasserelle.Capture` (capture d'écran et encodage H.264) est **dérivé** de
  `WinPlay.Capture` et modifié pour Ma Passerelle à partir du 10 novembre 2026.
- La version reprise et la liste des modifications sont dans
  [vendor/WinPlay/UPSTREAM.md](vendor/WinPlay/UPSTREAM.md).

WinPlay intègre lui-même du code dérivé d'autres projets (doubletake, LGPL-3.0-or-later ;
libairptp d'OwnTone, MIT) et dépend de paquets NuGet sous licence MIT. Ses crédits complets
sont conservés dans [vendor/WinPlay/THIRD_PARTY_NOTICES.md](vendor/WinPlay/THIRD_PARTY_NOTICES.md).

## Code propre à Ma Passerelle

`src/MaPasserelle/**`, `docs/**`, `assets/**` et les modifications de `src/MaPasserelle.Capture/**` :
© 2026 Gaëtan Remy, GPL-3.0-or-later.
Le programme `src/MaPasserelle/Program.cs` s'inspire de l'outil de test
`tools/WinPlay.DiscoveryCli` de WinPlay.
