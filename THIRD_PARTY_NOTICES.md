# Crédits et licences tierces

Ma Passerelle est distribué sous **GPL-3.0-or-later** (voir [LICENSE](LICENSE)).

## WinPlay — GPL-3.0-or-later

- Source : <https://github.com/dineshdhotrad/WinPlay> (© 2026 Dinesh Dhotrad)
- Utilisé dans : `vendor/WinPlay/**` et `src/MaPasserelle.Capture/**`
- La bibliothèque `WinPlay.Core` (protocole, jumelage, chiffrement) et ses tests sont repris
  dans `vendor/WinPlay`. Aucun fichier repris n'est modifié. Deux fichiers y sont ajoutés
  (ChaCha20-Poly1305 pour Windows 10, et ses tests).
- `src/MaPasserelle.Capture` (capture d'écran et encodage H.264) est **dérivé** de
  `WinPlay.Capture` et modifié pour Ma Passerelle à partir du 10 novembre 2026.
- La version reprise et la liste des modifications sont dans
  [vendor/WinPlay/UPSTREAM.md](vendor/WinPlay/UPSTREAM.md).

WinPlay intègre lui-même du code dérivé d'autres projets et dépend de paquets NuGet sous licence MIT.

- **doubletake** (© omarroth, LGPL-3.0-or-later, <https://github.com/omarroth/doubletake>) :
  le module FairPlay de `WinPlay.Core/Fairplay` est un portage en C# de son implémentation.
- **libairptp** d'OwnTone (MIT) : la construction des messages PTP.
- **BouncyCastle** (MIT) : cryptographie, dont ChaCha20-Poly1305 sous Windows 10.

 Ses crédits complets
sont conservés dans [vendor/WinPlay/THIRD_PARTY_NOTICES.md](vendor/WinPlay/THIRD_PARTY_NOTICES.md).

## Code propre à Ma Passerelle

`src/MaPasserelle/**`, `docs/**`, `assets/**` et les modifications de `src/MaPasserelle.Capture/**` :
© 2026 Gaëtan Remy, GPL-3.0-or-later.
Le programme `src/MaPasserelle/Program.cs` s'inspire de l'outil de test
`tools/WinPlay.DiscoveryCli` de WinPlay.
