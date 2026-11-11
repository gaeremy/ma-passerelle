// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Core.Hap;

namespace MaPasserelle.Engine;

/// <summary>
/// Jumelages de Ma Passerelle (%APPDATA%\MaPasserelle), protégés par DPAPI pour l'utilisateur courant.
/// Un jumelage déjà fait avec WinPlay sur ce compte est repris tel quel à la première utilisation.
/// </summary>
public sealed class Credentials
{
    private readonly CredentialStore _own = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MaPasserelle", "credentials.dat"));

    public HapPairingCredentials? Load(string deviceId)
    {
        if (_own.Load(deviceId) is { } mine) return mine;
        if (new CredentialStore().Load(deviceId) is not { } imported) return null;
        _own.Save(deviceId, imported);
        return imported;
    }

    public bool IsPaired(string deviceId) => Load(deviceId) is not null;

    public void Save(string deviceId, HapPairingCredentials credentials) => _own.Save(deviceId, credentials);

    public void Remove(string deviceId) => _own.Remove(deviceId);
}
