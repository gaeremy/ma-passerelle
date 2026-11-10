# Crédits et licences tierces

Ma Passerelle est distribué sous **GPL-3.0-or-later** (voir [LICENSE](LICENSE)).

## WinPlay — GPL-3.0-or-later

- Source : <https://github.com/dineshdhotrad/WinPlay> (© 2026 Dinesh Dhotrad)
- Utilisé dans : `vendor/WinPlay/**`
- Les bibliothèques `WinPlay.Core` (protocole, jumelage, chiffrement) et `WinPlay.Capture`
  (capture d'écran et encodage H.264), ainsi que les tests de `WinPlay.Core`, sont repris
  **sans modification**. La version et la date de reprise sont notées dans
  [vendor/WinPlay/UPSTREAM.md](vendor/WinPlay/UPSTREAM.md).

WinPlay intègre lui-même du code dérivé d'autres projets (doubletake, LGPL-3.0-or-later ;
libairptp d'OwnTone, MIT) et dépend de paquets NuGet sous licence MIT. Ses crédits complets
sont conservés dans [vendor/WinPlay/THIRD_PARTY_NOTICES.md](vendor/WinPlay/THIRD_PARTY_NOTICES.md).

## Code propre à Ma Passerelle

`src/MaPasserelle/**`, `docs/**` et `assets/**` : © 2026 Gaëtan Remy, GPL-3.0-or-later.
Le programme `src/MaPasserelle/Program.cs` s'inspire de l'outil de test
`tools/WinPlay.DiscoveryCli` de WinPlay.
